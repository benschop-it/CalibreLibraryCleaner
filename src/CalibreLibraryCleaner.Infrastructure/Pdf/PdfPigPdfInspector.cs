using System.Buffers;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Tokens;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

internal sealed partial class PdfPigPdfInspector : IPdfInspector
{
    private const int BufferSize = 128 * 1024;

    public async Task<PdfInspectionResult> InspectAsync(
        PdfInspectionRequest request,
        Func<PdfDocumentHeaderFacts, CancellationToken, ValueTask<IReadOnlyList<int>>> selectPages,
        IProgress<PdfInspectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectPages);
        request.Limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidatePath(request, out PdfInspectionProblemCode pathProblem))
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, pathProblem);
        }

        try
        {
            FileInfo initial = new(request.FullPath);
            if (!initial.Exists)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.MissingFile);
            }

            if (initial.Length == 0)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ZeroLength);
            }

            if (initial.Length > request.Limits.MaximumFileBytes)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.StreamLimitExceeded, PdfOpenStatus.ResourceLimited);
            }

            if (!MatchesObservation(initial, request))
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ChangedFile);
            }

            bool fileSizeWarning = initial.Length > request.Limits.FileWarningBytes;
            progress?.Report(new("Preflight", 0, 0, fileSizeWarning));
            await using FileStream stream = OpenRead(request.FullPath);
            RawPreflightFacts raw = await InspectRawFileAsync(stream, request, cancellationToken).ConfigureAwait(false);
            if (raw.Problem is { } preflightProblem)
            {
                return PdfInspectionResult.Failed(
                    request.BookId,
                    request.ExpectedRelativePath,
                    preflightProblem,
                    preflightProblem is PdfInspectionProblemCode.ObjectLimitExceeded or PdfInspectionProblemCode.StreamLimitExceeded
                        ? PdfOpenStatus.ResourceLimited
                        : PdfOpenStatus.Unreadable,
                    preflightProblem == PdfInspectionProblemCode.UnsupportedEncryption
                        ? PdfEncryptionStatus.UnsupportedEncryption
                        : PdfEncryptionStatus.Unknown);
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(raw.Sha256),
                    Convert.FromHexString(request.Fingerprint.Sha256.Value)))
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ChangedFile);
            }

            stream.Position = 0;
            QuotaFilterProvider filterProvider = new(request.Limits);
            ParsingOptions options = new()
            {
                UseLenientParsing = false,
                SkipMissingFonts = false,
                ClipPaths = false,
                UseActualText = true,
                MaxStackDepth = request.Limits.MaximumStackDepth,
                Logger = SilentPdfPigLog.Instance,
                FilterProvider = filterProvider,
            };

            using PdfDocument document = PdfDocument.Open(stream, options);
            int objectCount = document.Structure.CrossReferenceTable.ObjectOffsets.Count;
            if (objectCount > request.Limits.MaximumObjects)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ObjectLimitExceeded, PdfOpenStatus.ResourceLimited);
            }

            PdfActiveContentSummary activeContent = ReadActiveContent(document, request.Limits, cancellationToken);
            int pageCount = document.NumberOfPages;
            if (pageCount == 0)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ZeroPages);
            }

            if (pageCount > request.Limits.MaximumPages)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.PageLimitExceeded, PdfOpenStatus.ResourceLimited);
            }

            OutlineFacts outline = ReadOutline(document, request.Limits, cancellationToken);
            if (outline.Problem is { } outlineProblem)
            {
                return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, outlineProblem, PdfOpenStatus.ResourceLimited);
            }

            PdfDocumentHeaderFacts header = new(request.BookId, request.ExpectedRelativePath, pageCount, outline.TargetPages);
            IReadOnlyList<int> selected = await selectPages(header, cancellationToken).ConfigureAwait(false);
            int[] selectedPages = ValidateSelection(selected, pageCount, request.Limits.MaximumSampledPages);

            MetadataFacts metadata = ReadMetadata(document.Information, request.Limits);
            if (metadata.LimitExceeded)
            {
                return PdfInspectionResult.Failed(
                    request.BookId,
                    request.ExpectedRelativePath,
                    PdfInspectionProblemCode.MetadataLimitExceeded,
                    PdfOpenStatus.ResourceLimited);
            }

            List<PdfIdentifierEvidence> identifiers = [];
            int invalidIdentifiers = 0;
            FindIsbns(metadata.IdentifierText, PdfIdentifierSource.DocumentInformation, null, request.Limits.MaximumRetainedIdentifiers, identifiers, ref invalidIdentifiers);

            List<PdfPageFacts> pages = new(selectedPages.Length);
            List<PdfInspectionProblem> problems = [];
            XmpReadResult xmp = ReadXmp(document, request.Limits, cancellationToken);
            if (xmp.Problem is PdfInspectionProblemCode.MetadataLimitExceeded or PdfInspectionProblemCode.StackDepthExceeded)
            {
                return PdfInspectionResult.Failed(
                    request.BookId,
                    request.ExpectedRelativePath,
                    xmp.Problem.Value,
                    PdfOpenStatus.ResourceLimited);
            }

            if (xmp.Problem == PdfInspectionProblemCode.MalformedMetadata)
            {
                problems.Add(new(PdfInspectionProblemCode.MalformedMetadata));
            }

            bool documentResourceWarning = raw.ResourceWarning || objectCount > request.Limits.ObjectWarningCount || outline.ResourceWarning
                || metadata.ResourceWarning || xmp.ResourceWarning || filterProvider.SoftLimitExceeded;
            progress?.Report(new("Pages", 0, selectedPages.Length, fileSizeWarning || documentResourceWarning));
            FindIsbns(xmp.IdentifierText, PdfIdentifierSource.XmpMetadata, null, request.Limits.MaximumRetainedIdentifiers, identifiers, ref invalidIdentifiers);
            int totalUsefulCharacters = 0;
            int totalImages = 0;
            int totalOperations = 0;
            StringBuilder earlyText = new(Math.Min(request.Limits.MaximumEarlyIdentifierCharacters, 4096));

            for (int index = 0; index < selectedPages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageNumber = selectedPages[index];
                try
                {
                    Page page = document.GetPage(pageNumber);
                    PageReadResult pageResult = ReadPage(
                        document,
                        page,
                        request.Limits,
                        totalUsefulCharacters,
                        totalImages,
                        totalOperations,
                        earlyText,
                        cancellationToken);
                    pages.Add(pageResult.Facts);
                    totalUsefulCharacters = checked(totalUsefulCharacters + pageResult.Facts.UsefulCharacterCount);
                    totalImages = checked(totalImages + pageResult.Facts.ImageCount);
                    totalOperations = checked(totalOperations + pageResult.Facts.NontrivialOperationCount);
                    documentResourceWarning |= pageResult.Facts.ResourceHeavy
                        || totalUsefulCharacters > request.Limits.UsefulCharactersPerSampleWarning
                        || totalImages > request.Limits.ImagesPerSampleWarning
                        || filterProvider.SoftLimitExceeded;
                    if (pageNumber <= 5 && pageResult.EarlyText.Length > 0 && earlyText.Length < request.Limits.MaximumEarlyIdentifierCharacters)
                    {
                        int remaining = request.Limits.MaximumEarlyIdentifierCharacters - earlyText.Length;
                        earlyText.Append(pageResult.EarlyText.AsSpan(0, Math.Min(remaining, pageResult.EarlyText.Length)));
                        FindIsbns(pageResult.EarlyText, PdfIdentifierSource.EarlyPageText, pageNumber, request.Limits.MaximumRetainedIdentifiers, identifiers, ref invalidIdentifiers);
                    }
                }
                catch (PdfPageLimitException exception)
                {
                    problems.Add(new(exception.Code, pageNumber));
                    pages.Add(UnreadablePage(pageNumber, countersCapped: true));
                }
                catch (PdfInspectionLimitException exception)
                {
                    problems.Add(new(exception.Code, pageNumber));
                    pages.Add(UnreadablePage(pageNumber, countersCapped: true));
                }
                catch (PdfUnsupportedFilterException)
                {
                    problems.Add(new(PdfInspectionProblemCode.UnsupportedFilter, pageNumber));
                    pages.Add(UnreadablePage(pageNumber, countersCapped: false, fontOrFilterUnsupported: true));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsFontFailure(exception))
                {
                    problems.Add(new(PdfInspectionProblemCode.MalformedFont, pageNumber));
                    pages.Add(UnreadablePage(pageNumber, countersCapped: false, fontOrFilterUnsupported: true));
                }
                catch (Exception exception) when (IsParserFailure(exception))
                {
                    problems.Add(new(PdfInspectionProblemCode.PageUnreadable, pageNumber));
                    pages.Add(UnreadablePage(pageNumber, countersCapped: false));
                }

                progress?.Report(new("Pages", index + 1, selectedPages.Length, fileSizeWarning || documentResourceWarning));
            }

            if (totalUsefulCharacters > request.Limits.MaximumUsefulCharactersPerSample)
            {
                problems.Add(new(PdfInspectionProblemCode.StreamLimitExceeded));
            }

            if (totalImages > request.Limits.MaximumImagesPerSample)
            {
                problems.Add(new(PdfInspectionProblemCode.ImageLimitExceeded));
            }

            if (totalOperations > request.Limits.MaximumOperationsPerSample)
            {
                problems.Add(new(PdfInspectionProblemCode.OperationLimitExceeded));
            }

            EnsureUnchanged(initial, request);
            PdfEncryptionStatus encryption = document.IsEncrypted
                ? PdfEncryptionStatus.EncryptedAccessibleWithEmptyPassword
                : PdfEncryptionStatus.NotEncrypted;
            bool incomplete = problems.Count > 0 || pages.Any(page => !page.Parsed);
            return new(
                request.BookId,
                request.ExpectedRelativePath,
                PdfOpenStatus.Opened,
                encryption,
                pageCount,
                document.Version.ToString(CultureInfo.InvariantCulture),
                metadata.Summary,
                outline.Present,
                outline.Count,
                activeContent,
                selectedPages,
                pages,
                identifiers.DistinctBy(value => (value.NormalizedIsbn, value.Source, value.PageNumber)).Take(request.Limits.MaximumRetainedIdentifiers).ToArray(),
                invalidIdentifiers,
                objectCount,
                filterProvider.AggregateDecodedBytes,
                !documentResourceWarning,
                incomplete,
                problems.Take(request.Limits.MaximumRetainedFindings).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            return PdfInspectionResult.Failed(
                request.BookId,
                request.ExpectedRelativePath,
                PdfInspectionProblemCode.PasswordRequired,
                PdfOpenStatus.Unreadable,
                PdfEncryptionStatus.PasswordRequired);
        }
        catch (FileNotFoundException)
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.MissingFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.InaccessibleFile);
        }
        catch (PdfChangedDuringInspectionException)
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.ChangedFile);
        }
        catch (PdfFileAccessException)
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.InaccessibleFile);
        }
        catch (PdfInspectionLimitException exception)
        {
            return PdfInspectionResult.Failed(
                request.BookId,
                request.ExpectedRelativePath,
                exception.Code,
                PdfOpenStatus.ResourceLimited);
        }
        catch (Exception exception) when (exception is IOException || IsParserFailure(exception))
        {
            return PdfInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.MalformedStructure);
        }
    }

    private static PageReadResult ReadPage(
        PdfDocument document,
        Page page,
        PdfInspectionLimits limits,
        int priorUsefulCharacters,
        int priorImages,
        int priorOperations,
        StringBuilder retainedEarlyText,
        CancellationToken cancellationToken)
    {
        double width = Math.Abs(page.Width);
        double height = Math.Abs(page.Height);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || width > limits.MaximumPageDimensionPoints || height > limits.MaximumPageDimensionPoints)
        {
            throw new PdfPageLimitException(PdfInspectionProblemCode.DimensionLimitExceeded);
        }

        long widthMilliPoints = checked((long)Math.Round(width * 1000, MidpointRounding.AwayFromZero));
        long heightMilliPoints = checked((long)Math.Round(height * 1000, MidpointRounding.AwayFromZero));
        if (widthMilliPoints > int.MaxValue || heightMilliPoints > int.MaxValue)
        {
            throw new PdfPageLimitException(PdfInspectionProblemCode.DimensionLimitExceeded);
        }

        int operationCount = page.Operations.Count;
        if (operationCount > limits.MaximumOperationsPerPage || priorOperations > limits.MaximumOperationsPerSample - operationCount)
        {
            throw new PdfPageLimitException(PdfInspectionProblemCode.OperationLimitExceeded);
        }

        int usefulCharacters = 0;
        double glyphArea = 0;
        StringBuilder fingerprintText = new(Math.Min(limits.MaximumFingerprintCharactersPerPage, 4096));
        StringBuilder earlyText = new(Math.Min(limits.MaximumEarlyIdentifierCharacters, 1024));
        bool fingerprintTruncated = false;
        bool separatorPending = false;
        bool hiddenTextPresent = false;
        int letterIndex = 0;
        foreach (Letter letter in page.Letters)
        {
            if ((letterIndex++ & 0xfff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string value = letter.Value;
            hiddenTextPresent |= letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip;
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(rune))
                {
                    usefulCharacters++;
                    Rune upper = Rune.ToUpperInvariant(rune);
                    int required = upper.Utf16SequenceLength + (separatorPending && fingerprintText.Length > 0 ? 1 : 0);
                    if (!fingerprintTruncated && fingerprintText.Length <= limits.MaximumFingerprintCharactersPerPage - required)
                    {
                        if (separatorPending && fingerprintText.Length > 0)
                        {
                            fingerprintText.Append(' ');
                        }

                        fingerprintText.Append(upper.ToString());
                    }
                    else
                    {
                        fingerprintTruncated = true;
                    }

                    separatorPending = false;
                }
                else if (fingerprintText.Length > 0)
                {
                    separatorPending = true;
                }
            }

            foreach (char character in value)
            {
                if (page.Number <= 5
                    && retainedEarlyText.Length + earlyText.Length < limits.MaximumEarlyIdentifierCharacters
                    && !char.IsControl(character))
                {
                    earlyText.Append(character);
                }
            }

            double glyphWidth = Math.Abs(letter.BoundingBox.Width);
            double glyphHeight = Math.Abs(letter.BoundingBox.Height);
            if (double.IsFinite(glyphWidth) && double.IsFinite(glyphHeight))
            {
                glyphArea += glyphWidth * glyphHeight;
            }

            if (usefulCharacters > limits.MaximumUsefulCharactersPerPage
                || priorUsefulCharacters > limits.MaximumUsefulCharactersPerSample - usefulCharacters)
            {
                throw new PdfPageLimitException(PdfInspectionProblemCode.StreamLimitExceeded);
            }
        }

        int imageCount = 0;
        int maximumImageCoverage = 0;
        long aggregateImageCoverage = 0;
        string? dominantGeometry = null;
        bool imageResourceWarning = false;
        foreach (IPdfImage image in page.GetImages())
        {
            if ((imageCount & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            imageCount++;
            if (imageCount > limits.MaximumImagesPerPage || priorImages > limits.MaximumImagesPerSample - imageCount)
            {
                throw new PdfPageLimitException(PdfInspectionProblemCode.ImageLimitExceeded);
            }

            int imageWidthSamples = image.WidthInSamples;
            int imageHeightSamples = image.HeightInSamples;
            if (imageWidthSamples <= 0 || imageHeightSamples <= 0
                || imageWidthSamples > limits.MaximumImageDimensionSamples
                || imageHeightSamples > limits.MaximumImageDimensionSamples)
            {
                throw new PdfPageLimitException(PdfInspectionProblemCode.ImageLimitExceeded);
            }

            long pixels = checked((long)imageWidthSamples * imageHeightSamples);
            if (pixels > limits.MaximumDeclaredPixelsPerImage)
            {
                throw new PdfPageLimitException(PdfInspectionProblemCode.ImageLimitExceeded);
            }

            imageResourceWarning |= imageWidthSamples > limits.ImageDimensionSamplesWarning
                || imageHeightSamples > limits.ImageDimensionSamplesWarning
                || pixels > limits.DeclaredPixelsPerImageWarning;

            double imageWidth = Math.Abs(image.BoundingBox.Width);
            double imageHeight = Math.Abs(image.BoundingBox.Height);
            int coverage = BasisPoints(imageWidth * imageHeight, width * height);
            maximumImageCoverage = Math.Max(maximumImageCoverage, coverage);
            aggregateImageCoverage = Math.Min(10_000, aggregateImageCoverage + coverage);
            if (coverage == maximumImageCoverage)
            {
                dominantGeometry = FormattableString.Invariant($"{Math.Round(imageWidth / width, 3):0.000}x{Math.Round(imageHeight / height, 3):0.000}@{image.WidthInSamples}x{image.HeightInSamples}");
            }
        }

        int glyphCoverage = BasisPoints(glyphArea, width * height);
        string? fingerprint = fingerprintText.Length == 0
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText.ToString()))).ToLowerInvariant()[..32];
        bool fingerprintComplete = !fingerprintTruncated;
        bool fontEvidenceReliable = HasReliableEmbeddedFonts(document, page);
        bool resourceHeavy = operationCount > limits.OperationsPerPageWarning
            || imageCount > limits.ImagesPerPageWarning
            || usefulCharacters > limits.UsefulCharactersPerPageWarning
            || imageResourceWarning;
        return new(
            new(
                page.Number,
                true,
                usefulCharacters,
                glyphCoverage,
                imageCount,
                maximumImageCoverage,
                (int)aggregateImageCoverage,
                operationCount,
                (int)widthMilliPoints,
                (int)heightMilliPoints,
                null,
                fingerprint,
                fingerprintComplete,
                null,
                dominantGeometry,
                !fingerprintComplete,
                resourceHeavy,
                FontEvidenceReliable: fontEvidenceReliable,
                HiddenTextPresent: hiddenTextPresent),
            earlyText.ToString());
    }

    private static MetadataFacts ReadMetadata(DocumentInformation information, PdfInspectionLimits limits)
    {
        List<string> identifierParts = [];
        PdfMetadataValue title = ReadMetadataValue(information.Title, limits, identifierParts);
        PdfMetadataValue author = ReadMetadataValue(information.Author, limits, identifierParts);
        PdfMetadataValue subject = ReadMetadataValue(information.Subject, limits, identifierParts);
        PdfMetadataValue keywords = ReadMetadataValue(information.Keywords, limits, identifierParts);
        PdfMetadataValue creator = ReadMetadataValue(information.Creator, limits, identifierParts);
        PdfMetadataValue producer = ReadMetadataValue(information.Producer, limits, identifierParts);
        PdfMetadataValue creation = ReadMetadataDateValue(
            information.CreationDate,
            information.GetCreatedDateTimeOffset().HasValue,
            limits,
            identifierParts);
        PdfMetadataValue modification = ReadMetadataDateValue(
            information.ModifiedDate,
            information.GetModifiedDateTimeOffset().HasValue,
            limits,
            identifierParts);
        bool limitExceeded = new[]
        {
            information.Title, information.Author, information.Subject, information.Keywords, information.Creator,
            information.Producer, information.CreationDate, information.ModifiedDate,
        }.Any(value => value is not null && Encoding.UTF8.GetByteCount(value) > limits.MaximumMetadataFieldBytes);
        bool resourceWarning = new[]
        {
            information.Title, information.Author, information.Subject, information.Keywords, information.Creator,
            information.Producer, information.CreationDate, information.ModifiedDate,
        }.Any(value => value is not null && Encoding.UTF8.GetByteCount(value) > limits.MetadataFieldWarningBytes);
        return new(
            new(title, author, subject, keywords, creator, producer, creation, modification),
            string.Join(' ', identifierParts),
            limitExceeded,
            resourceWarning);
    }

    private static XmpReadResult ReadXmp(
        PdfDocument document,
        PdfInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!document.TryGetXmpMetadata(out XmpMetadata? metadata) || metadata is null)
            {
                return new(string.Empty, null, false);
            }

            if (metadata.MetadataStreamToken.Data.Length > limits.MaximumXmpBytes)
            {
                return new(string.Empty, PdfInspectionProblemCode.MetadataLimitExceeded, false);
            }

            bool resourceWarning = metadata.MetadataStreamToken.Data.Length > limits.XmpWarningBytes;
            byte[] bytes = metadata.GetXmlBytes().ToArray();
            try
            {
                if (bytes.Length > limits.MaximumXmpBytes)
                {
                    return new(string.Empty, PdfInspectionProblemCode.MetadataLimitExceeded, resourceWarning);
                }

                XmlReaderSettings settings = new()
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = limits.MaximumXmpBytes,
                    MaxCharactersFromEntities = 1_024,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                };
                StringBuilder evidence = new(Math.Min(limits.MaximumEarlyIdentifierCharacters, 4096));
                using MemoryStream input = new(bytes, writable: false);
                using XmlReader reader = XmlReader.Create(input, settings);
                int nodes = 0;
                int allowedFieldDepth = -1;
                while (reader.Read())
                {
                    if ((nodes++ & 0xff) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (reader.Depth > limits.MaximumStackDepth)
                    {
                        return new(string.Empty, PdfInspectionProblemCode.StackDepthExceeded, true);
                    }

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (allowedFieldDepth < 0 && IsAllowedXmpIdentifierField(reader.LocalName))
                        {
                            allowedFieldDepth = reader.Depth;
                        }

                        if (allowedFieldDepth >= 0 && reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                AppendBounded(evidence, reader.Value, limits.MaximumEarlyIdentifierCharacters);
                            }

                            reader.MoveToElement();
                        }

                        if (allowedFieldDepth == reader.Depth && reader.IsEmptyElement)
                        {
                            allowedFieldDepth = -1;
                        }
                    }

                    if (allowedFieldDepth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        AppendBounded(evidence, reader.Value, limits.MaximumEarlyIdentifierCharacters);
                    }

                    if (allowedFieldDepth >= 0 && reader.NodeType == XmlNodeType.EndElement && reader.Depth == allowedFieldDepth)
                    {
                        allowedFieldDepth = -1;
                    }
                }

                return new(evidence.ToString(), null, resourceWarning || bytes.Length > limits.XmpWarningBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) when ((exception is DecoderFallbackException or XmlException) || IsParserFailure(exception))
        {
            return new(string.Empty, PdfInspectionProblemCode.MalformedMetadata, false);
        }
    }

    private static bool IsAllowedXmpIdentifierField(string localName) => localName.Equals("title", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("subject", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("keywords", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("identifier", StringComparison.OrdinalIgnoreCase);

    private static PdfActiveContentSummary ReadActiveContent(
        PdfDocument document,
        PdfInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        Stack<(IToken Token, int Depth)> pending = [];
        HashSet<IndirectReference> visitedReferences = [];
        pending.Push((document.Structure.Catalog.CatalogDictionary, 0));
        int visitedTokens = 0;
        int actions = 0;
        int externalReferences = 0;
        int embeddedFiles = 0;
        long maximumStructuralTokens = Math.Min(8_000_000L, checked((long)limits.MaximumObjects * 16));

        while (pending.TryPop(out (IToken Token, int Depth) current))
        {
            if ((visitedTokens++ & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (visitedTokens > maximumStructuralTokens)
            {
                throw new PdfInspectionLimitException(PdfInspectionProblemCode.ObjectLimitExceeded);
            }

            if (current.Depth > limits.MaximumStackDepth)
            {
                throw new PdfInspectionLimitException(PdfInspectionProblemCode.StackDepthExceeded);
            }

            switch (current.Token)
            {
                case ObjectToken objectToken:
                    pending.Push((objectToken.Data, current.Depth));
                    break;
                case IndirectReferenceToken referenceToken when visitedReferences.Add(referenceToken.Data):
                    pending.Push((document.Structure.GetObject(referenceToken.Data), current.Depth + 1));
                    break;
                case StreamToken streamToken:
                    pending.Push((streamToken.StreamDictionary, current.Depth + 1));
                    break;
                case ArrayToken arrayToken:
                    foreach (IToken token in arrayToken.Data.Reverse())
                    {
                        pending.Push((token, current.Depth + 1));
                    }

                    break;
                case DictionaryToken dictionaryToken:
                    if (dictionaryToken.TryGet(NameToken.Create("Type"), out NameToken dictionaryType)
                        && dictionaryType.Data is "Filespec" or "EmbeddedFile")
                    {
                        embeddedFiles = SaturatingIncrement(embeddedFiles);
                        break;
                    }

                    foreach (KeyValuePair<string, IToken> entry in dictionaryToken.Data.Reverse())
                    {
                        string key = entry.Key;
                        actions = key is "JS" or "JavaScript" or "AA" or "OpenAction"
                            ? SaturatingIncrement(actions)
                            : actions;
                        externalReferences = key is "URI" or "GoToR"
                            ? SaturatingIncrement(externalReferences)
                            : externalReferences;
                        embeddedFiles = key is "EmbeddedFiles" or "EmbeddedFile"
                            ? SaturatingIncrement(embeddedFiles)
                            : embeddedFiles;

                        if (!SkipActiveContentValue(key))
                        {
                            pending.Push((entry.Value, current.Depth + 1));
                        }
                    }

                    break;
                case NameToken nameToken:
                    actions = nameToken.Data is "JavaScript" or "Launch"
                        ? SaturatingIncrement(actions)
                        : actions;
                    externalReferences = nameToken.Data is "URI" or "GoToR"
                        ? SaturatingIncrement(externalReferences)
                        : externalReferences;
                    embeddedFiles = nameToken.Data is "EmbeddedFile" or "Filespec"
                        ? SaturatingIncrement(embeddedFiles)
                        : embeddedFiles;
                    break;
            }
        }

        return new(actions, externalReferences, embeddedFiles);
    }

    private static bool SkipActiveContentValue(string key) => key is
        "Contents" or "Metadata" or "XObject" or "FontFile" or "FontFile2" or "FontFile3" or "EF" or "EmbeddedFile";

    private static int SaturatingIncrement(int value) => Math.Min(10_000, value + 1);

    private static bool HasReliableEmbeddedFonts(PdfDocument document, Page page)
    {
        if (page.Letters.Count == 0)
        {
            return true;
        }

        if (!page.Dictionary.TryGet(NameToken.Create("Resources"), out IToken resourcesToken)
            || !TryResolveDictionary(document, resourcesToken, out DictionaryToken resources)
            || !resources.TryGet(NameToken.Create("Font"), out IToken fontsToken)
            || !TryResolveDictionary(document, fontsToken, out DictionaryToken fonts)
            || fonts.Data.Count == 0)
        {
            return false;
        }

        foreach (IToken fontToken in fonts.Data.Values)
        {
            if (!TryResolveDictionary(document, fontToken, out DictionaryToken font)
                || !IsEmbeddedFont(document, font))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmbeddedFont(PdfDocument document, DictionaryToken font)
    {
        if (font.TryGet(NameToken.Create("Subtype"), out NameToken subtype) && subtype.Data == "Type3")
        {
            return true;
        }

        if (font.TryGet(NameToken.Create("DescendantFonts"), out IToken descendantsToken)
            && TryResolveArray(document, descendantsToken, out ArrayToken descendants))
        {
            return descendants.Data.Count > 0 && descendants.Data.All(token =>
                TryResolveDictionary(document, token, out DictionaryToken descendant)
                && HasEmbeddedFontDescriptor(document, descendant));
        }

        return HasEmbeddedFontDescriptor(document, font);
    }

    private static bool HasEmbeddedFontDescriptor(PdfDocument document, DictionaryToken font)
    {
        return font.TryGet(NameToken.Create("FontDescriptor"), out IToken descriptorToken)
            && TryResolveDictionary(document, descriptorToken, out DictionaryToken descriptor)
            && (descriptor.ContainsKey(NameToken.Create("FontFile"))
                || descriptor.ContainsKey(NameToken.Create("FontFile2"))
                || descriptor.ContainsKey(NameToken.Create("FontFile3")));
    }

    private static bool TryResolveDictionary(PdfDocument document, IToken token, out DictionaryToken dictionary)
    {
        IToken resolved = ResolveToken(document, token);
        dictionary = resolved as DictionaryToken ?? null!;
        return resolved is DictionaryToken;
    }

    private static bool TryResolveArray(PdfDocument document, IToken token, out ArrayToken array)
    {
        IToken resolved = ResolveToken(document, token);
        array = resolved as ArrayToken ?? null!;
        return resolved is ArrayToken;
    }

    private static IToken ResolveToken(PdfDocument document, IToken token)
    {
        for (int depth = 0; depth < 4; depth++)
        {
            token = token switch
            {
                ObjectToken objectToken => objectToken.Data,
                IndirectReferenceToken referenceToken => document.Structure.GetObject(referenceToken.Data),
                _ => token,
            };
            if (token is not (ObjectToken or IndirectReferenceToken))
            {
                break;
            }
        }

        return token;
    }

    private static void AppendBounded(StringBuilder target, string value, int maximumCharacters)
    {
        if (target.Length >= maximumCharacters || string.IsNullOrEmpty(value))
        {
            return;
        }

        int available = maximumCharacters - target.Length;
        target.Append(value.AsSpan(0, Math.Min(available, value.Length)));
        if (target.Length < maximumCharacters)
        {
            target.Append(' ');
        }
    }

    private static PdfMetadataValue ReadMetadataValue(string? value, PdfInspectionLimits limits, List<string> identifierParts)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new(PdfMetadataValueStatus.Missing);
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > limits.MaximumMetadataFieldBytes)
        {
            return new(PdfMetadataValueStatus.Truncated, Truncate(value, limits.MaximumRetainedMetadataCharacters));
        }

        string retained = Truncate(value, limits.MaximumRetainedMetadataCharacters);
        identifierParts.Add(retained);
        return new(retained.Length == value.Trim().Length ? PdfMetadataValueStatus.Present : PdfMetadataValueStatus.Truncated, retained);
    }

    private static PdfMetadataValue ReadMetadataDateValue(
        string? value,
        bool parsed,
        PdfInspectionLimits limits,
        List<string> identifierParts)
    {
        PdfMetadataValue retained = ReadMetadataValue(value, limits, identifierParts);
        return retained.Status == PdfMetadataValueStatus.Present && !parsed
            ? new PdfMetadataValue(PdfMetadataValueStatus.Malformed, retained.Value)
            : retained;
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maximumCharacters ? trimmed : trimmed[..maximumCharacters];
    }

    private static OutlineFacts ReadOutline(
        PdfDocument document,
        PdfInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!document.TryGetBookmarks(out Bookmarks? bookmarks, false) || bookmarks is null)
            {
                return new(false, 0, [], null, false);
            }

            List<int> targets = [];
            int count = 0;
            bool resourceWarning = false;
            foreach (BookmarkNode node in bookmarks.GetNodes())
            {
                if ((count & 0xff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                count++;
                if (count > limits.MaximumOutlineNodes || node.Level > limits.MaximumOutlineDepth)
                {
                    return new(true, count, [], PdfInspectionProblemCode.ObjectLimitExceeded, true);
                }

                resourceWarning |= count > limits.OutlineNodeWarningCount || node.Level > limits.OutlineDepthWarning;

                if (node is DocumentBookmarkNode documentNode && documentNode.PageNumber is > 0)
                {
                    targets.Add(documentNode.PageNumber);
                }
            }

            return new(true, count, targets.Distinct().ToArray(), null, resourceWarning);
        }
        catch (Exception exception) when (IsParserFailure(exception))
        {
            return new(false, 0, [], PdfInspectionProblemCode.MalformedOutline, false);
        }
    }

    private static int[] ValidateSelection(IReadOnlyList<int> selected, int pageCount, int maximum)
    {
        ArgumentNullException.ThrowIfNull(selected);
        int[] pages = selected.Distinct().Order().ToArray();
        if (pages.Length == 0 || pages.Length > maximum || pages.Any(page => page <= 0 || page > pageCount))
        {
            throw new PdfPageSelectionException();
        }

        return pages;
    }

    private static async Task<RawPreflightFacts> InspectRawFileAsync(
        FileStream stream,
        PdfInspectionRequest request,
        CancellationToken cancellationToken)
    {
        int headerLength = (int)Math.Min(stream.Length, request.Limits.MaximumHeaderBytes);
        byte[] header = new byte[headerLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (Encoding.ASCII.GetString(header).IndexOf("%PDF-", StringComparison.Ordinal) < 0)
        {
            return RawPreflightFacts.Fail(PdfInspectionProblemCode.InvalidSignature);
        }

        int tailLength = (int)Math.Min(stream.Length, request.Limits.MaximumTailBytes);
        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        string tailText = Encoding.ASCII.GetString(tail);
        if (!tailText.Contains("%%EOF", StringComparison.Ordinal) || !tailText.Contains("startxref", StringComparison.Ordinal))
        {
            return RawPreflightFacts.Fail(PdfInspectionProblemCode.TruncatedFile);
        }

        stream.Position = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] rented = ArrayPool<byte>.Shared.Rent(BufferSize + 512);
        int carryLength = 0;
        long declaredStreams = 0;
        bool streamWarning = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(rented.AsMemory(carryLength, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(rented.AsSpan(carryLength, read));
                int totalLength = carryLength + read;
                string text = Encoding.ASCII.GetString(rented, 0, totalLength);
                if (ContainsUnsupportedStandardEncryption(text))
                {
                    return RawPreflightFacts.Fail(PdfInspectionProblemCode.UnsupportedEncryption);
                }

                foreach (Match match in LengthPattern().Matches(text))
                {
                    if (match.Index + match.Length <= carryLength)
                    {
                        continue;
                    }

                    if (!long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long length))
                    {
                        continue;
                    }

                    if (length > request.Limits.MaximumEncodedStreamBytes)
                    {
                        return RawPreflightFacts.Fail(PdfInspectionProblemCode.StreamLimitExceeded);
                    }

                    streamWarning |= length > request.Limits.EncodedStreamWarningBytes;
                    declaredStreams = checked(declaredStreams + length);
                    if (declaredStreams > request.Limits.MaximumAggregateDecodedBytes)
                    {
                        return RawPreflightFacts.Fail(PdfInspectionProblemCode.StreamLimitExceeded);
                    }

                    streamWarning |= declaredStreams > request.Limits.AggregateDecodedWarningBytes;
                }

                int nextCarryLength = Math.Min(512, totalLength);
                rented.AsSpan(totalLength - nextCarryLength, nextCarryLength).CopyTo(rented);
                carryLength = nextCarryLength;
            }

            string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            bool warning = streamWarning;
            return new(
                sha,
                declaredStreams,
                warning,
                null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void FindIsbns(
        string text,
        PdfIdentifierSource source,
        int? pageNumber,
        int maximumRetained,
        List<PdfIdentifierEvidence> identifiers,
        ref int invalidCount)
    {
        foreach (Match match in IsbnPattern().Matches(text))
        {
            int end = match.Index + match.Length;
            if (match.Index > 0 && IsIsbnCharacter(text[match.Index - 1])
                || end < text.Length && IsIsbnCharacter(text[end]))
            {
                continue;
            }

            string normalized = new(match.Value.Where(character => char.IsAsciiDigit(character) || character is 'X' or 'x').Select(char.ToUpperInvariant).ToArray());
            if (normalized.Length is not (10 or 13))
            {
                continue;
            }

            if (!IsValidIsbn(normalized))
            {
                invalidCount++;
                continue;
            }

            if (identifiers.Count < maximumRetained)
            {
                identifiers.Add(new(normalized, source, pageNumber));
            }
        }
    }

    private static bool IsIsbnCharacter(char value) => char.IsAsciiDigit(value) || value is 'X' or 'x';

    private static bool IsValidIsbn(string isbn)
    {
        if (isbn.Length == 10)
        {
            int sum = 0;
            for (int index = 0; index < 10; index++)
            {
                int digit = index == 9 && isbn[index] == 'X' ? 10 : isbn[index] - '0';
                if (digit is < 0 or > 10 || index < 9 && digit == 10)
                {
                    return false;
                }

                sum += digit * (10 - index);
            }

            return sum % 11 == 0;
        }

        if (!isbn.StartsWith("978", StringComparison.Ordinal) && !isbn.StartsWith("979", StringComparison.Ordinal))
        {
            return false;
        }

        int checksum = 0;
        for (int index = 0; index < 13; index++)
        {
            int digit = isbn[index] - '0';
            if (digit is < 0 or > 9)
            {
                return false;
            }

            checksum += digit * (index % 2 == 0 ? 1 : 3);
        }

        return checksum % 10 == 0;
    }

    private static bool TryValidatePath(PdfInspectionRequest request, out PdfInspectionProblemCode problem)
    {
        problem = PdfInspectionProblemCode.UnsafePath;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRoot));
            string expected = Path.GetFullPath(Path.Combine(root, request.ExpectedRelativePath));
            string actual = Path.GetFullPath(request.FullPath);
            string prefix = root + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!actual.StartsWith(prefix, comparison)
                || !string.Equals(actual, expected, comparison)
                || Path.IsPathRooted(request.ExpectedRelativePath))
            {
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            string relative = Path.GetRelativePath(root, actual);
            string current = root;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MatchesObservation(FileInfo info, PdfInspectionRequest request) =>
        info.Length == request.Observation.Length
        && info.Length == request.Fingerprint.SizeInBytes
        && info.LastWriteTimeUtc == request.Observation.LastWriteTimeUtc.UtcDateTime
        && (int)info.Attributes == request.Observation.Attributes;

    private static void EnsureUnchanged(FileInfo initial, PdfInspectionRequest request)
    {
        FileInfo final = new(request.FullPath);
        final.Refresh();
        if (!MatchesObservation(final, request)
            || final.Length != initial.Length
            || final.LastWriteTimeUtc != initial.LastWriteTimeUtc)
        {
            throw new PdfChangedDuringInspectionException();
        }
    }

    private static FileStream OpenRead(string path)
    {
        try
        {
            return new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException exception)
        {
            throw new PdfFileAccessException(exception);
        }
    }

    private static int BasisPoints(double numerator, double denominator)
    {
        if (!double.IsFinite(numerator) || !double.IsFinite(denominator) || numerator <= 0 || denominator <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Round(numerator / denominator * 10_000, MidpointRounding.AwayFromZero), 0, 10_000);
    }

    private static bool ContainsUnsupportedStandardEncryption(string text)
    {
        Match filter = StandardEncryptionFilterPattern().Match(text);
        if (!filter.Success)
        {
            return false;
        }

        int length = Math.Min(2_048, text.Length - filter.Index);
        string dictionaryFragment = text.Substring(filter.Index, length);
        Match version = EncryptionVersionPattern().Match(dictionaryFragment);
        Match revision = EncryptionRevisionPattern().Match(dictionaryFragment);
        return (version.Success && int.Parse(version.Groups[1].Value, CultureInfo.InvariantCulture) > 5)
            || (revision.Success && int.Parse(revision.Groups[1].Value, CultureInfo.InvariantCulture) > 6);
    }

    private static bool IsParserFailure(Exception exception) =>
        exception is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException
        || exception.GetType().Namespace?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true;

    private static bool IsFontFailure(Exception exception) =>
        exception.GetType().Name.Contains("Font", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("font", StringComparison.OrdinalIgnoreCase);

    private static PdfPageFacts UnreadablePage(
        int pageNumber,
        bool countersCapped,
        bool fontOrFilterUnsupported = false) => new(
        pageNumber, false, 0, 0, 0, 0, 0, 0, 0, 0, null, null, false, null, null,
        countersCapped, false, fontOrFilterUnsupported);

    [GeneratedRegex(@"/Length\s+(\d{1,20})(?:\s|/|>>)", RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"/Filter\s*/Standard", RegexOptions.CultureInvariant)]
    private static partial Regex StandardEncryptionFilterPattern();

    [GeneratedRegex(@"/V\s+(\d{1,3})(?:\s|/|>>)", RegexOptions.CultureInvariant)]
    private static partial Regex EncryptionVersionPattern();

    [GeneratedRegex(@"/R\s+(\d{1,3})(?:\s|/|>>)", RegexOptions.CultureInvariant)]
    private static partial Regex EncryptionRevisionPattern();

    [GeneratedRegex(@"[0-9Xx](?:[\s-]?[0-9Xx]){9,12}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IsbnPattern();

    private sealed class PdfPageLimitException(PdfInspectionProblemCode code) : Exception
    {
        public PdfInspectionProblemCode Code { get; } = code;
    }

    private sealed class PdfChangedDuringInspectionException : IOException;

    private sealed class PdfFileAccessException(Exception innerException) : IOException("The PDF could not be opened read-only.", innerException);

    private sealed record PageReadResult(PdfPageFacts Facts, string EarlyText);

    private sealed record MetadataFacts(
        PdfDocumentMetadataSummary Summary,
        string IdentifierText,
        bool LimitExceeded,
        bool ResourceWarning);

    private sealed record XmpReadResult(
        string IdentifierText,
        PdfInspectionProblemCode? Problem,
        bool ResourceWarning);

    private sealed record OutlineFacts(
        bool Present,
        int Count,
        IReadOnlyList<int> TargetPages,
        PdfInspectionProblemCode? Problem,
        bool ResourceWarning);

    private sealed record RawPreflightFacts(
        string Sha256,
        long DeclaredStreamBytes,
        bool ResourceWarning,
        PdfInspectionProblemCode? Problem)
    {
        public static RawPreflightFacts Fail(PdfInspectionProblemCode problem) => new(
            new string('0', 64), 0, false, problem);
    }

    private sealed class SilentPdfPigLog : ILog
    {
        public static SilentPdfPigLog Instance { get; } = new();

        public void Debug(string message) { }

        public void Debug(string message, Exception exception) { }

        public void Warn(string message) { }

        public void Error(string message) { }

        public void Error(string message, Exception exception) { }
    }
}

internal sealed class PdfPageSelectionException : Exception
{
}
