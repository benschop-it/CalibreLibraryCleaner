namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal static class CalibreCompatibilityPolicy
{
    public static bool IsBuiltInConfiguration(CalibreExecutionOptions options) =>
        string.Equals(options.MinimumSupportedVersion,
            CalibreExecutionOptions.InitialMinimumSupportedVersion, StringComparison.Ordinal)
        && string.Equals(options.MaximumExclusiveVersion,
            CalibreExecutionOptions.InitialMaximumExclusiveVersion, StringComparison.Ordinal)
        && string.Equals(options.CapabilityProfile,
            CalibreExecutionOptions.InitialCapabilityProfile, StringComparison.Ordinal);

    public static bool IsSupportedVersion(
        string version,
        CalibreExecutionOptions options) =>
        TryParse(version, out Version? actual)
        && TryParse(options.MinimumSupportedVersion, out Version? minimum)
        && TryParse(options.MaximumExclusiveVersion, out Version? maximum)
        && actual >= minimum
        && actual < maximum;

    private static bool TryParse(string value, out Version? version)
    {
        if (!Version.TryParse(value, out version)) return false;
        version = new(version.Major, Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
        return true;
    }
}
