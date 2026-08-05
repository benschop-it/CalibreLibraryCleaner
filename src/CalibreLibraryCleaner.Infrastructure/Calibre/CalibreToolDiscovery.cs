using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed partial class CalibreToolDiscovery(
    CalibreExecutionOptions options,
    DirectCalibreProcessRunner processRunner) : ICalibreToolDiscovery
{
    public async Task<CalibreToolDiscoveryResult> DiscoverAndProbeAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        List<ExecutionIssue> issues = [];
        if (!CalibreCompatibilityPolicy.IsBuiltInConfiguration(options))
            return Failure("EXECUTION.CALIBRE_PROFILE_CONFIGURATION_UNSUPPORTED",
                "Only the built-in Calibre 9.x compatibility profile can be enabled.");
        if (!options.IsValidatedCompatibilityProfileEnabled)
            return Failure("EXECUTION.CALIBRE_PROFILE_NOT_VALIDATED",
                "The Calibre 9.x profile is disabled by configuration.");
        string executable;
        try { executable = Path.GetFullPath(options.TrustedExecutablePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("EXECUTION.CALIBRE_PATH_INVALID", "The configured trusted Calibre executable path is invalid.");
        }

        if (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "calibredb.exe", StringComparison.OrdinalIgnoreCase)
            || !ExecutionPathGuard.TryRejectReparsePoints(executable, true, out _))
            return Failure("EXECUTION.CALIBRE_NOT_FOUND", "The exact trusted calibredb executable was not found as a regular physical file.");
        try
        {
            FileAttributes attributes = File.GetAttributes(executable);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return Failure("EXECUTION.CALIBRE_NOT_REGULAR", "The trusted Calibre executable is not a regular file.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("EXECUTION.CALIBRE_INACCESSIBLE", "The trusted Calibre executable cannot be inspected.");
        }

        FileStream executableLock;
        Sha256Digest digest;
        try
        {
            executableLock = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            digest = await HashStreamAsync(executableLock, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("EXECUTION.CALIBRE_HASH_FAILED", "The trusted Calibre executable could not be hashed reliably.");
        }

        string productVersion;
        await using (executableLock.ConfigureAwait(false))
        {
            CalibreCommandResult versionResult = await processRunner.RunAsync(executable, libraryRoot, "version-probe", ["--version"],
                [executable], true, options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (!versionResult.IsSuccess)
                return Failure("EXECUTION.CALIBRE_VERSION_PROBE_FAILED", "The trusted Calibre executable did not return a valid version result.");
            Match versionMatch = VersionPattern().Match(versionResult.SanitizedStandardOutput + "\n" + versionResult.SanitizedStandardError);
            string actualVersion = versionMatch.Success ? versionMatch.Groups[1].Value : string.Empty;
            if (!versionMatch.Success || !CalibreCompatibilityPolicy.IsSupportedVersion(actualVersion, options))
                return Failure("EXECUTION.CALIBRE_VERSION_UNSUPPORTED",
                    $"Supported Calibre versions are {options.MinimumSupportedVersion} or newer, but below {options.MaximumExclusiveVersion}.");
            productVersion = actualVersion;
        }

        if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError)) return new(null, issues);
        ExecutionToolIdentity identity = new(executable, productVersion, digest, options.CapabilityProfile);
        CalibreToolDescriptor descriptor = new(executable, identity);
        return new(descriptor, Array.AsReadOnly(issues.ToArray()));
    }

    internal static async Task<Sha256Digest> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Sha256Digest> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new(Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static CalibreToolDiscoveryResult Failure(string code, string explanation) => new(null, [Block(code, explanation)]);
    private static ExecutionIssue Block(string code, string explanation) => new(code, ExecutionIssueSeverity.BlockingError, explanation);

    [GeneratedRegex(@"\bcalibre\s+(\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
