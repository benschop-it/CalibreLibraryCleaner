using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class CalibreRecoveryExecutionProfileProvider(
    CalibreExecutionOptions options) : ICalibreExecutionProfileProvider
{
    public RecoveryCapabilityProfile EvaluateRecoveryProfile(CalibreToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        bool exactTool = options.IsValidatedCompatibilityProfileEnabled
            && tool.Identity.ProductVersion == options.SupportedVersion
            && tool.Identity.CapabilityProfile == options.CapabilityProfile;
        RecoveryCapabilityStatus[] statuses = Enum.GetValues<RecoveryCapability>()
            .Select(capability =>
            {
                bool documented = Documented(capability);
                bool enabled = documented && exactTool && options.IsValidatedRecoveryProfileEnabled
                    && options.EnabledRecoveryCapabilities.Contains(capability);
                return new RecoveryCapabilityStatus(
                    capability,
                    documented,
                    enabled,
                    enabled,
                    enabled,
                    enabled
                        ? "Exact-version recovery capability is enabled by the validated profile."
                        : "Capability is disabled until its closed mapping and opt-in real-Calibre qualification pass.");
            }).ToArray();
        return new($"{options.CapabilityProfile}/recovery/1.0", tool.Identity, statuses);
    }

    private static bool Documented(RecoveryCapability capability) => capability is
        RecoveryCapability.ExportCurrentRecord
        or RecoveryCapability.CreateEmptyRecord
        or RecoveryCapability.FindCreatedRecord
        or RecoveryCapability.AddBackedUpFormat
        or RecoveryCapability.ReplaceExistingFormat
        or RecoveryCapability.RestoreTitle
        or RecoveryCapability.RestoreAuthors
        or RecoveryCapability.RestoreAuthorSort
        or RecoveryCapability.RestorePublisher
        or RecoveryCapability.RestorePublicationDate
        or RecoveryCapability.RestoreLanguages
        or RecoveryCapability.RestoreIdentifiers
        or RecoveryCapability.RestoreSeries
        or RecoveryCapability.RestoreSeriesIndex
        or RecoveryCapability.RemoveCleanupAddedFormat
        or RecoveryCapability.RemoveCleanupCreatedRecord
        or RecoveryCapability.VerifyRestoredContent;
}
