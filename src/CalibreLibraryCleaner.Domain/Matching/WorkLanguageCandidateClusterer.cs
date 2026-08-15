using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public enum CandidatePairDisposition
{
    Rejected,
    Weak,
    Strong,
    Anchor,
}

public sealed record BookCandidateDecision
{
    public BookCandidateDecision(
        BookCandidatePair pair,
        CandidatePairDisposition disposition,
        IEnumerable<CandidateEvidence> evidence,
        IEnumerable<CandidateContradiction>? contradictions,
        CandidateContentComparison? contentComparison = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        CandidateEvidence[] orderedEvidence = evidence.Distinct()
            .OrderByDescending(value => value.Strength)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.SourceId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.SourceVersion ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Provenance?.ResultId ?? string.Empty, StringComparer.Ordinal).ToArray();
        CandidateContradiction[] orderedContradictions = (contradictions ?? []).Distinct()
            .OrderBy(value => value.Code, StringComparer.Ordinal).ToArray();
        if (orderedEvidence.Length == 0) throw new ArgumentException("A candidate decision requires evidence.");
        Pair = pair;
        Disposition = disposition;
        Evidence = new ReadOnlyCollection<CandidateEvidence>(orderedEvidence);
        Contradictions = new ReadOnlyCollection<CandidateContradiction>(orderedContradictions);
        ContentComparison = contentComparison ?? EpubContentSignatureComparer.Unavailable();
    }

    public BookCandidatePair Pair { get; }
    public CandidatePairDisposition Disposition { get; }
    public IReadOnlyList<CandidateEvidence> Evidence { get; }
    public IReadOnlyList<CandidateContradiction> Contradictions { get; }
    public CandidateContentComparison ContentComparison { get; }
}

public static class BookCandidateDecisionPolicy
{
    public static IReadOnlyList<BookCandidateDecision> Decide(
        IEnumerable<BookCandidatePair> pairs,
        IReadOnlyDictionary<BookCandidatePairId, CandidateContentComparison>? contentComparisons = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Dictionary<BookCandidatePairId, CandidateContentComparison> content = contentComparisons is null
            ? []
            : new(contentComparisons);
        List<BookCandidateDecision> decisions = [];
        foreach (BookCandidatePair pair in pairs.OrderBy(value => value.Id.First.Value)
            .ThenBy(value => value.Id.Second.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateContentComparison comparison = content.GetValueOrDefault(
                pair.Id, EpubContentSignatureComparer.Unavailable());
            List<CandidateEvidence> evidence = [.. pair.Evidence];
            List<CandidateContradiction> contradictions = [.. pair.Contradictions];
            if (comparison.Classification == ContentSimilarityClassification.EquivalentText)
                evidence.Add(new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor));
            else if (comparison.Classification == ContentSimilarityClassification.HighSimilarity)
                evidence.Add(new("MATCH.CONTENT.HIGH_SIMILARITY", CandidateEvidenceStrength.Anchor));
            else if (comparison.Classification == ContentSimilarityClassification.Ambiguous)
                evidence.Add(new("MATCH.CONTENT.AMBIGUOUS", CandidateEvidenceStrength.Weak));
            else if (comparison.Classification == ContentSimilarityClassification.Different)
                contradictions.Add(new("MATCH.CONTENT.DIFFERENT"));
            else if (pair.NeedsContentEvidence)
                evidence.Add(new("MATCH.CONTENT.UNAVAILABLE", CandidateEvidenceStrength.Weak));

            bool decisiveContradiction = contradictions.Any(value => value.Code is
                "MATCH.LANGUAGE.CONFLICT" or "MATCH.SERIES_INDEX.CONFLICT" or "MATCH.CONTENT.DIFFERENT");
            CandidatePairDisposition disposition = decisiveContradiction
                ? CandidatePairDisposition.Rejected
                : pair.HasAnchor
                    || comparison.Classification is ContentSimilarityClassification.EquivalentText
                        or ContentSimilarityClassification.HighSimilarity
                    ? CandidatePairDisposition.Anchor
                    : pair.NeedsContentEvidence
                        ? CandidatePairDisposition.Weak
                        : pair.CheapScore >= 1_200
                            ? CandidatePairDisposition.Strong
                            : CandidatePairDisposition.Weak;
            decisions.Add(new(pair, disposition, evidence, contradictions, comparison));
        }
        return decisions;
    }
}

public static class WorkLanguageCandidateClusterer
{
    public static IReadOnlyList<WorkLanguageCandidateGroup> Cluster(
        IEnumerable<BookMatchingProfile> profiles,
        IEnumerable<BookCandidateDecision> decisions,
        MatchingPolicyVersion? policyVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(decisions);
        BookMatchingProfile[] profileValues = profiles.OrderBy(value => value.BookId.Value).ToArray();
        if (profileValues.Select(value => value.BookId).Distinct().Count() != profileValues.Length)
            throw new ArgumentException("Matching profiles must have unique record IDs.", nameof(profiles));
        Dictionary<CalibreBookId, BookMatchingProfile> profileById = profileValues.ToDictionary(value => value.BookId);
        BookCandidateDecision[] decisionValues = decisions.OrderByDescending(value => value.Disposition)
            .ThenByDescending(value => value.Pair.CheapScore)
            .ThenBy(value => value.Pair.Id.First.Value)
            .ThenBy(value => value.Pair.Id.Second.Value).ToArray();
        if (decisionValues.Select(value => value.Pair.Id).Distinct().Count() != decisionValues.Length
            || decisionValues.Any(value => !profileById.ContainsKey(value.Pair.Id.First)
                || !profileById.ContainsKey(value.Pair.Id.Second)))
            throw new ArgumentException("Candidate decisions must be unique and reference matching profiles.", nameof(decisions));
        Dictionary<BookCandidatePairId, BookCandidateDecision> decisionByPair = decisionValues
            .ToDictionary(value => value.Pair.Id);
        Dictionary<CalibreBookId, Component> componentByMember = profileValues
            .ToDictionary(value => value.BookId, value => new Component([value.BookId], []));

        foreach (BookCandidateDecision decision in decisionValues.Where(value => value.Disposition == CandidatePairDisposition.Anchor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Merge(decision, isAnchor: true, componentByMember, profileById, decisionByPair);
        }
        foreach (BookCandidateDecision decision in decisionValues.Where(value => value.Disposition == CandidatePairDisposition.Strong))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Merge(decision, isAnchor: false, componentByMember, profileById, decisionByPair);
        }

        MatchingPolicyVersion version = policyVersion ?? MatchingPolicyVersion.Current;
        List<WorkLanguageCandidateGroup> groups = [];
        foreach (Component component in componentByMember.Values.Distinct()
            .Where(value => value.Members.Count >= 2)
            .OrderBy(value => value.Members.Min(member => member.Value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            BookCandidateDecision[] internalDecisions = decisionValues.Where(value =>
                value.Disposition is CandidatePairDisposition.Anchor or CandidatePairDisposition.Strong
                && component.Members.Contains(value.Pair.Id.First)
                && component.Members.Contains(value.Pair.Id.Second)).ToArray();
            CandidateEvidence[] evidence = internalDecisions.SelectMany(value => value.Evidence)
                .Distinct().ToArray();
            string language = ResolveLanguage(component.Members.Select(value => profileById[value]));
            bool hasAnchor = internalDecisions.Any(value => value.Disposition == CandidatePairDisposition.Anchor);
            ContentSimilarityClassification[] comparisons = internalDecisions
                .Select(value => value.ContentComparison.Classification).ToArray();
            ContentComparisonSummary summary = new(
                comparisons.Length,
                comparisons.Count(value => value == ContentSimilarityClassification.EquivalentText),
                comparisons.Count(value => value == ContentSimilarityClassification.HighSimilarity),
                comparisons.Count(value => value == ContentSimilarityClassification.Ambiguous),
                comparisons.Count(value => value == ContentSimilarityClassification.Different),
                comparisons.Count(value => value == ContentSimilarityClassification.Unavailable));
            groups.Add(WorkLanguageCandidateGroup.Create(
                language,
                component.Members,
                component.Anchors.Count > 0 ? component.Anchors : [component.Members.MinBy(value => value.Value)],
                hasAnchor ? WorkLanguageCandidateConfidence.Strong : WorkLanguageCandidateConfidence.Probable,
                evidence,
                contentComparison: summary,
                policyVersion: version));
        }
        return groups.OrderBy(value => value.Language, StringComparer.Ordinal)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal).ToArray();
    }

    private static void Merge(
        BookCandidateDecision decision,
        bool isAnchor,
        Dictionary<CalibreBookId, Component> componentByMember,
        IReadOnlyDictionary<CalibreBookId, BookMatchingProfile> profiles,
        IReadOnlyDictionary<BookCandidatePairId, BookCandidateDecision> decisions)
    {
        Component first = componentByMember[decision.Pair.Id.First];
        Component second = componentByMember[decision.Pair.Id.Second];
        if (ReferenceEquals(first, second)) return;
        if (!isAnchor
            && (first.Members.Count > 1 && !first.Anchors.Contains(decision.Pair.Id.First)
                || second.Members.Count > 1 && !second.Anchors.Contains(decision.Pair.Id.Second)))
            return;
        if (!ComponentsCompatible(first, second, profiles, decisions)) return;
        HashSet<CalibreBookId> members = [.. first.Members, .. second.Members];
        HashSet<CalibreBookId> anchors = [.. first.Anchors, .. second.Anchors];
        if (isAnchor)
        {
            anchors.Add(decision.Pair.Id.First);
            anchors.Add(decision.Pair.Id.Second);
        }
        else if (anchors.Count == 0)
        {
            anchors.Add(members.MinBy(value => value.Value));
        }
        Component merged = new(members, anchors);
        foreach (CalibreBookId member in members) componentByMember[member] = merged;
    }

    private static bool ComponentsCompatible(
        Component first,
        Component second,
        IReadOnlyDictionary<CalibreBookId, BookMatchingProfile> profiles,
        IReadOnlyDictionary<BookCandidatePairId, BookCandidateDecision> decisions)
    {
        string[] firstLanguages = first.Members.Select(value => KnownLanguage(profiles[value]))
            .Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        string[] secondLanguages = second.Members.Select(value => KnownLanguage(profiles[value]))
            .Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        if (firstLanguages.Length > 1 || secondLanguages.Length > 1
            || firstLanguages.Length == 1 && secondLanguages.Length == 1
                && !string.Equals(firstLanguages[0], secondLanguages[0], StringComparison.Ordinal))
            return false;
        foreach (CalibreBookId left in first.Members)
            foreach (CalibreBookId right in second.Members)
            {
                BookMatchingProfile leftProfile = profiles[left];
                BookMatchingProfile rightProfile = profiles[right];
                if (!CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(
                        leftProfile.AuthorKeys, rightProfile.AuthorKeys))
                    return false;
                if (leftProfile.SeriesKey is not null && rightProfile.SeriesKey is not null
                    && string.Equals(leftProfile.SeriesKey, rightProfile.SeriesKey, StringComparison.Ordinal)
                    && leftProfile.SeriesIndex is not null && rightProfile.SeriesIndex is not null
                    && leftProfile.SeriesIndex != rightProfile.SeriesIndex)
                    return false;
                if (decisions.TryGetValue(new(left, right), out BookCandidateDecision? cross)
                    && cross.Disposition == CandidatePairDisposition.Rejected)
                    return false;
            }
        return true;
    }

    private static string ResolveLanguage(IEnumerable<BookMatchingProfile> profiles)
    {
        string[] known = profiles.Select(KnownLanguage).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        return known.Length == 1 ? known[0] : "und";
    }

    private static string? KnownLanguage(BookMatchingProfile profile) =>
        profile.Languages.Count == 1 ? profile.Languages[0] : null;

    private sealed record Component(HashSet<CalibreBookId> Members, HashSet<CalibreBookId> Anchors);
}
