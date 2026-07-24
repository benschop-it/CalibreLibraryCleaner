namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanLineagePolicy
{
    public static bool IsCompatible(CleanupPlan existing, CleanupPlan candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        if (existing.Id != candidate.Id) return true;
        if (existing.SchemaVersion != candidate.SchemaVersion
            || existing.PolicyVersion != candidate.PolicyVersion
            || existing.ContentDigest != candidate.ContentDigest
            || existing.CreatedAtUtc != candidate.CreatedAtUtc
            || !SameInput(existing.InputIdentity, candidate.InputIdentity))
            return false;

        CleanupPlan shorter = existing.ArtifactRevision.Value <= candidate.ArtifactRevision.Value ? existing : candidate;
        CleanupPlan longer = ReferenceEquals(shorter, existing) ? candidate : existing;
        return shorter.LifecycleHistory.Count <= longer.LifecycleHistory.Count
            && shorter.LifecycleHistory.SequenceEqual(longer.LifecycleHistory.Take(shorter.LifecycleHistory.Count));
    }

    private static bool SameInput(CleanupPlanInputIdentity left, CleanupPlanInputIdentity right) =>
        left.LibraryUuid == right.LibraryUuid && left.SchemaVersion == right.SchemaVersion
        && left.GroupId == right.GroupId && left.MemberIds.SequenceEqual(right.MemberIds)
        && left.RecommendationModelVersion == right.RecommendationModelVersion
        && left.RecommendationInputVersion == right.RecommendationInputVersion
        && left.PolicyVersion == right.PolicyVersion && left.DefinitionDigest == right.DefinitionDigest;
}
