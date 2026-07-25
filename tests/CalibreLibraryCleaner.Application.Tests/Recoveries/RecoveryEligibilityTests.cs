using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

public sealed class RecoveryEligibilityTests
{
    [Fact]
    public void FullyVerifiedMatchingSourceAndCurrentStateIsEligible()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            source, current, current.Snapshot.Identity.LibraryRoot, profile, false),
            DateTimeOffset.UtcNow);

        result.IsEligible.Should().BeTrue();
        result.BlockingIssues.Should().BeEmpty();
    }

    [Fact]
    public void MissingPlanJournalOrManifestFailsClosed()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoverySourceInspection incomplete = source with
        {
            CleanupPlan = null,
            Journal = null,
            OriginalBackupManifest = null,
        };

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            incomplete, current, current.Snapshot.Identity.LibraryRoot, profile, false),
            DateTimeOffset.UtcNow);

        result.IsEligible.Should().BeFalse();
        result.BlockingIssues.Should().Contain(value =>
            value.Code == "RECOVERY.SOURCE_ARTIFACTS_INCOMPLETE");
    }

    [Fact]
    public void UnknownJournalBackupOrderAndConflictAreSeparateBlockers()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoverySourceInspection contradictory = source with
        {
            Journal = source.Journal! with
            {
                HasKnownDurableState = false,
                OriginalBackupVerifiedBeforeMutation = false,
            },
        };

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            contradictory, current, current.Snapshot.Identity.LibraryRoot, profile, true),
            DateTimeOffset.UtcNow);

        result.BlockingIssues.Select(value => value.Code).Should().Contain([
            "RECOVERY.JOURNAL_STATE_UNKNOWN",
            "RECOVERY.ORIGINAL_BACKUP_NOT_VERIFIED_BEFORE_MUTATION",
            "RECOVERY.CONFLICTING_MUTATION_OR_RECOVERY"]);
    }

    [Fact]
    public void DisabledIndividualCapabilitiesBlockMandatoryBackupAndVerification()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoveryCapabilityProfile disabled = new(profile.ProfileIdentity, profile.ToolIdentity,
            profile.Capabilities.Select(value =>
                value.Capability is RecoveryCapability.ExportCurrentRecord
                    or RecoveryCapability.VerifyRestoredContent
                    ? value with { Enabled = false }
                    : value));

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            source, current, current.Snapshot.Identity.LibraryRoot, disabled, false),
            DateTimeOffset.UtcNow);

        result.BlockingIssues.Select(value => value.Code).Should().Contain([
            "RECOVERY.CURRENT_BACKUP_EXPORT_UNSUPPORTED",
            "RECOVERY.SEMANTIC_VERIFICATION_UNSUPPORTED"]);
    }

    [Fact]
    public void UnsupportedSourceApplicationVersionFailsClosed()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoverySourceInspection unsupported = source with
        {
            SourceApplicationVersion = "2.0.0",
            Journal = source.Journal! with { ApplicationVersion = "2.0.0" },
        };

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            unsupported, current, current.Snapshot.Identity.LibraryRoot, profile, false),
            DateTimeOffset.UtcNow);

        result.BlockingIssues.Should().Contain(value =>
            value.Code == "RECOVERY.SOURCE_APPLICATION_UNSUPPORTED");
    }

    [Fact]
    public void AssemblyVersionStampFromMilestone7IsSupported()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoverySourceInspection stamped = source with
        {
            SourceApplicationVersion = "1.0.0.0",
            Journal = source.Journal! with { ApplicationVersion = "1.0.0.0" },
        };

        RecoveryEligibilityResult result = new RecoveryEligibilityValidator().Evaluate(new(
            stamped, current, current.Snapshot.Identity.LibraryRoot, profile, false),
            DateTimeOffset.UtcNow);

        result.BlockingIssues.Should().NotContain(value =>
            value.Code == "RECOVERY.SOURCE_APPLICATION_UNSUPPORTED");
    }
}
