namespace CalibreLibraryCleaner.Infrastructure.Calibre;

public sealed record CalibreExecutionOptions
{
    public const string InitialSupportedVersion = "9.11.0";
    public const string InitialCapabilityProfile = "calibredb/windows/9.11.0";

    public string TrustedExecutablePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Calibre2",
        "calibredb.exe");

    public string SupportedVersion { get; init; } = InitialSupportedVersion;
    public string CapabilityProfile { get; init; } = InitialCapabilityProfile;
    public bool IsValidatedCompatibilityProfileEnabled { get; init; }
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadOnlyCommandTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumCapturedCharacters { get; init; } = 32_768;
    public string ControlledConfigDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "calibre-config",
        InitialSupportedVersion);
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
}
