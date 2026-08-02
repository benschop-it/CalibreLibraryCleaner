using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
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
        LogLevel.Debug,
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
                EpubInspectionResult? fallback = await TryInspectFallbackAsync(
                    request,
                    new(
                        EpubInspectionProblemCode.Unsupported,
                        "The EPUB uses an entry that the primary parser does not support.",
                        IssueCode: EpubInspectionIssueCode.UnsupportedParser),
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (fallback is not null && fallback.Coverage == EpubAssessmentCoverage.FallbackReadable)
                {
                    EnsureCurrentFile(request);
                    return fallback;
                }

                return Fail(request, EpubInspectionProblemCode.CannotOpen, "The EPUB ZIP container is invalid or unreadable.");
            }

            if (preflight.Problem is not null)
            {
                EpubInspectionResult? fallback = await TryInspectFallbackAsync(
                    request,
                    preflight.Problem,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (fallback is not null)
                {
                    EnsureCurrentFile(request);
                    return fallback;
                }

                return Fail(request, preflight.Problem.Code, preflight.Problem.Explanation);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Package", 0, null));
            if (!HasRecoverablePackageDefect(preflight.RecoverableProblems))
            {
                try
                {
                    await ValidateWithVersOneAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsUnclassifiedParserFailure(exception))
                {
                    InspectionFailed(logger, "UnsupportedParserFailure", null);
                    EpubInspectionResult? fallback = await TryInspectFallbackAsync(
                        request,
                        new(
                            EpubInspectionProblemCode.Unsupported,
                            "The EPUB parser could not safely interpret this package.",
                            IssueCode: EpubInspectionIssueCode.UnsupportedParser),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    return fallback ?? Fail(request, EpubInspectionProblemCode.Unsupported, "The EPUB parser could not safely interpret this package.");
                }
            }
            EpubInspectionResult result = await ReadFactsAsync(request, preflight.RecoverableProblems, progress, cancellationToken).ConfigureAwait(false);
            if (result.Problems.Count == 1)
            {
                EpubInspectionResult? fallback = await TryInspectFallbackAsync(
                    request,
                    result.Problems[0],
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (fallback is not null)
                {
                    result = fallback;
                }
            }

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
        catch (NotSupportedException)
        {
            InspectionFailed(logger, "Unsupported", null);
            return Fail(request, EpubInspectionProblemCode.Unsupported, "The EPUB uses a container or package feature that is not supported.");
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

    private static bool IsUnclassifiedParserFailure(Exception exception) => exception is not (
        OperationCanceledException or
        FileChangedException or
        FileNotFoundException or
        DirectoryNotFoundException or
        UnauthorizedAccessException or
        SecurityException or
        IOException or
        InvalidDataException or
        XmlException or
        EpubReaderException or
        OverflowException or
        InspectionLimitException or
        OutOfMemoryException);

    private static bool HasRecoverablePackageDefect(IReadOnlyList<EpubInspectionProblem> problems) =>
        problems.Any(problem => problem.Code == EpubInspectionProblemCode.PackageMalformed);

    private static async Task<PreflightResult> PreflightAsync(EpubInspectionRequest request, CancellationToken token)
    {
        List<EpubInspectionProblem> recoverableProblems = [];
        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        CentralDirectoryInspection centralDirectory = await ValidateCentralDirectoryAsync(file, request.Limits.MaximumArchiveEntries, token).ConfigureAwait(false);
        if (centralDirectory.Entries.Any(entry => entry.Encrypted))
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.Encrypted, "Encrypted ZIP entries are not supported for EPUB inspection.");
        }
        file.Position = 0;
        using CancellationCheckingStream guarded = new(file, token);
        using ZipArchive archive = new(guarded, ZipArchiveMode.Read, leaveOpen: true);
        System.Collections.ObjectModel.ReadOnlyCollection<ZipArchiveEntry> archiveEntries;
        try
        {
            archiveEntries = archive.Entries;
        }
        catch (InvalidDataException)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.PackageMalformed, "The EPUB archive central directory is corrupt or contains invalid metadata.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.PackageMalformed, "The EPUB archive central directory is corrupt or contains invalid metadata.");
        }

        if (archiveEntries.Count > request.Limits.MaximumArchiveEntries)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB contains too many archive entries.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        long totalLength = 0;
        long totalCompressed = 0;
        foreach (ZipArchiveEntry entry in archiveEntries)
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
            return PreflightResult.Fail(
                EpubInspectionProblemCode.PackageMalformed,
                "The EPUB container document is missing.",
                EpubInspectionIssueCode.MissingContainer);
        }

        if (containerEntry.Length > request.Limits.MaximumXmlBytes)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB container document exceeds its configured limit.");
        }

        XDocument container;
        try
        {
            container = await ReadXmlAsync(containerEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
        }
        catch (XmlException)
        {
            return PreflightResult.Fail(
                EpubInspectionProblemCode.PackageMalformed,
                "The EPUB container document is not valid XML.",
                EpubInspectionIssueCode.MalformedContainer);
        }

        string? packageReference = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (!EpubArchivePathResolver.TryNormalizeEntryName(packageReference, out string packagePath)
            || !entries.TryGetValue(packagePath, out ZipArchiveEntry? packageEntry))
        {
            return PreflightResult.Fail(
                EpubInspectionProblemCode.PackageMalformed,
                "The EPUB package document is missing or has an unsafe path.",
                EpubInspectionIssueCode.MissingPackage);
        }

        if (packageEntry.Length > request.Limits.MaximumXmlBytes)
        {
            return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB package document exceeds its configured limit.");
        }

        XDocument package;
        try
        {
            package = await ReadXmlAsync(packageEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
        }
        catch (XmlException)
        {
            return PreflightResult.Fail(
                EpubInspectionProblemCode.PackageMalformed,
                "The EPUB package document is not valid XML.",
                EpubInspectionIssueCode.MalformedPackage);
        }

        foreach (XElement item in package.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            token.ThrowIfCancellationRequested();
            string? href = item.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                AddRecoverableProblem(recoverableProblems, EpubInspectionProblemCode.PackageMalformed, "An EPUB manifest item has no content file path.", EpubInspectionIssueCode.InvalidManifestItemPath);
                continue;
            }

            if (EpubArchivePathResolver.IsRemote(href))
            {
                continue;
            }

            string contentPath = href.Split('#', 2)[0].Split('?', 2)[0];
            string contentFileName = contentPath[(contentPath.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(contentFileName) || contentFileName is "." or "..")
            {
                AddRecoverableProblem(recoverableProblems, EpubInspectionProblemCode.PackageMalformed, "An EPUB manifest item has no content file name.", EpubInspectionIssueCode.InvalidManifestItemName);
                continue;
            }

            if (!EpubArchivePathResolver.TryResolve(packagePath, href, out _))
            {
                return PreflightResult.Fail(
                    EpubInspectionProblemCode.UnsafeArchive,
                    "An EPUB manifest item has an unsafe content path.",
                    EpubInspectionIssueCode.InvalidManifestItemPath);
            }
        }

        string? tocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?.Attribute("toc")?.Value;
        HashSet<string> ncxReferences = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Where(element =>
                string.Equals(element.Attribute("media-type")?.Value, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase)
                || tocId is not null && string.Equals(element.Attribute("id")?.Value, tocId, StringComparison.Ordinal))
            .Select(element => element.Attribute("href")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
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
                return PreflightResult.Fail(
                    EpubInspectionProblemCode.UnsafeArchive,
                    "The EPUB navigation document has an unsafe path.",
                    EpubInspectionIssueCode.MalformedNavigation);
            }

            if (!entries.TryGetValue(eagerXmlPath, out ZipArchiveEntry? eagerXmlEntry))
            {
                continue;
            }

            if (eagerXmlEntry.Length > request.Limits.MaximumXmlBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "An EPUB navigation document exceeds its configured limit.");
            }

            XDocument eagerXml;
            try
            {
                eagerXml = await ReadXmlAsync(eagerXmlEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
            }
            catch (XmlException)
            {
                AddRecoverableProblem(recoverableProblems, EpubInspectionProblemCode.PackageMalformed, "An EPUB navigation document is not valid XML.", EpubInspectionIssueCode.MalformedNavigation);
                continue;
            }

            if (ncxReferences.Contains(eagerXmlReference)
                && !eagerXml.Descendants().Any(element => element.Name.LocalName == "navMap"))
            {
                AddRecoverableProblem(recoverableProblems, EpubInspectionProblemCode.PackageMalformed, "The EPUB 2 NCX document does not contain a navMap element.", EpubInspectionIssueCode.MissingNavigationMap);
            }
        }

        if (entries.TryGetValue("META-INF/encryption.xml", out ZipArchiveEntry? encryptionEntry))
        {
            if (encryptionEntry.Length > request.Limits.MaximumXmlBytes)
            {
                return PreflightResult.Fail(EpubInspectionProblemCode.LimitExceeded, "The EPUB encryption document exceeds its configured limit.");
            }

            XDocument encryption;
            try
            {
                encryption = await ReadXmlAsync(encryptionEntry, request.Limits.MaximumXmlBytes, null, token).ConfigureAwait(false);
            }
            catch (XmlException)
            {
                return PreflightResult.Fail(
                    EpubInspectionProblemCode.PackageMalformed,
                    "The EPUB encryption document is not valid XML.",
                    allowsFallbackInspection: false);
            }

            string[] algorithms = EncryptionAlgorithms(encryption);
            if (algorithms.Length == 0 || !algorithms.All(IsRecognizedFontObfuscation))
            {
                return PreflightResult.Fail(
                    EpubInspectionProblemCode.Encrypted,
                    "Unsupported encryption or DRM prevents comparable EPUB inspection.",
                    allowsFallbackInspection: false);
            }
        }

        return new(entries.Keys.Order(StringComparer.Ordinal).ToArray(), null, recoverableProblems);
    }

    private static async Task<CentralDirectoryInspection> ValidateCentralDirectoryAsync(FileStream file, int maximumEntries, CancellationToken token)
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
        List<CentralDirectoryEntry> entries = [];
        for (ulong index = 0; index < resolvedTotalEntries; index++)
        {
            token.ThrowIfCancellationRequested();
            await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryFileHeaderSignature)
            {
                throw new InvalidDataException("The ZIP central directory is malformed.");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            long nextHeader = checked(file.Position + fileNameLength + extraLength + commentLength);
            if ((ulong)nextHeader > centralDirectoryOffset + centralDirectorySize)
            {
                throw new InvalidDataException("The ZIP central directory entry exceeds its declared bounds.");
            }

            entries.Add(new((flags & 0x0001) != 0, compressionMethod));
            file.Position = nextHeader;
        }

        return new(entries);
    }

    private static async Task<EpubInspectionResult?> TryInspectFallbackAsync(
        EpubInspectionRequest request,
        EpubInspectionProblem trigger,
        IProgress<EpubInspectionProgress>? progress,
        CancellationToken token)
    {
        if (!trigger.AllowsFallbackInspection
            || trigger.Code is EpubInspectionProblemCode.CannotOpen
            or EpubInspectionProblemCode.Unreadable
            or EpubInspectionProblemCode.ChangedDuringInspection)
        {
            return null;
        }

        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        CentralDirectoryInspection centralDirectory;
        try
        {
            centralDirectory = await ValidateCentralDirectoryAsync(file, request.Limits.MaximumArchiveEntries, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or InspectionLimitException or OverflowException)
        {
            return null;
        }

        file.Position = 0;
        using CancellationCheckingStream guarded = new(file, token);
        using ZipArchive archive = new(guarded, ZipArchiveMode.Read, leaveOpen: true);
        System.Collections.ObjectModel.ReadOnlyCollection<ZipArchiveEntry> archiveEntries;
        try
        {
            archiveEntries = archive.Entries;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            return null;
        }

        if (archiveEntries.Count != centralDirectory.Entries.Count
            || archiveEntries.Count > request.Limits.MaximumArchiveEntries)
        {
            return null;
        }

        long totalLength = 0;
        long totalCompressed = 0;
        List<FallbackEntry> candidates = [];
        List<EpubInspectionIssue> issues = [];
        Dictionary<string, List<FallbackEntry>> normalizedGroups = new(StringComparer.Ordinal);
        for (int index = 0; index < archiveEntries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archiveEntries[index];
            CentralDirectoryEntry centralEntry = centralDirectory.Entries[index];
            long length = entry.Length;
            long compressed = entry.CompressedLength;
            if (length < 0 || compressed < 0)
            {
                return null;
            }

            totalLength = checked(totalLength + length);
            totalCompressed = checked(totalCompressed + compressed);
            if (totalLength > request.Limits.MaximumDeclaredUncompressedBytes)
            {
                return null;
            }

            if (!EpubArchivePathResolver.TryNormalizeEntryName(entry.FullName, out string normalized))
            {
                issues.Add(new(EpubInspectionIssueCode.UnsafeEntry, "Archive"));
                continue;
            }

            FallbackEntry fallbackEntry = new(normalized, entry, centralEntry);
            if (!normalizedGroups.TryGetValue(normalized, out List<FallbackEntry>? group))
            {
                group = [];
                normalizedGroups.Add(normalized, group);
            }

            group.Add(fallbackEntry);
        }

        if (totalLength > 10L * 1024 * 1024
            && (totalCompressed == 0 || totalLength > checked(totalCompressed * request.Limits.MaximumAggregateCompressionRatio)))
        {
            return null;
        }

        foreach ((string normalized, List<FallbackEntry> group) in normalizedGroups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (group.Count > 1)
            {
                issues.Add(new(EpubInspectionIssueCode.DuplicateEntry, "Archive", normalized, observed: group.Count));
                continue;
            }

            FallbackEntry item = group[0];
            if (item.Central.Encrypted)
            {
                issues.Add(new(EpubInspectionIssueCode.EncryptedEntry, "Archive", item.Name));
                continue;
            }

            if (item.Central.CompressionMethod is not 0 and not 8)
            {
                issues.Add(new(EpubInspectionIssueCode.UnsupportedEntry, "Archive", item.Name, observed: item.Central.CompressionMethod));
                continue;
            }

            if (item.Entry.Length > request.Limits.MaximumEntryBytes)
            {
                issues.Add(new(EpubInspectionIssueCode.OversizedEntry, "Archive", item.Name, item.Entry.Length, request.Limits.MaximumEntryBytes));
                continue;
            }

            if (item.Entry.Length > 1024L * 1024
                && (item.Entry.CompressedLength == 0
                    || item.Entry.Length > checked(item.Entry.CompressedLength * request.Limits.MaximumCompressionRatio)))
            {
                issues.Add(new(EpubInspectionIssueCode.SuspiciousEntry, "Archive", item.Name));
                continue;
            }

            candidates.Add(item);
        }

        ReadBudget readBudget = new(request.Limits.MaximumDeclaredUncompressedBytes);
        bool encryptionMetadataDeclared = normalizedGroups.ContainsKey("META-INF/encryption.xml");
        FallbackEntry? encryptionMetadata = candidates.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, "META-INF/encryption.xml", StringComparison.Ordinal));
        if (encryptionMetadataDeclared && encryptionMetadata is null)
        {
            return null;
        }

        if (encryptionMetadata is not null)
        {
            if (encryptionMetadata.Entry.Length > request.Limits.MaximumXmlBytes)
            {
                return null;
            }

            XDocument encryption;
            try
            {
                encryption = await ReadXmlAsync(
                    encryptionMetadata.Entry,
                    request.Limits.MaximumXmlBytes,
                    readBudget,
                    token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException)
            {
                return null;
            }

            XElement[] encryptedData = encryption.Descendants()
                .Where(element => element.Name.LocalName == "EncryptedData")
                .ToArray();
            if (encryptedData.Length == 0)
            {
                return null;
            }

            HashSet<string> protectedNames = new(StringComparer.Ordinal);
            foreach (XElement encryptedItem in encryptedData)
            {
                token.ThrowIfCancellationRequested();
                string? reference = encryptedItem.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "CipherReference")?
                    .Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "URI")?
                    .Value;
                if (!TryNormalizeEncryptedReferences(reference, out string[] protectedReferences))
                {
                    return null;
                }

                protectedNames.UnionWith(protectedReferences);
            }

            foreach (string protectedName in protectedNames.Order(StringComparer.Ordinal))
            {
                if (candidates.RemoveAll(candidate => candidate.Name == protectedName) > 0)
                {
                    issues.Add(new(EpubInspectionIssueCode.EncryptedEntry, "Encryption", protectedName));
                }
            }
        }

        AddTriggerIssue(issues, trigger);
        issues.Add(new(EpubInspectionIssueCode.UnknownReadingOrder, "Fallback"));
        HashSet<string> acceptedNames = candidates.Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal);
        FallbackEntry[] contentCandidates = candidates
            .Where(candidate => IsFallbackContentCandidate(candidate.Name))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        int readableCharacters = 0;
        int renderableCount = 0;
        EpubRenderableEvidence renderableEvidence = EpubRenderableEvidence.None;
        int inspectedCandidates = 0;
        int inspectedLocalReferences = 0;
        progress?.Report(new("Fallback", 0, contentCandidates.Length));
        foreach (FallbackEntry candidate in contentCandidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Entry.Length > request.Limits.MaximumChapterBytes)
            {
                issues.Add(new(
                    EpubInspectionIssueCode.OversizedEntry,
                    "Fallback",
                    candidate.Name,
                    candidate.Entry.Length,
                    request.Limits.MaximumChapterBytes));
                continue;
            }

            inspectedCandidates++;
            progress?.Report(new("Fallback", inspectedCandidates, contentCandidates.Length));
            string content;
            try
            {
                content = await ReadTextAsync(candidate.Entry, request.Limits.MaximumChapterBytes, readBudget, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
            {
                issues.Add(new(EpubInspectionIssueCode.UnsupportedEntry, "Fallback", candidate.Name));
                continue;
            }

            HtmlDocument document;
            try
            {
                document = LoadBoundedHtml(content, request.Limits, token);
            }
            catch (InspectionLimitException)
            {
                issues.Add(new(EpubInspectionIssueCode.SuspiciousEntry, "Fallback", candidate.Name));
                continue;
            }

            foreach (HtmlNode node in document.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>())
            {
                node.Remove();
            }

            int characters = document.DocumentNode.InnerText.Count(char.IsLetterOrDigit);
            LocalMediaInspection localMedia = InspectAcceptedLocalMediaReferences(
                document,
                candidate.Name,
                acceptedNames,
                request.Limits.MaximumLocalReferences,
                ref inspectedLocalReferences,
                token);
            if (localMedia.LimitExceeded)
            {
                issues.Add(new(
                    EpubInspectionIssueCode.PartialCoverage,
                    "FallbackReferences",
                    candidate.Name,
                    observed: localMedia.InspectedCount,
                    limit: request.Limits.MaximumLocalReferences));
            }
            bool renderableSvg = Path.GetExtension(candidate.Name).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                && document.DocumentNode.SelectSingleNode("//*[local-name()='svg']") is not null
                && document.DocumentNode.SelectNodes("//*[local-name()='path' or local-name()='rect' or local-name()='circle' or local-name()='ellipse' or local-name()='line' or local-name()='polyline' or local-name()='polygon' or local-name()='image' or local-name()='text']")?.Count > 0;
            if (characters == 0 && !localMedia.Found && !renderableSvg)
            {
                continue;
            }

            renderableCount++;
            readableCharacters = checked(readableCharacters + characters);
            if (readableCharacters > request.Limits.MaximumReadableCharacters)
            {
                return null;
            }

            if (characters > 0) renderableEvidence |= EpubRenderableEvidence.Text;
            if (localMedia.Found) renderableEvidence |= EpubRenderableEvidence.LocalMediaReference;
            if (renderableSvg) renderableEvidence |= EpubRenderableEvidence.Svg;
        }

        if (renderableCount == 0)
        {
            EpubInspectionIssue[] boundedIssues = BoundIssues(issues, request.Limits.MaximumEvidencePerRule);
            return EpubInspectionResult.Failed(request.BookId, request.ExpectedRelativePath, trigger.Code, trigger.Explanation) with
            {
                Coverage = EpubAssessmentCoverage.Incomplete,
                AvailableFacets = EpubAssessmentFacet.Archive,
                Issues = boundedIssues,
                FallbackCandidateCount = inspectedCandidates,
            };
        }

        EnsureCurrentFile(request);
        EpubInspectionIssue[] successfulIssues = BoundIssues(issues, request.Limits.MaximumEvidencePerRule);
        return new(
            request.BookId,
            request.ExpectedRelativePath,
            true,
            true,
            false,
            null,
            null,
            [],
            [],
            [],
            [],
            false,
            null,
            null,
            false,
            0,
            0,
            renderableCount,
            candidates.Count,
            [],
            [],
            [],
            [],
            [],
            readableCharacters,
            centralDirectory.Entries.Any(entry => entry.Encrypted) ? "Encrypted ZIP entries skipped" : "ZIP entry encryption checked",
            false,
            [],
            Coverage: EpubAssessmentCoverage.FallbackReadable,
            AvailableFacets: EpubAssessmentFacet.Archive | EpubAssessmentFacet.Content,
            Issues: successfulIssues,
            FallbackCandidateCount: inspectedCandidates,
            FallbackRenderableCount: renderableCount,
            RenderableEvidence: renderableEvidence);
    }

    private static bool IsFallbackContentCandidate(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalMediaInspection InspectAcceptedLocalMediaReferences(
        HtmlDocument document,
        string baseFile,
        HashSet<string> acceptedNames,
        int maximumReferences,
        ref int inspectedReferences,
        CancellationToken token)
    {
        foreach (HtmlNode node in document.DocumentNode.SelectNodes("//*[@src or @poster or @data]") ?? Enumerable.Empty<HtmlNode>())
        {
            foreach (string attributeName in new[] { "src", "poster", "data" })
            {
                token.ThrowIfCancellationRequested();
                string? reference = node.Attributes[attributeName]?.Value;
                if (string.IsNullOrWhiteSpace(reference)
                    || !IsRenderableMediaAttribute(node, attributeName))
                {
                    continue;
                }

                inspectedReferences++;
                if (inspectedReferences > maximumReferences)
                {
                    return new(false, true, inspectedReferences);
                }

                if (!EpubArchivePathResolver.IsRemote(reference)
                    && EpubArchivePathResolver.TryResolve(baseFile, reference, out string normalized)
                    && acceptedNames.Contains(normalized)
                    && IsAcceptedFallbackMedia(normalized))
                {
                    return new(true, false, inspectedReferences);
                }
            }
        }

        return new(false, false, inspectedReferences);
    }

    private static bool IsRenderableMediaAttribute(HtmlNode node, string attributeName) =>
        attributeName.Equals("src", StringComparison.OrdinalIgnoreCase)
            && (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("audio", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("video", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("embed", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("input", StringComparison.OrdinalIgnoreCase)
                    && node.GetAttributeValue("type", string.Empty).Equals("image", StringComparison.OrdinalIgnoreCase))
        || attributeName.Equals("poster", StringComparison.OrdinalIgnoreCase)
            && node.Name.Equals("video", StringComparison.OrdinalIgnoreCase)
        || attributeName.Equals("data", StringComparison.OrdinalIgnoreCase)
            && node.Name.Equals("object", StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptedFallbackMedia(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".avif" or ".bmp" or ".gif" or ".jpeg" or ".jpg" or ".png" or ".svg" or ".webp"
        or ".aac" or ".m4a" or ".mp3" or ".oga" or ".ogg" or ".wav"
        or ".m4v" or ".mp4" or ".ogv" or ".webm";

    private static bool TryNormalizeEncryptedReferences(string? reference, out string[] normalized)
    {
        normalized = [];
        if (string.IsNullOrWhiteSpace(reference) || EpubArchivePathResolver.IsRemote(reference))
        {
            return false;
        }

        string path = reference.Split('#', '?')[0];
        HashSet<string> candidates = new(StringComparer.Ordinal);
        if (TryNormalizeEncryptedPath(path, out string encoded))
        {
            candidates.Add(encoded);
        }

        try
        {
            string decodedPath = Uri.UnescapeDataString(path);
            if (TryNormalizeEncryptedPath(decodedPath, out string decoded))
            {
                candidates.Add(decoded);
            }
        }
        catch (UriFormatException)
        {
            return false;
        }

        normalized = candidates.Order(StringComparer.Ordinal).ToArray();
        return normalized.Length > 0;
    }

    private static bool TryNormalizeEncryptedPath(string path, out string normalized) =>
        EpubArchivePathResolver.TryNormalizeEntryName(path, out normalized)
        || EpubArchivePathResolver.TryResolve("META-INF/encryption.xml", path, out normalized);

    private static void AddTriggerIssue(List<EpubInspectionIssue> issues, EpubInspectionProblem trigger)
    {
        EpubInspectionIssueCode code = trigger.IssueCode ?? EpubInspectionIssueCode.PartialCoverage;
        if (code == EpubInspectionIssueCode.PartialCoverage
            && issues.Any(issue => issue.Code is not EpubInspectionIssueCode.UnknownReadingOrder and not EpubInspectionIssueCode.PartialCoverage))
        {
            return;
        }

        if (!issues.Any(issue => issue.Code == code))
        {
            issues.Add(new(code, "Preflight"));
        }
    }

    private static EpubInspectionIssue[] BoundIssues(List<EpubInspectionIssue> issues, int maximum)
    {
        return issues
            .GroupBy(issue => issue.Code)
            .OrderBy(group => group.Key)
            .SelectMany(group =>
            {
                EpubInspectionIssue[] ordered = group
                    .OrderBy(issue => issue.Stage, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Item, StringComparer.Ordinal)
                    .ToArray();
                if (ordered.Length <= maximum)
                {
                    return ordered;
                }

                EpubInspectionIssue[] retained = ordered.Take(maximum).ToArray();
                EpubInspectionIssue last = retained[^1];
                retained[^1] = new(
                    last.Code,
                    last.Stage,
                    last.Item,
                    last.Observed,
                    last.Limit,
                    last.OmittedCount + ordered.Skip(maximum).Sum(issue => issue.OmittedCount + 1),
                    last.ExceptionType);
                return retained;
            })
            .ToArray();
    }

    private static async Task ValidateWithVersOneAsync(EpubInspectionRequest request, CancellationToken token)
    {
        await using FileStream file = OpenRead(request.FullPath);
        EnsureCurrentFile(request);
        using CancellationCheckingStream guarded = new(file, token);
        EpubReaderOptions options = new(EpubReaderOptionsPreset.RELAXED);
        // Tolerate non-conformant OPF packages whose root <package> element is not in
        // the expected OPF namespace (or is missing), so a malformed package node does
        // not abort inspection. OpenBookAsync then yields a null package reference and
        // the null-check below surfaces it as a controlled malformed-package failure.
        options.PackageReaderOptions.IgnoreMissingPackageNode = true;
        options.PackageReaderOptions.SkipInvalidManifestItems = true;
        options.Epub3NavDocumentReaderOptions.IgnoreMissingNavManifestItemError = true;
        options.Epub3NavDocumentReaderOptions.IgnoreMissingNavFileError = true;
        options.Epub3NavDocumentReaderOptions.IgnoreNavFileIsNotValidXmlError = true;
        // Tolerate non-conformant EPUB 2 NCX navigation points that omit required
        // content/label elements so a malformed toc.ncx does not abort inspection.
        options.Epub2NcxReaderOptions.IgnoreMissingContentForNavigationPoints = true;
        options.Epub2NcxReaderOptions.AllowNavigationPointsWithoutLabels = true;
        options.Epub2NcxReaderOptions.IgnoreTocFileIsNotValidXmlError = true;
        options.Epub2NcxReaderOptions.IgnoreMissingNavMapElementError = true;
        options.SpineReaderOptions.IgnoreMissingManifestItems = true;
        options.SpineReaderOptions.IgnoreMissingContentFiles = true;
        // Tolerate non-conformant EPUB 2 cover metadata that points at a manifest
        // item whose content file is missing or is not an image (e.g. a cover.xml
        // page) so a malformed cover reference does not abort inspection.
        options.BookCoverReaderOptions.Epub2MetadataIgnoreMissingManifestItem = true;
        options.BookCoverReaderOptions.Epub2MetadataIgnoreMissingContent = true;
        options.BookCoverReaderOptions.Epub2MetadataIgnoreMissingContentFile = true;
        options.ContentDownloaderOptions.DownloadContent = false;
        options.ContentDownloaderOptions.CustomContentDownloader = FailClosedContentDownloader.Instance;
        using EpubBookRef book = await EpubReader.OpenBookAsync(guarded, options).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EPUB reader did not return a package reference.");
        token.ThrowIfCancellationRequested();
        _ = book.Title;
    }

    private static async Task<EpubInspectionResult> ReadFactsAsync(
        EpubInspectionRequest request,
        IReadOnlyList<EpubInspectionProblem> recoverableProblems,
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
            XDocument? toc = null;
            try
            {
                toc = await ReadXmlAsync(tocEntry, request.Limits.MaximumXmlBytes, readBudget, token).ConfigureAwait(false);
            }
            catch (XmlException)
            {
            }

            if (toc is not null)
            {
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
            if (algorithms.Length == 0 || !algorithms.All(IsRecognizedFontObfuscation)) return Fail(request, EpubInspectionProblemCode.Encrypted, "Unsupported encryption or DRM prevents comparable EPUB inspection.", allowsFallbackInspection: false);
            encryptionState = "Recognized font obfuscation";
        }

        return new(
            request.BookId, request.ExpectedRelativePath, true, true, true, version, title, authors, languages, dates, identifiers,
            coverPresent, width, height, navigation, manifest.Count, spineIds.Length, chapterCount, manifest.Count,
            missingSpine.OrderedItems(), broken.OrderedItems(), emptyChapters.OrderedItems(),
            repeated.OrderedItems(), remote.OrderedItems(), readable, encryptionState, optionalTruncations.TotalCount > 0, [], coverHeaderMalformed,
            optionalTruncations.OrderedItems(), missingSpine.TotalCount, broken.TotalCount, emptyChapters.TotalCount, repeated.TotalCount, remote.TotalCount,
            recoverableProblems,
            Issues: recoverableProblems
                .Where(problem => problem.IssueCode is not null)
                .Select(problem => new EpubInspectionIssue(problem.IssueCode!.Value, "Preflight"))
                .ToArray());
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
        byte[] bytes;
        await using (Stream stream = entry.Open())
        {
            using LimitedReadStream limited = new(stream, maximumBytes, token, readBudget);
            using MemoryStream buffer = new();
            await limited.CopyToAsync(buffer, token).ConfigureAwait(false);
            bytes = buffer.GetBuffer();
            // Decode only the bytes actually read; GetBuffer may be oversized.
            bytes = bytes.AsSpan(0, (int)buffer.Length).ToArray();
        }

        // Decode with a replacement fallback so stray non-UTF-8 (or mislabelled)
        // bytes become U+FFFD instead of throwing; genuinely corrupt structure
        // still surfaces as an XmlException while parsing the resulting text.
        Encoding encoding = ResolveXmlEncoding(bytes);
        using MemoryStream source = new(bytes, writable: false);
        using StreamReader textReader = new(source, encoding, detectEncodingFromByteOrderMarks: true);
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = maximumBytes, Async = true };
        using XmlReader reader = XmlReader.Create(textReader, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
    }

    private static Encoding ResolveXmlEncoding(ReadOnlySpan<byte> bytes)
    {
        // The XML declaration (if any) is ASCII up to '?>'; scan a bounded prefix
        // for an encoding="..." attribute so a declared legacy encoding is honored.
        // A byte-order mark, when present, overrides this via the StreamReader.
        Encoding resolved = Encoding.UTF8;
        int limit = Math.Min(bytes.Length, 256);
        string prolog = Encoding.ASCII.GetString(bytes[..limit]);
        int declEnd = prolog.IndexOf("?>", StringComparison.Ordinal);
        if (prolog.StartsWith("<?xml", StringComparison.Ordinal) && declEnd > 0)
        {
            string? name = TryReadEncodingName(prolog.AsSpan(0, declEnd));
            if (name is not null)
            {
                try
                {
                    resolved = Encoding.GetEncoding(name);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    // Unknown name, or a recognized code page that is unavailable (e.g. the
                    // CodePages provider is not registered): fall back to UTF-8 rather than fail.
                    resolved = Encoding.UTF8;
                }
            }
        }

        Encoding tolerant = (Encoding)resolved.Clone();
        tolerant.DecoderFallback = DecoderFallback.ReplacementFallback;
        return tolerant;
    }

    private static string? TryReadEncodingName(ReadOnlySpan<char> declaration)
    {
        int encodingIndex = declaration.IndexOf("encoding", StringComparison.OrdinalIgnoreCase);
        if (encodingIndex < 0)
        {
            return null;
        }

        int cursor = encodingIndex + "encoding".Length;
        while (cursor < declaration.Length && char.IsWhiteSpace(declaration[cursor]))
        {
            cursor++;
        }

        if (cursor >= declaration.Length || declaration[cursor] != '=')
        {
            return null;
        }

        cursor++;
        while (cursor < declaration.Length && char.IsWhiteSpace(declaration[cursor]))
        {
            cursor++;
        }

        if (cursor >= declaration.Length || (declaration[cursor] != '"' && declaration[cursor] != '\''))
        {
            return null;
        }

        char quote = declaration[cursor];
        int start = cursor + 1;
        int end = declaration[start..].IndexOf(quote);
        if (end <= 0)
        {
            return null;
        }

        string name = declaration.Slice(start, end).Trim().ToString();
        return name.Length > 0 ? name : null;
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
            if (node.NodeType == HtmlAgilityPack.HtmlNodeType.Element && ++nodeCount > limits.MaximumHtmlNodes)
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
    private static void AddRecoverableProblem(
        List<EpubInspectionProblem> problems,
        EpubInspectionProblemCode code,
        string explanation,
        EpubInspectionIssueCode issueCode)
    {
        if (!problems.Any(problem => problem.Code == code && problem.IssueCode == issueCode))
        {
            problems.Add(new(code, explanation, IssueCode: issueCode));
        }
    }
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
        string value = reference.Replace("\u2028", string.Empty, StringComparison.Ordinal).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)
            && Uri.TryCreate($"https:{value}", UriKind.Absolute, out Uri? protocolRelative))
        {
            return SanitizeParsedExternalReference(protocolRelative, "protocol-relative");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return SanitizeParsedExternalReference(uri, uri.Scheme);
        }

        return "scheme:external";
    }

    private static string SanitizeParsedExternalReference(Uri uri, string scheme)
    {
        try
        {
            if (uri.IsFile || string.IsNullOrEmpty(uri.Host))
            {
                return Bound($"scheme:{scheme}")!;
            }

            string host = uri.IdnHost;
            return Bound(string.IsNullOrEmpty(host)
                ? $"scheme:{scheme};host:invalid"
                : $"scheme:{scheme};host:{host}")!;
        }
        catch (UriFormatException)
        {
            return Bound($"scheme:{scheme};host:invalid")!;
        }
    }
    private static EpubInspectionResult Fail(
        EpubInspectionRequest request,
        EpubInspectionProblemCode code,
        string explanation,
        bool allowsFallbackInspection = true) => EpubInspectionResult.Failed(
            request.BookId,
            request.ExpectedRelativePath,
            code,
            explanation) with
        {
            Problems = [new(code, explanation, AllowsFallbackInspection: allowsFallbackInspection)],
        };

    private sealed record CentralDirectoryEntry(bool Encrypted, ushort CompressionMethod);
    private sealed record CentralDirectoryInspection(IReadOnlyList<CentralDirectoryEntry> Entries);
    private sealed record FallbackEntry(string Name, ZipArchiveEntry Entry, CentralDirectoryEntry Central);
    private readonly record struct LocalMediaInspection(bool Found, bool LimitExceeded, int InspectedCount);
    private sealed record ManifestItem(string Path, string MediaType, string Properties);
    private sealed record PreflightResult(
        IReadOnlyList<string> EntryNames,
        EpubInspectionProblem? Problem,
        IReadOnlyList<EpubInspectionProblem> RecoverableProblems)
    {
        public static PreflightResult Fail(
            EpubInspectionProblemCode code,
            string explanation,
            EpubInspectionIssueCode? issueCode = null,
            bool allowsFallbackInspection = true) => new(
                [],
                new(code, explanation, IssueCode: issueCode, AllowsFallbackInspection: allowsFallbackInspection),
                []);
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
