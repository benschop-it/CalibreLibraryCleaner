using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace CalibreLibraryCleaner.Infrastructure.Epub;

internal sealed class VersOneEpubInspector(ILogger<VersOneEpubInspector> logger) : IEpubInspector
{
    private const int BufferSize = 128 * 1024;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064B50;
    private static readonly Action<ILogger, string, Exception?> InspectionFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, nameof(InspectionFailed)),
        "EPUB inspection returned a structured failure with code {ReasonCode}");

    public async Task<EpubInspectionResult> InspectAsync(
        EpubInspectionRequest request,
        IProgress<EpubInspectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureCurrentFile(request);
            if (request.Observation.Length == 0)
            {
                return Fail(request, EpubInspectionProblemCode.CannotOpen, "The EPUB file is empty.");
            }

            if (request.Observation.Length > request.Limits.MaximumFileBytes)
            {
                return Fail(request, EpubInspectionProblemCode.LimitExceeded, "The EPUB exceeds the configured file-size limit.");
            }

            progress?.Report(new("Preflight", 0, null));
            PreflightResult preflight;
            try
            {
                preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Fail(request, EpubInspectionProblemCode.CannotOpen, "The EPUB ZIP container is invalid or unreadable.");
            }

            if (preflight.Problem is not null)
            {
                return Fail(request, preflight.Problem.Code, preflight.Problem.Explanation);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Package", 0, null));
            await ValidateWithVersOneAsync(request, cancellationToken).ConfigureAwait(false);
            EpubInspectionResult result = await ReadFactsAsync(request, progress, cancellationToken).ConfigureAwait(false);
            EnsureCurrentFile(request);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileChangedException)
        {
            return Fail(request, EpubInspectionProblemCode.ChangedDuringInspection, "The EPUB changed during inspection; partial results were discarded.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or SecurityException or IOException)
        {
            InspectionFailed(logger, "Unreadable", null);
            return Fail(request, EpubInspectionProblemCode.Unreadable, "The EPUB could not be read safely.");
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or EpubReaderException)
        {
            InspectionFailed(logger, "Malformed", null);
            return Fail(request, EpubInspectionProblemCode.PackageMalformed, "The EPUB container or package is malformed or unsupported.");
        }
        catch (OverflowException)
        {
            InspectionFailed(logger, "LimitExceeded", null);
            return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB analysis exceeded a configured numeric limit.");
        }
        catch (InspectionLimitException)
        {
            InspectionFailed(logger, "LimitExceeded", null);
            return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB analysis exceeded a configured read limit.");
        }
    }

    private static async Task<PreflightResult> PreflightAsync(EpubInspectionRequest request, CancellationToken token)
    {
        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        if (await ValidateCentralDirectoryAsync(file, request.Limits.MaximumArchiveEntries, token).ConfigureAwait(false))
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.Encrypted, "Encrypted ZIP entries are not supported for EPUB inspection.");
        }
        file.Position = 0;
        using CancellationCheckingStream guarded = new(file, token);
        using ZipArchive archive = new(guarded, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > request.Limits.MaximumArchiveEntries)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB contains too many archive entries.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        long totalLength = 0;
        long totalCompressed = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!EpubArchivePathResolver.TryNormalizeEntryName(entry.FullName, out string name) || !names.Add(name))
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.UnsafeArchive, "The EPUB contains an unsafe or duplicate archive path.");
            }

            long length = entry.Length;
            long compressed = entry.CompressedLength;
            if (length < 0 || compressed < 0 || length > request.Limits.MaximumEntryBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "An EPUB archive entry exceeds its configured limit.");
            }

            totalLength = checked(totalLength + length);
            totalCompressed = checked(totalCompressed + compressed);
            if (totalLength > request.Limits.MaximumDeclaredUncompressedBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB exceeds the aggregate uncompressed-size limit.");
            }

            if (length > 1024L * 1024
                && (compressed == 0 || length > checked(compressed * request.Limits.MaximumCompressionRatio)))
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.UnsafeArchive, "The EPUB contains a suspicious compression ratio.");
            }

            entries.Add(name, entry);
        }

        if (totalLength > 10L * 1024 * 1024
            && (totalCompressed == 0 || totalLength > checked(totalCompressed * request.Limits.MaximumAggregateCompressionRatio)))
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.UnsafeArchive, "The EPUB aggregate compression ratio is suspicious.");
        }

        if (!entries.TryGetValue("META-INF/container.xml", out ZipArchiveEntry? containerEntry))
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.PackageMalformed, "The EPUB container document is missing.");
        }

        if (containerEntry.Length > request.Limits.MaximumXmlBytes)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB container document exceeds its configured limit.");
        }

        XDocument container = await ReadXmlAsync(containerEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
        string? packageReference = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (!EpubArchivePathResolver.TryNormalizeEntryName(packageReference, out string packagePath)
            || !entries.TryGetValue(packagePath, out ZipArchiveEntry? packageEntry))
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.PackageMalformed, "The EPUB package document is missing or has an unsafe path.");
        }

        if (packageEntry.Length > request.Limits.MaximumXmlBytes)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB package document exceeds its configured limit.");
        }

        XDocument package = await ReadXmlAsync(packageEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);

        string? tocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?.Attribute("toc")?.Value;
        string[] eagerXmlReferences = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Where(element =>
                element.Attribute("properties")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.Ordinal) == true
                || string.Equals(element.Attribute("media-type")?.Value, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase)
                || tocId is not null && string.Equals(element.Attribute("id")?.Value, tocId, StringComparison.Ordinal))
            .Select(element => element.Attribute("href")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string eagerXmlReference in eagerXmlReferences)
        {
            token.ThrowIfCancellationRequested();
            if (!EpubArchivePathResolver.TryResolve(packagePath, eagerXmlReference, out string eagerXmlPath))
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.UnsafeArchive, "The EPUB navigation document has an unsafe path.");
            }

            if (!entries.TryGetValue(eagerXmlPath, out ZipArchiveEntry? eagerXmlEntry))
            {
                continue;
            }

            if (eagerXmlEntry.Length > request.Limits.MaximumXmlBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "An EPUB navigation document exceeds its configured limit.");
            }

            _ = await ReadXmlAsync(eagerXmlEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
        }

        if (entries.TryGetValue("META-INF/encryption.xml", out ZipArchiveEntry? encryptionEntry))
        {
            if (encryptionEntry.Length > request.Limits.MaximumXmlBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB encryption document exceeds its configured limit.");
            }

            XDocument encryption = await ReadXmlAsync(encryptionEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
            string[] algorithms = EncryptionAlgorithms(encryption);
            if (algorithms.Length == 0 || !algorithms.All(IsRecognizedFontObfuscation))
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.Encrypted, "Unsupported encryption or DRM prevents comparable EPUB inspection.");
            }
        }

        return new(entries.Keys.Order(StringComparer.Ordinal).ToArray(), null);
    }

    private static async Task<bool> ValidateCentralDirectoryAsync(FileStream file, int maximumEntries, CancellationToken token)
    {
        const int endRecordLength = 22;
        const int maximumCommentLength = ushort.MaxValue;
        int tailLength = checked((int)Math.Min(file.Length, endRecordLength + maximumCommentLength));
        if (tailLength < endRecordLength)
        {
            throw new InvalidDataException("The ZIP end record is missing.");
        }

        byte[] tail = GC.AllocateUninitializedArray<byte>(tailLength);
        file.Position = file.Length - tailLength;
        await file.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        int endRecordIndex = -1;
        for (int index = tail.Length - endRecordLength; index >= 0; index--)
        {
            if ((index & 0x0FFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> candidate = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) == EndOfCentralDirectorySignature
                && index + endRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..]) == tail.Length)
            {
                endRecordIndex = index;
                break;
            }
        }

        if (endRecordIndex < 0)
        {
            throw new InvalidDataException("The ZIP end record is missing or malformed.");
        }

        ReadOnlySpan<byte> endRecord = tail.AsSpan(endRecordIndex, endRecordLength);
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            throw new InvalidDataException("Split ZIP archives are not supported.");
        }

        ulong resolvedTotalEntries = totalEntries;
        ulong centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
        ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
        if (totalEntries > maximumEntries)
        {
            throw new InspectionLimitException();
        }

        if (totalEntries == ushort.MaxValue)
        {
            long endRecordOffset = file.Length - tailLength + endRecordIndex;
            if (endRecordOffset < 20)
            {
                throw new InvalidDataException("The ZIP64 locator is missing.");
            }

            byte[] locator = new byte[20];
            file.Position = endRecordOffset - locator.Length;
            await file.ReadExactlyAsync(locator, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EndOfCentralDirectoryLocatorSignature
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
            {
                throw new InvalidDataException("The ZIP64 locator is malformed.");
            }

            ulong zip64EndOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64EndOffset > (ulong)Math.Max(0, file.Length - 56))
            {
                throw new InvalidDataException("The ZIP64 end record points outside the archive.");
            }

            byte[] zip64End = new byte[56];
            file.Position = checked((long)zip64EndOffset);
            await file.ReadExactlyAsync(zip64End, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64End) != Zip64EndOfCentralDirectorySignature
                || BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(4)) < 44
                || BinaryPrimitives.ReadUInt32LittleEndian(zip64End.AsSpan(16)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(zip64End.AsSpan(20)) != 0)
            {
                throw new InvalidDataException("The ZIP64 end record is malformed.");
            }

            ulong zip64EntriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(24));
            resolvedTotalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(32));
            centralDirectorySize = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(40));
            centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(48));
            if (zip64EntriesOnDisk != resolvedTotalEntries)
            {
                throw new InvalidDataException("Split ZIP64 archives are not supported.");
            }
        }

        if (resolvedTotalEntries > (ulong)maximumEntries)
        {
            throw new InspectionLimitException();
        }

        if (centralDirectoryOffset > (ulong)file.Length
            || centralDirectorySize > (ulong)file.Length - centralDirectoryOffset)
        {
            throw new InvalidDataException("The ZIP central directory points outside the archive.");
        }

        file.Position = checked((long)centralDirectoryOffset);
        byte[] header = new byte[46];
        for (ulong index = 0; index < resolvedTotalEntries; index++)
        {
            token.ThrowIfCancellationRequested();
            await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidDataException("The ZIP central directory is malformed.");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            long nextHeader = checked(file.Position + fileNameLength + extraLength + commentLength);
            if ((ulong)nextHeader > centralDirectoryOffset + centralDirectorySize)
            {
                throw new InvalidDataException("The ZIP central directory entry exceeds its declared bounds.");
            }

            file.Position = nextHeader;
            if ((flags & 0x0001) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ValidateWithVersOneAsync(EpubInspectionRequest request, CancellationToken token)
    {
        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        using CancellationCheckingStream guarded = new(file, token);
        EpubReaderOptions options = new(EpubReaderOptionsPreset.RELAXED);
        options.Epub3NavDocumentReaderOptions.IgnoreMissingNavManifestItemError = true;
        options.Epub3NavDocumentReaderOptions.IgnoreMissingNavFileError = true;
        options.SpineReaderOptions.IgnoreMissingManifestItems = true;
        options.SpineReaderOptions.IgnoreMissingContentFiles = true;
        options.ContentDownloaderOptions.DownloadContent = false;
        options.ContentDownloaderOptions.CustomContentDownloader = FailClosedContentDownloader.Instance;
        using EpubBookRef book = await EpubReader.OpenBookAsync(guarded, options).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EPUB reader did not return a package reference.");
        token.ThrowIfCancellationRequested();
        _ = book.Title;
    }

    private static async Task<EpubInspectionResult> ReadFactsAsync(
        EpubInspectionRequest request,
        IProgress<EpubInspectionProgress>? progress,
        CancellationToken token)
    {
        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        using CancellationCheckingStream guarded = new(file, token);
        using ZipArchive archive = new(guarded, ZipArchiveMode.Read, leaveOpen: true);
        ReadBudget readBudget = new(request.Limits.MaximumDeclaredUncompressedBytes);
        Dictionary<string, ZipArchiveEntry> entries = archive.Entries.ToDictionary(entry => NormalizeRequired(entry.FullName), StringComparer.Ordinal);
        XDocument container = await ReadXmlAsync(entries["META-INF/container.xml"], request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
        string? packageReference = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (!EpubArchivePathResolver.TryNormalizeEntryName(packageReference, out string packagePath) || !entries.TryGetValue(packagePath, out ZipArchiveEntry? packageEntry))
        {
            throw new InvalidDataException("Package path is invalid.");
        }

        XDocument package = await ReadXmlAsync(packageEntry, request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
        XElement root = package.Root ?? throw new InvalidDataException("Package root is missing.");
        string? version = Bound(root.Attribute("version")?.Value);
        XElement? metadata = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "metadata");
        string? title = Bound(metadata?.Descendants().FirstOrDefault(element => element.Name.LocalName == "title")?.Value);
        string[] authors = Values(metadata, "creator");
        string[] languages = Values(metadata, "language");
        string[] dates = Values(metadata, "date");
        string[] identifiers = Values(metadata, "identifier");

        Dictionary<string, ManifestItem> manifest = [];
        foreach (XElement item in root.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            token.ThrowIfCancellationRequested();
            string? id = item.Attribute("id")?.Value;
            string? href = item.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href) || !EpubArchivePathResolver.TryResolve(packagePath, href, out string resolved)) continue;
            manifest.TryAdd(id, new(resolved, item.Attribute("media-type")?.Value ?? string.Empty, item.Attribute("properties")?.Value ?? string.Empty));
        }

        string[] spineIds = root.Descendants().Where(element => element.Name.LocalName == "itemref")
            .Select(element => element.Attribute("idref")?.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Take(request.Limits.MaximumSpineItems + 1).ToArray();
        if (spineIds.Length > request.Limits.MaximumSpineItems) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "The EPUB spine exceeds its configured item limit.");
        EvidenceAccumulator missingSpine = new(request.Limits.MaximumEvidencePerRule);
        EvidenceAccumulator emptyChapters = new(request.Limits.MaximumEvidencePerRule);
        EvidenceAccumulator repeated = new(request.Limits.MaximumEvidencePerRule);
        EvidenceAccumulator broken = new(request.Limits.MaximumEvidencePerRule);
        EvidenceAccumulator remote = new(request.Limits.MaximumEvidencePerRule);
        EvidenceAccumulator optionalTruncations = new(request.Limits.MaximumEvidencePerRule);
        Dictionary<string, int> spineIdOccurrences = new(StringComparer.Ordinal);
        Dictionary<string, int> spinePathOccurrences = new(StringComparer.Ordinal);
        Dictionary<string, int> navigationTargetOccurrences = new(StringComparer.Ordinal);
        int readable = 0;
        int chapterCount = 0;
        int localReferenceCount = 0;
        foreach (string spineId in spineIds)
        {
            token.ThrowIfCancellationRequested();
            AddDuplicateOccurrence(spineIdOccurrences, spineId, "idref", repeated);
            if (!manifest.TryGetValue(spineId, out ManifestItem? item) || !entries.TryGetValue(item.Path, out ZipArchiveEntry? chapter))
            {
                missingSpine.Add(Bound(spineId)!);
                continue;
            }

            AddDuplicateOccurrence(spinePathOccurrences, item.Path, "href", repeated);
            if (!item.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
            if (chapter.Length > request.Limits.MaximumChapterBytes) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "An EPUB chapter exceeds its configured limit.");
            chapterCount++;
            progress?.Report(new("Content", chapterCount, spineIds.Length));
            string html = await ReadTextAsync(chapter, request.Limits.MaximumChapterBytes, readBudget, token).ConfigureAwait(false);
            HtmlDocument document = LoadBoundedHtml(html, request.Limits, token);
            foreach (HtmlNode node in document.DocumentNode.SelectNodes("//script|//style|//nav") ?? Enumerable.Empty<HtmlNode>())
            {
                token.ThrowIfCancellationRequested();
                node.Remove();
            }
            token.ThrowIfCancellationRequested();
            int characters = document.DocumentNode.InnerText.Count(char.IsLetterOrDigit);
            readable = checked(readable + characters);
            if (readable > request.Limits.MaximumReadableCharacters) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "Readable-content analysis exceeded its configured limit.");
            if (characters < 100) emptyChapters.Add(item.Path);
            foreach (HtmlNode node in document.DocumentNode.SelectNodes("//*[@src or @href or @poster]") ?? Enumerable.Empty<HtmlNode>())
            {
                token.ThrowIfCancellationRequested();
                foreach (string attributeName in new[] { "src", "href", "poster" })
                {
                    string? reference = node.Attributes[attributeName]?.Value;
                    if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('#')) continue;
                    if (EpubArchivePathResolver.IsRemote(reference) || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        remote.Add(SanitizeExternalReference(reference));
                    }
                    else if (!EpubArchivePathResolver.TryResolve(item.Path, reference, out string local) || !entries.ContainsKey(local))
                    {
                        localReferenceCount++;
                        if (localReferenceCount > request.Limits.MaximumLocalReferences) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB local-reference analysis exceeded its configured limit.");
                        broken.Add(Bound(reference)!);
                    }
                    else
                    {
                        localReferenceCount++;
                        if (localReferenceCount > request.Limits.MaximumLocalReferences) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB local-reference analysis exceeded its configured limit.");
                    }
                }
            }
        }

        foreach (ManifestItem cssItem in manifest.Values.Where(item => item.MediaType.Contains("css", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(cssItem.Path, out ZipArchiveEntry? cssEntry)) continue;
            if (cssEntry.Length > request.Limits.MaximumCssBytes)
            {
                optionalTruncations.Add($"css:{cssItem.Path}");
                continue;
            }

            string css = await ReadTextAsync(cssEntry, request.Limits.MaximumCssBytes, readBudget, token).ConfigureAwait(false);
            foreach (string reference in EnumerateCssReferences(css))
            {
                token.ThrowIfCancellationRequested();
                if (EpubArchivePathResolver.IsRemote(reference) || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    remote.Add(SanitizeExternalReference(reference));
                    continue;
                }

                localReferenceCount++;
                if (localReferenceCount > request.Limits.MaximumLocalReferences) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB local-reference analysis exceeded its configured limit.");
                if (!EpubArchivePathResolver.TryResolve(cssItem.Path, reference, out string local) || !entries.ContainsKey(local))
                {
                    broken.Add(Bound(reference)!);
                }
            }
        }

        bool navigation = false;
        foreach (ManifestItem navItem in manifest.Values
                     .Where(item => item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.Ordinal))
                     .OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(navItem.Path, out ZipArchiveEntry? navEntry)) continue;
            if (navEntry.Length > request.Limits.MaximumXmlBytes) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "The EPUB navigation document exceeds its configured limit.");
            string navHtml = await ReadTextAsync(navEntry, request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
            HtmlDocument navDocument = LoadBoundedHtml(navHtml, request.Limits, token);
            foreach (HtmlNode anchor in navDocument.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                token.ThrowIfCancellationRequested();
                string? reference = anchor.Attributes["href"]?.Value;
                if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('#')) continue;
                if (EpubArchivePathResolver.IsRemote(reference))
                {
                    remote.Add(SanitizeExternalReference(reference));
                    continue;
                }

                localReferenceCount++;
                if (localReferenceCount > request.Limits.MaximumLocalReferences) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB local-reference analysis exceeded its configured limit.");
                if (!EpubArchivePathResolver.TryResolve(navItem.Path, reference, out string local) || !entries.ContainsKey(local))
                {
                    broken.Add(Bound(reference)!);
                    continue;
                }

                navigation = true;
                AddDuplicateOccurrence(navigationTargetOccurrences, local, "navigation", repeated);
            }
        }

        string? tocId = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?.Attribute("toc")?.Value;
        if (tocId is not null && manifest.TryGetValue(tocId, out ManifestItem? tocItem) && entries.TryGetValue(tocItem.Path, out ZipArchiveEntry? tocEntry))
        {
            if (tocEntry.Length > request.Limits.MaximumXmlBytes) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "The EPUB navigation document exceeds its configured limit.");
            XDocument toc = await ReadXmlAsync(tocEntry, request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
            foreach (string reference in toc.Descendants()
                         .Where(element => element.Name.LocalName == "content")
                         .Select(element => element.Attribute("src")?.Value)
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => value!))
            {
                token.ThrowIfCancellationRequested();
                if (EpubArchivePathResolver.IsRemote(reference))
                {
                    remote.Add(SanitizeExternalReference(reference));
                    continue;
                }

                localReferenceCount++;
                if (localReferenceCount > request.Limits.MaximumLocalReferences) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "EPUB local-reference analysis exceeded its configured limit.");
                if (!EpubArchivePathResolver.TryResolve(tocItem.Path, reference, out string local) || !entries.ContainsKey(local))
                {
                    broken.Add(Bound(reference)!);
                    continue;
                }

                navigation = true;
                AddDuplicateOccurrence(navigationTargetOccurrences, local, "navigation", repeated);
            }
        }

        string? coverId = metadata?.Descendants().FirstOrDefault(element => element.Name.LocalName == "meta" && string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
        ManifestItem? coverItem = manifest.Values.FirstOrDefault(item => item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.Ordinal))
            ?? (coverId is not null && manifest.TryGetValue(coverId, out ManifestItem? declaredCover) ? declaredCover : null);
        ZipArchiveEntry? coverEntry = null;
        bool coverPresent = coverItem is not null && entries.TryGetValue(coverItem.Path, out coverEntry);
        if (coverEntry?.Length > request.Limits.MaximumCoverBytes) return Fail(request, EpubInspectionProblemCode.LimitExceeded, "The EPUB cover exceeds its configured limit.");
        (int? width, int? height) = coverEntry is not null ? await ReadImageDimensionsAsync(coverEntry, request.Limits, readBudget, token).ConfigureAwait(false) : (null, null);
        bool coverHeaderMalformed = coverEntry is not null
            && width is null
            && height is null
            && coverItem is not null
            && IsSupportedImageHeader(coverItem);
        string encryptionState = "None";
        if (entries.TryGetValue("META-INF/encryption.xml", out ZipArchiveEntry? encryptionEntry))
        {
            XDocument encryption = await ReadXmlAsync(encryptionEntry, request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
            string[] algorithms = EncryptionAlgorithms(encryption);
            if (algorithms.Length == 0 || !algorithms.All(IsRecognizedFontObfuscation)) return Fail(request, EpubInspectionProblemCode.Encrypted, "Unsupported encryption or DRM prevents comparable EPUB inspection.");
            encryptionState = "Recognized font obfuscation";
        }

        return new(
            request.BookId, request.ExpectedRelativePath, true, true, true, version, title, authors, languages, dates, identifiers,
            coverPresent, width, height, navigation, manifest.Count, spineIds.Length, chapterCount, manifest.Count,
            missingSpine.OrderedItems(), broken.OrderedItems(), emptyChapters.OrderedItems(),
            repeated.OrderedItems(), remote.OrderedItems(), readable, encryptionState, optionalTruncations.TotalCount > 0, [], coverHeaderMalformed,
            optionalTruncations.OrderedItems(), missingSpine.TotalCount, broken.TotalCount, emptyChapters.TotalCount, repeated.TotalCount, remote.TotalCount);
    }

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });

    private static void EnsureCurrentFile(EpubInspectionRequest request)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LibraryRoot));
        string full = Path.GetFullPath(request.FullPath);
        string expected = Path.GetFullPath(Path.Combine(root, request.ExpectedRelativePath));
        if (!string.Equals(full, expected, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new FileChangedException();
        FileAttributes rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) throw new FileChangedException();
        string? parent = Path.GetDirectoryName(full);
        if (parent is null) throw new FileChangedException();
        string current = root;
        string relativeParent = Path.GetRelativePath(root, parent);
        foreach (string part in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) throw new FileChangedException();
        }

        FileInfo info = new(full);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileChangedException();
        }

        FormatFileObservation actual = new(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, (int)info.Attributes);
        if (actual != request.Observation || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new FileChangedException();
    }

    private static async Task<XDocument> ReadXmlAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        ReadBudget? readBudget,
        CancellationToken token)
    {
        await using Stream stream = entry.Open();
        using LimitedReadStream limited = new(stream, maximumBytes, token, readBudget);
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumBytes, Async = true };
        using XmlReader reader = XmlReader.Create(limited, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadTextAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        ReadBudget readBudget,
        CancellationToken token)
    {
        await using Stream stream = entry.Open();
        using LimitedReadStream limited = new(stream, maximumBytes, token, readBudget);
        using StreamReader reader = new(limited, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private static async Task<(int? Width, int? Height)> ReadImageDimensionsAsync(
        ZipArchiveEntry entry,
        EpubInspectionLimits limits,
        ReadBudget readBudget,
        CancellationToken token)
    {
        if (entry.Length > limits.MaximumCoverBytes) return (null, null);
        int count = (int)Math.Min(entry.Length, limits.MaximumCoverHeaderBytes);
        byte[] header = new byte[count];
        await using Stream stream = entry.Open();
        int read = await stream.ReadAtLeastAsync(header, count, throwOnEndOfStream: false, token).ConfigureAwait(false);
        readBudget.Add(read);
        if (read >= 24 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (ReadPositiveBigEndian(header, 16), ReadPositiveBigEndian(header, 20));
        if (read >= 10 && (header.AsSpan(0, 3).SequenceEqual("GIF"u8)))
        {
            int width = BitConverter.ToUInt16(header, 6);
            int height = BitConverter.ToUInt16(header, 8);
            return (width > 0 ? width : null, height > 0 ? height : null);
        }
        if (read >= 30 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8) && header.AsSpan(12, 4).SequenceEqual("VP8X"u8))
        {
            int width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
            int height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
            return (width, height);
        }
        if (read >= 4 && header[0] == 0xFF && header[1] == 0xD8)
        {
            for (int index = 2; index + 9 < read; index++)
            {
                if (header[index] == 0xFF && header[index + 1] is >= 0xC0 and <= 0xC3)
                {
                    int width = (header[index + 7] << 8) | header[index + 8];
                    int height = (header[index + 5] << 8) | header[index + 6];
                    return (width > 0 ? width : null, height > 0 ? height : null);
                }
            }
        }

        string start = System.Text.Encoding.UTF8.GetString(header, 0, read);
        if (start.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            int? width = ReadSvgDimension(start, "width");
            int? height = ReadSvgDimension(start, "height");
            if (width is not null && height is not null) return (width, height);
        }

        return (null, null);
    }

    private static HtmlDocument LoadBoundedHtml(string html, EpubInspectionLimits limits, CancellationToken token)
    {
        int tagMarkers = 0;
        for (int index = 0; index < html.Length; index++)
        {
            if ((index & 0x0FFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (html[index] == '<' && ++tagMarkers > checked(limits.MaximumHtmlNodes * 2))
            {
                throw new InspectionLimitException();
            }
        }

        HtmlDocument document = new()
        {
            OptionMaxNestedChildNodes = limits.MaximumHtmlDepth,
        };
        try
        {
            document.LoadHtml(html);
        }
        catch (Exception exception) when (exception.Message.StartsWith("Document has more than", StringComparison.Ordinal))
        {
            throw new InspectionLimitException();
        }

        int nodeCount = 0;
        Stack<HtmlNode> nodes = new();
        nodes.Push(document.DocumentNode);
        while (nodes.TryPop(out HtmlNode? node))
        {
            token.ThrowIfCancellationRequested();
            if (++nodeCount > limits.MaximumHtmlNodes)
            {
                throw new InspectionLimitException();
            }

            for (HtmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
            {
                nodes.Push(child);
            }
        }

        return document;
    }

    private static IEnumerable<string> EnumerateCssReferences(string css)
    {
        int searchFrom = 0;
        while (searchFrom < css.Length)
        {
            int start = css.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;
            int end = css.IndexOf(')', start + 4);
            if (end < 0) yield break;
            string value = css[(start + 4)..end].Trim().Trim('\'', '"');
            if (value.Length > 0 && value.Length <= 512) yield return value;
            searchFrom = end + 1;
        }
    }

    private static int? ReadSvgDimension(string svg, string name)
    {
        int index = svg.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        index = svg.IndexOf('=', index + name.Length);
        if (index < 0) return null;
        int start = index + 1;
        while (start < svg.Length && (char.IsWhiteSpace(svg[start]) || svg[start] is '\'' or '"')) start++;
        int end = start;
        while (end < svg.Length && (char.IsDigit(svg[end]) || svg[end] == '.')) end++;
        return double.TryParse(svg[start..end], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && value is > 0 and <= int.MaxValue
            ? (int)value
            : null;
    }

    private static int? ReadPositiveBigEndian(byte[] bytes, int offset)
    {
        uint value = ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16)
            | ((uint)bytes[offset + 2] << 8)
            | bytes[offset + 3];
        return value is > 0 and <= int.MaxValue ? (int)value : null;
    }
    private static bool IsSupportedImageHeader(ManifestItem item)
    {
        string mediaType = item.MediaType.Trim();
        if (mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/svg+xml") return true;
        string extension = Path.GetExtension(item.Path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }
    private static string[] EncryptionAlgorithms(XDocument encryption) => encryption.Descendants().Attributes()
        .Where(attribute => attribute.Name.LocalName == "Algorithm")
        .Select(attribute => attribute.Value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
    private static bool IsRecognizedFontObfuscation(string algorithm) => string.Equals(
            algorithm,
            "http://www.idpf.org/2008/embedding",
            StringComparison.Ordinal)
        || string.Equals(algorithm, "http://ns.adobe.com/pdf/enc#RC", StringComparison.Ordinal);
    private static string NormalizeRequired(string value) => EpubArchivePathResolver.TryNormalizeEntryName(value, out string normalized) ? normalized : throw new InvalidDataException("Unsafe archive path.");
    private static string[] Values(XElement? metadata, string localName) => (metadata?.Descendants().Where(element => element.Name.LocalName == localName).Select(element => Bound(element.Value)).Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(100).ToArray()) ?? [];
    private static string? Bound(string? value) { string? result = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); return result is null ? null : result[..Math.Min(result.Length, 512)]; }
    private static void AddDuplicateOccurrence(
        Dictionary<string, int> occurrences,
        string value,
        string kind,
        EvidenceAccumulator evidence)
    {
        occurrences.TryGetValue(value, out int count);
        count = checked(count + 1);
        occurrences[value] = count;
        if (count > 1)
        {
            evidence.Add($"{kind}:{Bound(value)}#{count}");
        }
    }

    private static string SanitizeExternalReference(string reference)
    {
        string value = reference.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)
            && Uri.TryCreate($"https:{value}", UriKind.Absolute, out Uri? protocolRelative))
        {
            return Bound($"scheme:protocol-relative;host:{protocolRelative.IdnHost}")!;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return Bound(uri.IsFile || string.IsNullOrEmpty(uri.Host)
                ? $"scheme:{uri.Scheme}"
                : $"scheme:{uri.Scheme};host:{uri.IdnHost}")!;
        }

        return "scheme:external";
    }
    private static EpubInspectionResult Fail(EpubInspectionRequest request, EpubInspectionProblemCode code, string explanation) => EpubInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, code, explanation);

    private sealed record ManifestItem(string Path, string MediaType, string Properties);
    private sealed record PreflightResult(IReadOnlyList<string> EntryNames, EpubInspectionProblem? Problem)
    {
        public static PreflightResult Fail(EpubInspectionProblemCode code, string explanation) => new([], new(code, explanation));
    }
    private sealed class FileChangedException : Exception;
    private sealed class InspectionLimitException : Exception;
    private sealed class EvidenceAccumulator(int maximum)
    {
        private readonly List<string> _items = [];

        public int TotalCount { get; private set; }

        public void Add(string value)
        {
            TotalCount = checked(TotalCount + 1);
            if (_items.Count < maximum)
            {
                _items.Add(Bound(value)!);
            }
        }

        public string[] OrderedItems() => _items.Order(StringComparer.Ordinal).ToArray();
    }
    private sealed class ReadBudget(long limit)
    {
        private long _read;

        public void Add(int count)
        {
            _read = checked(_read + count);
            if (_read > limit) throw new InspectionLimitException();
        }
    }
    private sealed class FailClosedContentDownloader : VersOne.Epub.Environment.IContentDownloader
    {
        public static FailClosedContentDownloader Instance { get; } = new();

        public Task<Stream> DownloadAsync(string url, string userAgent) =>
            throw new InvalidOperationException("EPUB network access is disabled.");
    }

    private sealed class LimitedReadStream(
        Stream inner,
        long limit,
        CancellationToken token,
        ReadBudget? readBudget = null) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); int read = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read + 1)); Add(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { token.ThrowIfCancellationRequested(); int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read + 1)], cancellationToken).ConfigureAwait(false); Add(read); return read; }
        private void Add(int count) { _read += count; readBudget?.Add(count); if (_read > limit) throw new InspectionLimitException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
