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
        if (!string.Equals(options.SupportedVersion, CalibreExecutionOptions.InitialSupportedVersion, StringComparison.Ordinal)
            || !string.Equals(options.CapabilityProfile, CalibreExecutionOptions.InitialCapabilityProfile, StringComparison.Ordinal))
            return Failure("EXECUTION.CALIBRE_PROFILE_CONFIGURATION_UNSUPPORTED",
                "Only the built-in exact Calibre 9.11.0 compatibility profile can be enabled.");
        if (!options.IsValidatedCompatibilityProfileEnabled)
            return Failure("EXECUTION.CALIBRE_PROFILE_NOT_VALIDATED",
                "The exact Calibre 9.11.0 profile remains disabled until its opt-in disposable-library compatibility suite passes.");
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

        await using (executableLock.ConfigureAwait(false))
        {
            CalibreCommandResult versionResult = await processRunner.RunAsync(executable, libraryRoot, "version-probe", ["--version"],
                [executable], true, options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (!versionResult.IsSuccess)
                return Failure("EXECUTION.CALIBRE_VERSION_PROBE_FAILED", "The trusted Calibre executable did not return a valid version result.");
            Match versionMatch = VersionPattern().Match(versionResult.SanitizedStandardOutput + "\n" + versionResult.SanitizedStandardError);
            if (!versionMatch.Success || !string.Equals(versionMatch.Groups[1].Value, options.SupportedVersion, StringComparison.Ordinal))
                return Failure("EXECUTION.CALIBRE_VERSION_UNSUPPORTED", $"Only exact Calibre version {options.SupportedVersion} is supported by this capability profile.");

            Dictionary<string, string[]> probes = new(StringComparer.Ordinal)
            {
                ["global-help"] = ["--help"],
                ["add-format-help"] = ["add_format", "--help"],
                ["remove-help"] = ["remove", "--help"],
                ["export-help"] = ["export", "--help"],
            };
            Dictionary<string, string> help = new(StringComparer.Ordinal);
            foreach ((string name, string[] arguments) in probes)
            {
                CalibreCommandResult result = await processRunner.RunAsync(executable, libraryRoot, name, arguments,
                    [executable], true, options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    issues.Add(Block("EXECUTION.CALIBRE_CAPABILITY_PROBE_FAILED", "A required Calibre command help probe failed."));
                    continue;
                }
                help[name] = result.SanitizedStandardOutput + "\n" + result.SanitizedStandardError;
            }

            if (!ContainsAll(help.GetValueOrDefault("global-help"), "--with-library")
                || !ContainsAll(help.GetValueOrDefault("add-format-help"), "add_format", "--dont-replace")
                || !ContainsAll(help.GetValueOrDefault("remove-help"), "remove", "--permanent")
                || !ContainsAll(help.GetValueOrDefault("export-help"), "export", "--dont-save-extra-files",
                    "--dont-update-metadata", "--to-dir", "--single-dir"))
                issues.Add(Block("EXECUTION.CALIBRE_CAPABILITY_UNKNOWN", "The exact required documented Calibre commands and options could not be confirmed."));
        }

        if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError)) return new(null, issues);
        ExecutionToolIdentity identity = new(executable, options.SupportedVersion, digest, options.CapabilityProfile);
        CalibreToolDescriptor descriptor = new(executable, identity, Enum.GetValues<CalibreExecutionCapability>());
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

    private static bool ContainsAll(string? text, params string[] values) => text is not null
        && values.All(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static CalibreToolDiscoveryResult Failure(string code, string explanation) => new(null, [Block(code, explanation)]);
    private static ExecutionIssue Block(string code, string explanation) => new(code, ExecutionIssueSeverity.BlockingError, explanation);

    [GeneratedRegex(@"\bcalibre\s+(\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
