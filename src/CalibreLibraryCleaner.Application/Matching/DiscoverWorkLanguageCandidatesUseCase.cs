using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Matching;

public enum WorkLanguageDiscoveryPhase
{
    BuildingProfiles,
    GeneratingCandidates,
    InspectingContent,
    Clustering,
}

public sealed record WorkLanguageDiscoveryProgress(
    WorkLanguageDiscoveryPhase Phase,
    int Completed,
    int Total,
    string Detail = "");

public sealed record WorkLanguageDiscoveryResult(
    IReadOnlyList<WorkLanguageCandidateGroup> Groups,
    BookMatchingRunSummary Summary,
    bool LimitExceeded);

public interface IWorkLanguageCandidateDiscoverer
{
    Task<WorkLanguageDiscoveryResult> ExecuteAsync(
        IReadOnlyList<CalibreBook> books,
        IReadOnlyList<EpubAssessment> epubAssessments,
        IReadOnlyList<EpubAssessmentTarget> epubTargets,
        int maximumContentConcurrency,
        IProgress<WorkLanguageDiscoveryProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class DiscoverWorkLanguageCandidatesUseCase(
    ResolveCandidateContentSignaturesUseCase resolveContentSignatures) : IWorkLanguageCandidateDiscoverer
{
    public async Task<WorkLanguageDiscoveryResult> ExecuteAsync(
        IReadOnlyList<CalibreBook> books,
        IReadOnlyList<EpubAssessment> epubAssessments,
        IReadOnlyList<EpubAssessmentTarget> epubTargets,
        int maximumContentConcurrency,
        IProgress<WorkLanguageDiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(epubAssessments);
        ArgumentNullException.ThrowIfNull(epubTargets);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(WorkLanguageDiscoveryPhase.BuildingProfiles, 0, books.Count));
        IReadOnlyList<BookMatchingProfile> profiles = BookMatchingProfileFactory.Create(
            books, epubAssessments, cancellationToken);
        progress?.Report(new(WorkLanguageDiscoveryPhase.BuildingProfiles, books.Count, books.Count));

        progress?.Report(new(WorkLanguageDiscoveryPhase.GeneratingCandidates, 0, profiles.Count));
        BookCandidateGenerationResult candidates = BookCandidateGenerator.Generate(
            profiles, cancellationToken: cancellationToken);
        progress?.Report(new(WorkLanguageDiscoveryPhase.GeneratingCandidates, profiles.Count, profiles.Count));
        int unknownLanguageCount = books.Count - profiles.Count
            + profiles.Count(value => value.Languages.Count != 1);
        if (candidates.LimitExceeded)
        {
            return new(
                [],
                new(
                    MatchingPolicyVersion.Current,
                    MatchingEvidenceStatus.Unavailable,
                    books.Count,
                    candidates.ProposedDirectedPairCount,
                    0,
                    candidates.RecordsCapped,
                    0,
                    0,
                    0,
                    0,
                    unknownLanguageCount),
                true);
        }

        progress?.Report(new(
            WorkLanguageDiscoveryPhase.InspectingContent,
            0,
            0,
            "Planning unique candidate EPUB fingerprints."));
        IProgress<CandidateContentSignatureProgress>? contentProgress = progress is null
            ? null
            : new ContentProgressAdapter(progress);
        CandidateContentSignatureBatchResult content = await resolveContentSignatures.ExecuteAsync(
            candidates.Pairs,
            epubTargets,
            maximumContentConcurrency,
            EpubInspectionLimits.V1,
            EpubContentSignatureLimits.V1,
            contentProgress,
            cancellationToken).ConfigureAwait(false);
        Dictionary<BookCandidatePairId, CandidateContentComparison> comparisons = [];
        foreach (BookCandidatePair pair in candidates.Pairs.Where(value => value.NeedsContentEvidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            comparisons[pair.Id] = content.Signatures.TryGetValue(pair.Id.First, out EpubContentSignature? first)
                && content.Signatures.TryGetValue(pair.Id.Second, out EpubContentSignature? second)
                ? EpubContentSignatureComparer.Compare(first, second)
                : EpubContentSignatureComparer.Unavailable();
        }

        progress?.Report(new(WorkLanguageDiscoveryPhase.Clustering, 0, candidates.Pairs.Count));
        IReadOnlyList<BookCandidateDecision> decisions = BookCandidateDecisionPolicy.Decide(
            candidates.Pairs, comparisons, cancellationToken);
        IReadOnlyList<WorkLanguageCandidateGroup> groups = WorkLanguageCandidateClusterer.Cluster(
            profiles, decisions, cancellationToken: cancellationToken);
        progress?.Report(new(WorkLanguageDiscoveryPhase.Clustering, candidates.Pairs.Count, candidates.Pairs.Count));
        BookMatchingRunSummary summary = new(
            MatchingPolicyVersion.Current,
            MatchingEvidenceStatus.Available,
            books.Count,
            candidates.ProposedDirectedPairCount,
            candidates.Pairs.Count,
            candidates.RecordsCapped,
            content.RequestedFingerprintCount,
            content.CacheHits,
            comparisons.Count,
            groups.Count,
            unknownLanguageCount);
        return new(groups, summary, false);
    }

    private sealed class ContentProgressAdapter(IProgress<WorkLanguageDiscoveryProgress> progress) :
        IProgress<CandidateContentSignatureProgress>
    {
        public void Report(CandidateContentSignatureProgress value) => progress.Report(new(
            WorkLanguageDiscoveryPhase.InspectingContent,
            value.CompletedFingerprints,
            value.TotalFingerprints,
            value.Detail));
    }
}
