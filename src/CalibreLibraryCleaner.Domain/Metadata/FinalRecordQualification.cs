using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum FinalRecordQualification
{
    NeedsReview,
    Repairable,
    Qualified,
    ProposedUnresolvable,
}

public enum FinalRecordRouteOutcome
{
    NotNeeded,
    NotAttempted,
    CompletedWithEvidence,
    CompletedWithoutEvidence,
    Conflicting,
    Unavailable,
    DeferredByBounds,
}

public enum FinalRecordContentStatus
{
    Incomplete,
    Usable,
    ConfirmedUnusable,
}

public sealed record FinalRecordResolutionRoute
{
    public FinalRecordResolutionRoute(string name, string version, FinalRecordRouteOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (name.Length > 128 || version.Length > 128
            || name.Any(char.IsControl) || version.Any(char.IsControl)
            || !Enum.IsDefined(outcome))
            throw new ArgumentException("Final record resolution route is invalid.");
        Name = name;
        Version = version;
        Outcome = outcome;
    }

    public string Name { get; }
    public string Version { get; }
    public FinalRecordRouteOutcome Outcome { get; }
    public bool IsCompleted => Outcome is FinalRecordRouteOutcome.CompletedWithEvidence
        or FinalRecordRouteOutcome.CompletedWithoutEvidence;
}

public sealed record FinalRecordQualificationEvidence
{
    public FinalRecordQualificationEvidence(
        bool titleSupported,
        bool authorsSupported,
        FinalRecordContentStatus content,
        bool managedLayoutVerified,
        bool materialIdentityProblem,
        bool concreteRepairAvailable,
        bool conflicting,
        IEnumerable<FinalRecordResolutionRoute> routes,
        bool reviewerAccepted = false,
        bool manuallyAssigned = false)
    {
        ArgumentNullException.ThrowIfNull(routes);
        FinalRecordResolutionRoute[] values = routes.Take(33).ToArray();
        if (!Enum.IsDefined(content) || values.Length > 32
            || values.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Final record qualification evidence is invalid.");
        TitleSupported = titleSupported;
        AuthorsSupported = authorsSupported;
        Content = content;
        ManagedLayoutVerified = managedLayoutVerified;
        MaterialIdentityProblem = materialIdentityProblem;
        ConcreteRepairAvailable = concreteRepairAvailable;
        Conflicting = conflicting;
        Routes = new ReadOnlyCollection<FinalRecordResolutionRoute>(values);
        ReviewerAccepted = reviewerAccepted;
        ManuallyAssigned = manuallyAssigned;
    }

    public bool TitleSupported { get; }
    public bool AuthorsSupported { get; }
    public FinalRecordContentStatus Content { get; }
    public bool ManagedLayoutVerified { get; }
    public bool MaterialIdentityProblem { get; }
    public bool ConcreteRepairAvailable { get; }
    public bool Conflicting { get; }
    public IReadOnlyList<FinalRecordResolutionRoute> Routes { get; }
    public bool ReviewerAccepted { get; }
    public bool ManuallyAssigned { get; }
    public bool ResolutionExhausted => Routes.Any(value => value.Outcome != FinalRecordRouteOutcome.NotNeeded)
        && Routes.Where(value => value.Outcome != FinalRecordRouteOutcome.NotNeeded)
            .All(value => value.IsCompleted);
}

public static class FinalRecordQualificationPolicy
{
    public const string PolicyVersion = "final-record-qualification/1.0.0";

    public static FinalRecordQualification Assess(FinalRecordQualificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ManuallyAssigned)
            return FinalRecordQualification.ProposedUnresolvable;
        if (evidence.Content == FinalRecordContentStatus.ConfirmedUnusable)
            return FinalRecordQualification.ProposedUnresolvable;
        if (evidence.ConcreteRepairAvailable)
            return FinalRecordQualification.Repairable;
        if (evidence.ReviewerAccepted && evidence.ManagedLayoutVerified
            && evidence.Content != FinalRecordContentStatus.ConfirmedUnusable)
            return FinalRecordQualification.Qualified;
        if (evidence.Conflicting)
            return FinalRecordQualification.NeedsReview;
        if (evidence.TitleSupported && evidence.AuthorsSupported
            && evidence.Content == FinalRecordContentStatus.Usable
            && evidence.ManagedLayoutVerified && !evidence.MaterialIdentityProblem)
            return FinalRecordQualification.Qualified;
        if (evidence.MaterialIdentityProblem && evidence.ResolutionExhausted)
            return FinalRecordQualification.ProposedUnresolvable;
        return FinalRecordQualification.NeedsReview;
    }
}
