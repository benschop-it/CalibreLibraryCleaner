using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

public sealed record CalibreExecutionOptions
{
    public const string InitialMinimumSupportedVersion = "9.11.0";
    public const string InitialMaximumExclusiveVersion = "10.0.0";
    public const string InitialCapabilityProfile = "calibredb/windows/9.x";

    public string TrustedExecutablePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Calibre2",
        "calibredb.exe");

    public string MinimumSupportedVersion { get; init; } = InitialMinimumSupportedVersion;
    public string MaximumExclusiveVersion { get; init; } = InitialMaximumExclusiveVersion;
    public string CapabilityProfile { get; init; } = InitialCapabilityProfile;
    public bool IsValidatedCompatibilityProfileEnabled { get; init; } = true;
    public bool IsValidatedRecoveryProfileEnabled { get; init; }
    public IReadOnlySet<RecoveryCapability> EnabledRecoveryCapabilities { get; init; } =
        new HashSet<RecoveryCapability>();
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadOnlyCommandTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan WorkerStartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WorkerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int WorkerMaximumMessageBytes { get; init; } = 1_048_576;
    public int MaximumCapturedCharacters { get; init; } = 32_768;
    public string ControlledConfigDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "calibre-config",
        "9.x");
}

public sealed record ExecutionStorageOptions
{
    public string LeaseRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "leases");

    public string HistoryRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "execution-history");

    public string TransferStagingRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "transfer-staging");
}
