using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public sealed record BookCandidateGenerationLimits
{
    public BookCandidateGenerationLimits(
        int maximumCandidatesPerRecord = 20,
        int maximumOrdinaryBucketSize = 256,
        int maximumUniquePairs = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCandidatesPerRecord, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCandidatesPerRecord, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOrdinaryBucketSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumOrdinaryBucketSize, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUniquePairs, 1);
        MaximumCandidatesPerRecord = maximumCandidatesPerRecord;
        MaximumOrdinaryBucketSize = maximumOrdinaryBucketSize;
        MaximumUniquePairs = maximumUniquePairs;
    }

    public int MaximumCandidatesPerRecord { get; }
    public int MaximumOrdinaryBucketSize { get; }
    public int MaximumUniquePairs { get; }
}

public readonly record struct BookCandidatePairId
{
    public BookCandidatePairId(CalibreBookId first, CalibreBookId second)
    {
        if (first == second) throw new ArgumentException("A candidate pair requires distinct books.");
        First = first.Value < second.Value ? first : second;
        Second = first.Value < second.Value ? second : first;
    }

    public CalibreBookId First { get; }
    public CalibreBookId Second { get; }
    public override string ToString() => $"{First.Value}:{Second.Value}";
}

public sealed record BookCandidatePair
{
    public BookCandidatePair(
        BookCandidatePairId id,
        int cheapScore,
        IEnumerable<CandidateEvidence> evidence,
        IEnumerable<CandidateContradiction>? contradictions,
        bool needsContentEvidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cheapScore);
        ArgumentNullException.ThrowIfNull(evidence);
        CandidateEvidence[] orderedEvidence = evidence.Distinct()
            .OrderByDescending(value => value.Strength)
            .ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
        CandidateContradiction[] orderedContradictions = (contradictions ?? []).Distinct()
            .OrderBy(value => value.Code, StringComparer.Ordinal).ToArray();
        if (orderedEvidence.Length == 0) throw new ArgumentException("A candidate pair requires evidence.");
        Id = id;
        CheapScore = cheapScore;
        Evidence = new ReadOnlyCollection<CandidateEvidence>(orderedEvidence);
        Contradictions = new ReadOnlyCollection<CandidateContradiction>(orderedContradictions);
        NeedsContentEvidence = needsContentEvidence;
    }

    public BookCandidatePairId Id { get; }
    public int CheapScore { get; }
    public IReadOnlyList<CandidateEvidence> Evidence { get; }
    public IReadOnlyList<CandidateContradiction> Contradictions { get; }
    public bool NeedsContentEvidence { get; }
    public bool HasAnchor => Evidence.Any(value => value.Strength == CandidateEvidenceStrength.Anchor);
}

public sealed record BookCandidateGenerationResult
{
    public BookCandidateGenerationResult(
        IEnumerable<BookCandidatePair> pairs,
        int proposedDirectedPairCount,
        int recordsCapped,
        int maximumObservedBucketSize,
        bool limitExceeded)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentOutOfRangeException.ThrowIfNegative(proposedDirectedPairCount);
        ArgumentOutOfRangeException.ThrowIfNegative(recordsCapped);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumObservedBucketSize);
        Pairs = new ReadOnlyCollection<BookCandidatePair>(pairs
            .OrderBy(value => value.Id.First.Value)
            .ThenBy(value => value.Id.Second.Value).ToArray());
        ProposedDirectedPairCount = proposedDirectedPairCount;
        RecordsCapped = recordsCapped;
        MaximumObservedBucketSize = maximumObservedBucketSize;
        LimitExceeded = limitExceeded;
    }

    public IReadOnlyList<BookCandidatePair> Pairs { get; }
    public int ProposedDirectedPairCount { get; }
    public int RecordsCapped { get; }
    public int MaximumObservedBucketSize { get; }
    public bool LimitExceeded { get; }
}

public static class BookCandidateGenerator
{
    private const int MinimumCandidateScore = 450;

    public static BookCandidateGenerationResult Generate(
        IEnumerable<BookMatchingProfile> profiles,
        BookCandidateGenerationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        BookCandidateGenerationLimits policy = limits ?? new();
        BookMatchingProfile[] ordered = profiles.OrderBy(value => value.BookId.Value).ToArray();
        if (ordered.Select(value => value.BookId).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Matching profiles must have unique record IDs.", nameof(profiles));
        Dictionary<CalibreBookId, BookMatchingProfile> byId = ordered.ToDictionary(value => value.BookId);
        Indexes indexes = BuildIndexes(ordered, cancellationToken);
        Dictionary<BookCandidatePairId, BookCandidatePair> retained = [];
        Dictionary<CalibreBookId, Dictionary<BookCandidatePairId, BookCandidatePair>> ordinarySelections = [];
        int effectiveMaximumUniquePairs = (int)Math.Min(
            policy.MaximumUniquePairs,
            Math.Min(int.MaxValue, (long)ordered.Length * 10));
        int proposedDirected = 0;
        int recordsCapped = 0;

        foreach (BookMatchingProfile source in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<CalibreBookId> candidateIds = [];
            AddCandidates(candidateIds, CandidateMetadataNormalizer.AuthorAliasKeys(source.AuthorKeys),
                indexes.AuthorKeys, source.BookId, policy.MaximumOrdinaryBucketSize);

            BookCandidatePair[] ranked = candidateIds
                .Select(candidateId => Score(source, byId[candidateId]))
                .Where(value => value is not null && value.CheapScore >= MinimumCandidateScore)
                .Select(value => value!)
                .OrderByDescending(value => value.HasAnchor)
                .ThenByDescending(value => value.CheapScore)
                .ThenBy(value => Other(value.Id, source.BookId).Value)
                .ToArray();
            proposedDirected = checked(proposedDirected + ranked.Length);
            BookCandidatePair[] anchors = ranked.Where(value => value.HasAnchor).ToArray();
            BookCandidatePair[] ordinary = ranked.Where(value => !value.HasAnchor).ToArray();
            if (ordinary.Length > policy.MaximumCandidatesPerRecord) recordsCapped++;
            foreach (BookCandidatePair pair in anchors)
            {
                if (!retained.TryGetValue(pair.Id, out BookCandidatePair? existing)
                    || pair.CheapScore > existing.CheapScore)
                    retained[pair.Id] = pair;
                if (retained.Count > effectiveMaximumUniquePairs)
                    return new([], proposedDirected, recordsCapped, indexes.MaximumObservedBucketSize, true);
            }
            ordinarySelections[source.BookId] = ordinary
                .Take(policy.MaximumCandidatesPerRecord)
                .ToDictionary(value => value.Id);
        }

        foreach ((CalibreBookId sourceId, Dictionary<BookCandidatePairId, BookCandidatePair> selections)
            in ordinarySelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (BookCandidatePair pair in selections.Values)
            {
                CalibreBookId otherId = Other(pair.Id, sourceId);
                if (!ordinarySelections[otherId].ContainsKey(pair.Id)) continue;
                retained.TryAdd(pair.Id, pair);
                if (retained.Count > effectiveMaximumUniquePairs)
                    return new([], proposedDirected, recordsCapped, indexes.MaximumObservedBucketSize, true);
            }
        }

        return new(retained.Values, proposedDirected, recordsCapped,
            indexes.MaximumObservedBucketSize, false);
    }

    private static Indexes BuildIndexes(
        IEnumerable<BookMatchingProfile> profiles,
        CancellationToken cancellationToken)
    {
        Indexes result = new();
        foreach (BookMatchingProfile profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddToIndex(result.AuthorKeys, CandidateMetadataNormalizer.AuthorAliasKeys(profile.AuthorKeys),
                profile.BookId, result);
        }
        foreach (Dictionary<string, List<CalibreBookId>> index in result.All)
            foreach (List<CalibreBookId> values in index.Values) values.Sort((left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private static BookCandidatePair? Score(BookMatchingProfile first, BookMatchingProfile second)
    {
        List<CandidateEvidence> evidence = [];
        List<CandidateContradiction> contradictions = [];
        int score = 0;
        bool compatibleAuthor = CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(
            first.AuthorKeys, second.AuthorKeys);
        if (!compatibleAuthor) return null;
        bool exactBinary = Overlaps(first.ExactBinaryKeys, second.ExactBinaryKeys);
        bool strongIdentifier = Overlaps(first.StrongIdentifiers, second.StrongIdentifiers);
        bool embeddedIdentifier = Overlaps(first.EmbeddedIdentifiers, second.EmbeddedIdentifiers);
        bool exactTitle = Overlaps(first.TitleKeys, second.TitleKeys);
        bool exactAuthor = CandidateMetadataNormalizer.HaveExactAuthorIdentity(
            first.AuthorKeys, second.AuthorKeys);
        int titleSimilarity = JaccardPermille(first.TitleTokens, second.TitleTokens);
        int authorSimilarity = JaccardPermille(first.AuthorTokens, second.AuthorTokens);
        bool sameSeries = first.SeriesKey is not null && second.SeriesKey is not null
            && string.Equals(first.SeriesKey, second.SeriesKey, StringComparison.Ordinal)
            && SeriesIndicesCompatible(first.SeriesIndex, second.SeriesIndex);
        bool knownLanguageConflict = first.Languages.Count > 0 && second.Languages.Count > 0
            && !Overlaps(first.Languages, second.Languages);
        bool seriesIndexConflict = SeriesIndicesConflict(first, second);

        if (exactBinary) Add(evidence, "MATCH.BINARY.EXACT", CandidateEvidenceStrength.Anchor, 2000, ref score);
        if (strongIdentifier) Add(evidence, "MATCH.IDENTIFIER.EXACT", CandidateEvidenceStrength.Anchor, 1600, ref score);
        if (embeddedIdentifier) Add(evidence, "MATCH.EMBEDDED_ID.EXACT", CandidateEvidenceStrength.Strong, 800, ref score);
        if (exactTitle) Add(evidence, "MATCH.TITLE.EXACT_VARIANT", CandidateEvidenceStrength.Strong, 500, ref score);
        if (exactAuthor) Add(evidence, "MATCH.AUTHOR.EXACT_VARIANT", CandidateEvidenceStrength.Strong, 500, ref score);
        else Add(evidence, "MATCH.AUTHOR.COMPATIBLE_ALIAS", CandidateEvidenceStrength.Supporting, 350, ref score);
        if (titleSimilarity >= 200)
            Add(evidence, TitleBand(titleSimilarity), CandidateEvidenceStrength.Supporting,
                titleSimilarity * 6 / 10, ref score);
        if (authorSimilarity >= 250)
            Add(evidence, AuthorBand(authorSimilarity), CandidateEvidenceStrength.Supporting,
                authorSimilarity * 4 / 10, ref score);
        if (sameSeries) Add(evidence, "MATCH.SERIES_INDEX.COMPATIBLE", CandidateEvidenceStrength.Supporting, 300, ref score);
        if (Overlaps(first.Languages, second.Languages))
            Add(evidence, "MATCH.LANGUAGE.COMPATIBLE", CandidateEvidenceStrength.Supporting, 150, ref score);
        if (knownLanguageConflict) contradictions.Add(new("MATCH.LANGUAGE.CONFLICT"));
        if (seriesIndexConflict) contradictions.Add(new("MATCH.SERIES_INDEX.CONFLICT"));
        if (evidence.Count == 0) return null;

        bool hasWorkLevelEvidence = exactBinary
            || strongIdentifier
            || embeddedIdentifier
            || exactTitle
            || titleSimilarity >= 500
            || sameSeries;
        if (!hasWorkLevelEvidence) return null;
        bool needsContent = !exactBinary
            && !knownLanguageConflict
            && !seriesIndexConflict;
        return new(new(first.BookId, second.BookId), score, evidence, contradictions, needsContent);
    }

    private static void Add(
        List<CandidateEvidence> target,
        string code,
        CandidateEvidenceStrength strength,
        int points,
        ref int score)
    {
        target.Add(new(code, strength));
        score = checked(score + points);
    }

    private static int JaccardPermille(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count == 0 || second.Count == 0) return 0;
        HashSet<string> left = first.ToHashSet(StringComparer.Ordinal);
        HashSet<string> right = second.ToHashSet(StringComparer.Ordinal);
        int intersection = left.Count(right.Contains);
        int union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : intersection * 1000 / union;
    }

    private static string TitleBand(int value) => value switch
    {
        >= 800 => "MATCH.TITLE.TOKEN_HIGH",
        >= 500 => "MATCH.TITLE.TOKEN_MEDIUM",
        _ => "MATCH.TITLE.TOKEN_LOW",
    };

    private static string AuthorBand(int value) => value switch
    {
        >= 800 => "MATCH.AUTHOR.TOKEN_HIGH",
        >= 500 => "MATCH.AUTHOR.TOKEN_MEDIUM",
        _ => "MATCH.AUTHOR.TOKEN_LOW",
    };

    private static bool SeriesIndicesCompatible(decimal? first, decimal? second) =>
        first is null || second is null || first == second;

    private static bool SeriesIndicesConflict(BookMatchingProfile first, BookMatchingProfile second) =>
        first.SeriesKey is not null && second.SeriesKey is not null
        && string.Equals(first.SeriesKey, second.SeriesKey, StringComparison.Ordinal)
        && first.SeriesIndex is not null && second.SeriesIndex is not null
        && first.SeriesIndex != second.SeriesIndex;

    private static bool Overlaps(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.Count > 0 && second.Count > 0 && first.Intersect(second, StringComparer.Ordinal).Any();

    private static CalibreBookId Other(BookCandidatePairId pair, CalibreBookId source) =>
        pair.First == source ? pair.Second : pair.First;

    private static void AddCandidates(
        HashSet<CalibreBookId> target,
        IEnumerable<string> keys,
        Dictionary<string, List<CalibreBookId>> index,
        CalibreBookId source,
        int maximumBucketSize)
    {
        foreach (string key in keys)
            if (index.TryGetValue(key, out List<CalibreBookId>? values) && values.Count <= maximumBucketSize)
                target.UnionWith(values.Where(value => value != source));
    }

    private static void AddToIndex(
        Dictionary<string, List<CalibreBookId>> index,
        IEnumerable<string> keys,
        CalibreBookId bookId,
        Indexes indexes)
    {
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!index.TryGetValue(key, out List<CalibreBookId>? values))
            {
                values = [];
                index.Add(key, values);
            }
            values.Add(bookId);
            indexes.MaximumObservedBucketSize = Math.Max(indexes.MaximumObservedBucketSize, values.Count);
        }
    }

    private sealed class Indexes
    {
        public Dictionary<string, List<CalibreBookId>> AuthorKeys { get; } = new(StringComparer.Ordinal);
        public int MaximumObservedBucketSize { get; set; }
        public IEnumerable<Dictionary<string, List<CalibreBookId>>> All =>
            [AuthorKeys];
    }
}
