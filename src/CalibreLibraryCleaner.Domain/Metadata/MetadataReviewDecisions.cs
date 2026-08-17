using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public sealed record MetadataReviewEditionIdentity
{
    public MetadataReviewEditionIdentity(
        EditionMetadataProviderIdentity provider,
        string editionId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(editionId);
        if (editionId.Length > 160)
            throw new ArgumentOutOfRangeException(nameof(editionId));
        Provider = provider;
        EditionId = editionId.Trim();
    }

    public EditionMetadataProviderIdentity Provider { get; }
    public string EditionId { get; }
}

public sealed record MetadataReviewDecisionKey
{
    public MetadataReviewDecisionKey(
        MetadataReviewSubjectId subjectId,
        string proposalPolicyVersion,
        MetadataReviewEditionIdentity editionIdentity,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalPolicyVersion);
        ArgumentNullException.ThrowIfNull(editionIdentity);
        ArgumentNullException.ThrowIfNull(generationId);
        if (proposalPolicyVersion.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(proposalPolicyVersion));
        SubjectId = subjectId;
        ProposalPolicyVersion = proposalPolicyVersion.Trim();
        EditionIdentity = editionIdentity;
        GenerationId = generationId;
        Revision = revision;
    }

    public MetadataReviewSubjectId SubjectId { get; }
    public string ProposalPolicyVersion { get; }
    public MetadataReviewEditionIdentity EditionIdentity { get; }
    public LibraryStateGenerationId GenerationId { get; }
    public LibraryStateRevision Revision { get; }
}

public sealed record MetadataReviewDecision
{
    public MetadataReviewDecision(MetadataReviewDecisionKey key, bool apply)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Apply = apply;
    }

    public MetadataReviewDecisionKey Key { get; }
    public bool Apply { get; }
}

public sealed record ReviewedMetadataSubject
{
    public ReviewedMetadataSubject(
        MetadataReviewSubject subject,
        bool apply,
        bool isOverride,
        MetadataReviewDecisionKey? decisionKey)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable
            && (apply || isOverride || decisionKey is not null)
            || isOverride != (decisionKey is not null)
            || decisionKey is not null && decisionKey.SubjectId != subject.Id)
            throw new ArgumentException("Reviewed metadata subject is invalid.");
        Subject = subject;
        Apply = apply;
        IsOverride = isOverride;
        DecisionKey = decisionKey;
    }

    public MetadataReviewSubject Subject { get; }
    public bool Apply { get; }
    public bool IsOverride { get; }
    public MetadataReviewDecisionKey? DecisionKey { get; }
}

public static class MetadataReviewDecisionPolicy
{
    public static IReadOnlyList<ReviewedMetadataSubject> Reconcile(
        IEnumerable<MetadataReviewSubject> subjects,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        IEnumerable<MetadataReviewDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(decisions);
        MetadataReviewDecision[] values = decisions.ToArray();
        if (values.Select(value => value.Key).Distinct().Count() != values.Length)
            throw new ArgumentException("Metadata review decisions must have unique keys.", nameof(decisions));
        Dictionary<MetadataReviewDecisionKey, MetadataReviewDecision> byKey = values.ToDictionary(value => value.Key);
        ReviewedMetadataSubject[] reviewed = subjects.OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .Select(subject => Reconcile(subject, generationId, revision, byKey)).ToArray();
        return new ReadOnlyCollection<ReviewedMetadataSubject>(reviewed);
    }

    public static MetadataReviewDecision? CreateOverride(
        MetadataReviewSubject subject,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(generationId);
        if (subject.Proposal.Candidate is null || subject.Proposal.PrimaryProvider is null
            || apply == subject.Proposal.IsSelectedByDefault)
            return null;
        return new(CreateKey(subject, generationId, revision), apply);
    }

    public static MetadataReviewDecisionKey CreateKey(
        MetadataReviewSubject subject,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(generationId);
        if (subject.Proposal.Candidate is null || subject.Proposal.PrimaryProvider is null)
            throw new ArgumentException("Unavailable metadata subjects do not have decision keys.", nameof(subject));
        return new(
            subject.Id,
            subject.Proposal.PolicyVersion,
            new(subject.Proposal.PrimaryProvider, subject.Proposal.Candidate.EditionId),
            generationId,
            revision);
    }

    private static ReviewedMetadataSubject Reconcile(
        MetadataReviewSubject subject,
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        Dictionary<MetadataReviewDecisionKey, MetadataReviewDecision> decisions)
    {
        bool defaultApply = subject.Proposal.IsSelectedByDefault;
        if (subject.Proposal.Candidate is null || subject.Proposal.PrimaryProvider is null)
            return new(subject, false, false, null);
        MetadataReviewDecisionKey key = CreateKey(subject, generationId, revision);
        return decisions.TryGetValue(key, out MetadataReviewDecision? decision)
            ? new(subject, decision.Apply, true, key)
            : new(subject, defaultApply, false, null);
    }
}
