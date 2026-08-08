namespace CalibreLibraryCleaner.Domain.Matching;

public enum ContentSimilarityClassification
{
    EquivalentText,
    HighSimilarity,
    Ambiguous,
    Different,
    Unavailable,
}

public sealed record CandidateContentComparison
{
    public CandidateContentComparison(
        ContentSimilarityClassification classification,
        int forwardStrictMatches,
        int reverseStrictMatches,
        int forwardRelaxedMatches,
        int reverseRelaxedMatches,
        int matchedRegionCount,
        int tokenCountRatioPermille,
        int shingleSimilarityPermille = 0)
    {
        int[] counts =
        [
            forwardStrictMatches,
            reverseStrictMatches,
            forwardRelaxedMatches,
            reverseRelaxedMatches,
            matchedRegionCount,
            tokenCountRatioPermille,
            shingleSimilarityPermille,
        ];
        if (!Enum.IsDefined(classification)
            || counts.Any(value => value < 0)
            || forwardStrictMatches > 12
            || reverseStrictMatches > 12
            || forwardRelaxedMatches > 12
            || reverseRelaxedMatches > 12
            || matchedRegionCount > 4
            || tokenCountRatioPermille > 1000
            || shingleSimilarityPermille > 1000)
            throw new ArgumentException("Content comparison evidence is invalid.");
        Classification = classification;
        ForwardStrictMatches = forwardStrictMatches;
        ReverseStrictMatches = reverseStrictMatches;
        ForwardRelaxedMatches = forwardRelaxedMatches;
        ReverseRelaxedMatches = reverseRelaxedMatches;
        MatchedRegionCount = matchedRegionCount;
        TokenCountRatioPermille = tokenCountRatioPermille;
        ShingleSimilarityPermille = shingleSimilarityPermille;
    }

    public ContentSimilarityClassification Classification { get; }
    public int ForwardStrictMatches { get; }
    public int ReverseStrictMatches { get; }
    public int ForwardRelaxedMatches { get; }
    public int ReverseRelaxedMatches { get; }
    public int MatchedRegionCount { get; }
    public int TokenCountRatioPermille { get; }
    public int ShingleSimilarityPermille { get; }
}

public static class EpubContentSignatureComparer
{
    public static CandidateContentComparison Compare(
        EpubContentSignature first,
        EpubContentSignature second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.PolicyVersion != second.PolicyVersion)
            return Unavailable();
        int ratio = checked((int)(Math.Min(first.TotalTokenCount, second.TotalTokenCount) * 1000
            / Math.Max(first.TotalTokenCount, second.TotalTokenCount)));
        bool languageConflict = first.DetectedLanguage is not null && second.DetectedLanguage is not null
            && !string.Equals(first.DetectedLanguage, second.DetectedLanguage, StringComparison.Ordinal);
        MatchCounts forward = Count(first, second);
        MatchCounts reverse = Count(second, first);
        int regionCount = forward.Regions.Union(reverse.Regions).Count();
        int shingleSimilarity = SetSimilarityPermille(first.ShingleMinHashes, second.ShingleMinHashes);
        ContentSimilarityClassification classification;
        if (languageConflict || ratio < 650)
            classification = ContentSimilarityClassification.Different;
        else if (ratio is >= 800 and <= 1000 && regionCount >= 4
            && forward.Strict >= 8 && reverse.Strict >= 8)
            classification = ContentSimilarityClassification.EquivalentText;
        else if (ratio is >= 800 and <= 1000 && regionCount >= 4
            && forward.Relaxed >= 8 && reverse.Relaxed >= 8)
            classification = ContentSimilarityClassification.HighSimilarity;
        else if (ratio is >= 800 and <= 1000 && shingleSimilarity >= 700)
            classification = ContentSimilarityClassification.HighSimilarity;
        else if (forward.Relaxed <= 2 && reverse.Relaxed <= 2)
            classification = ContentSimilarityClassification.Different;
        else
            classification = ContentSimilarityClassification.Ambiguous;
        return new(classification, forward.Strict, reverse.Strict,
            forward.Relaxed, reverse.Relaxed, regionCount, ratio, shingleSimilarity);
    }

    public static CandidateContentComparison Unavailable() => new(
        ContentSimilarityClassification.Unavailable, 0, 0, 0, 0, 0, 0);

    private static int SetSimilarityPermille(IReadOnlyList<ulong> first, IReadOnlyList<ulong> second)
    {
        if (first.Count == 0 || second.Count == 0) return 0;
        HashSet<ulong> left = first.ToHashSet();
        HashSet<ulong> right = second.ToHashSet();
        int intersection = left.Count(right.Contains);
        int union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : intersection * 1000 / union;
    }

    private static MatchCounts Count(EpubContentSignature source, EpubContentSignature target)
    {
        HashSet<Domain.Libraries.Sha256Digest> strict = target.Landmarks
            .Select(value => value.StrictHash).ToHashSet();
        HashSet<Domain.Libraries.Sha256Digest> relaxed = target.Landmarks
            .Select(value => value.RelaxedHash).ToHashSet();
        int strictMatches = 0;
        int relaxedMatches = 0;
        HashSet<int> regions = [];
        foreach (ContentLandmarkSignature landmark in source.Landmarks)
        {
            bool strictMatch = strict.Contains(landmark.StrictHash);
            bool relaxedMatch = relaxed.Contains(landmark.RelaxedHash);
            if (strictMatch) strictMatches++;
            if (relaxedMatch) relaxedMatches++;
            if (strictMatch || relaxedMatch) regions.Add(Math.Min(3, landmark.Ordinal / 3));
        }
        return new(strictMatches, relaxedMatches, regions);
    }

    private sealed record MatchCounts(int Strict, int Relaxed, HashSet<int> Regions);
}
