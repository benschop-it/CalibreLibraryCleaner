using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum FusedEditionMetadataConfidence
{
    Unavailable,
    Low,
    Medium,
    High,
}

public enum EditionMetadataField
{
    Title,
    Authors,
    Identifiers,
    Publisher,
    PublicationDate,
    Languages,
    Series,
    SeriesIndex,
    Cover,
}

public sealed record EditionMetadataFieldSource
{
    public EditionMetadataFieldSource(
        EditionMetadataField field,
        EditionMetadataProviderIdentity provider,
        string editionId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(editionId);
        if (!Enum.IsDefined(field) || editionId.Length > 160)
            throw new ArgumentException("Edition metadata field source is invalid.");
        Field = field;
        Provider = provider;
        EditionId = editionId.Trim();
    }

    public EditionMetadataField Field { get; }
    public EditionMetadataProviderIdentity Provider { get; }
    public string EditionId { get; }
}

public sealed record FusedEditionMetadataProposal
{
    public const string CurrentPolicyVersion = "edition-metadata-fusion/1.0.0";

    public FusedEditionMetadataProposal(
        FusedEditionMetadataConfidence confidence,
        EditionMetadataProviderIdentity? primaryProvider,
        EditionMetadataCandidate? candidate,
        IEnumerable<EditionMetadataProposal> providerProposals,
        IEnumerable<EditionMetadataFieldSource>? fieldSources,
        IEnumerable<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(providerProposals);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        EditionMetadataProposal[] proposals = providerProposals
            .OrderBy(value => value.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Provider.Version, StringComparer.Ordinal)
            .ThenBy(value => value.BookId.Value)
            .ThenBy(value => value.RetrievedAtUtc)
            .ThenBy(value => value.Candidate?.EditionId ?? string.Empty, StringComparer.Ordinal)
            .Take(1_025).ToArray();
        EditionMetadataFieldSource[] sources = (fieldSources ?? []).Distinct()
            .OrderBy(value => value.Field)
            .ThenBy(value => value.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Provider.Version, StringComparer.Ordinal)
            .ThenBy(value => value.EditionId, StringComparer.Ordinal).ToArray();
        string[] reasons = reasonCodes.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(33).ToArray();
        bool available = confidence != FusedEditionMetadataConfidence.Unavailable;
        if (!Enum.IsDefined(confidence) || proposals.Length > 1_024 || reasons.Length is 0 or > 32
            || reasons.Any(value => value.Length > 128)
            || available != (primaryProvider is not null && candidate is not null)
            || !available && sources.Length > 0
            || available && !proposals.Any(value => value.Provider == primaryProvider
                && value.Candidate?.EditionId == candidate!.EditionId)
            || sources.Any(value => !proposals.Any(proposal => proposal.Provider == value.Provider
                && proposal.Candidate?.EditionId == value.EditionId)))
            throw new ArgumentException("Fused edition metadata proposal is invalid.");
        Confidence = confidence;
        PolicyVersion = CurrentPolicyVersion;
        PrimaryProvider = primaryProvider;
        Candidate = candidate;
        ProviderProposals = new ReadOnlyCollection<EditionMetadataProposal>(proposals);
        FieldSources = new ReadOnlyCollection<EditionMetadataFieldSource>(sources);
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
    }

    public FusedEditionMetadataConfidence Confidence { get; }
    public string PolicyVersion { get; }
    public bool IsSelectedByDefault => Confidence is FusedEditionMetadataConfidence.High
        or FusedEditionMetadataConfidence.Medium;
    public EditionMetadataProviderIdentity? PrimaryProvider { get; }
    public EditionMetadataCandidate? Candidate { get; }
    public IReadOnlyList<EditionMetadataProposal> ProviderProposals { get; }
    public IReadOnlyList<EditionMetadataFieldSource> FieldSources { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
}

public readonly record struct MetadataReviewSubjectId
{
    public MetadataReviewSubjectId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record MetadataReviewSubject
{
    public MetadataReviewSubject(
        MetadataReviewSubjectId id,
        UnifiedCandidateGroupId? unifiedGroupId,
        IEnumerable<CalibreBookId> members,
        CalibreBookId targetBookId,
        FusedEditionMetadataProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(proposal);
        CalibreBookId[] orderedMembers = members.Distinct().OrderBy(value => value.Value).ToArray();
        if (orderedMembers.Length == 0 || !orderedMembers.Contains(targetBookId)
            || unifiedGroupId is null && orderedMembers.Length != 1
            || unifiedGroupId is not null && orderedMembers.Length < 2)
            throw new ArgumentException("Metadata review subject is invalid.");
        Id = id;
        UnifiedGroupId = unifiedGroupId;
        Members = new ReadOnlyCollection<CalibreBookId>(orderedMembers);
        TargetBookId = targetBookId;
        Proposal = proposal;
    }

    public MetadataReviewSubjectId Id { get; }
    public UnifiedCandidateGroupId? UnifiedGroupId { get; }
    public IReadOnlyList<CalibreBookId> Members { get; }
    public CalibreBookId TargetBookId { get; }
    public FusedEditionMetadataProposal Proposal { get; }

    public MetadataReviewSubject Retarget(CalibreBookId targetBookId) =>
        new(Id, UnifiedGroupId, Members, targetBookId, Proposal);
}

public static class EditionMetadataFusionPolicy
{
    public static FusedEditionMetadataProposal Fuse(IEnumerable<EditionMetadataProposal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        EditionMetadataProposal[] proposals = values.OrderBy(value => value.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Provider.Version, StringComparer.Ordinal)
            .ThenBy(value => value.BookId.Value)
            .ThenBy(value => value.Candidate?.EditionId ?? string.Empty, StringComparer.Ordinal).ToArray();
        ProviderSelection[] allSelections = proposals
            .Where(value => value.Status == EditionMetadataProposalStatus.Proposed)
            .Select(value => new ProviderSelection(value, value.Candidate!))
            .ToArray();
        ProviderSelection[] selections = allSelections
            .GroupBy(value => value.Proposal.Provider)
            .Select(group => group.OrderByDescending(value => value.Proposal.MatchScore)
                .ThenByDescending(value => value.Candidate.Completeness)
                .ThenBy(value => value.Candidate.EditionId, StringComparer.Ordinal)
                .ThenBy(value => value.Proposal.BookId.Value).First())
            .ToArray();
        if (selections.Length == 0)
            return new(
                FusedEditionMetadataConfidence.Unavailable,
                null,
                null,
                proposals,
                [],
                ["METADATA.FUSION.CONFIDENCE_UNAVAILABLE"]);

        bool withinProviderDisagreement = allSelections
            .GroupBy(value => value.Proposal.Provider)
            .Any(group => group.DistinctBy(value => value.Candidate.EditionId)
                .SelectMany((left, index) => group.DistinctBy(value => value.Candidate.EditionId).Skip(index + 1)
                    .Select(right => (left, right)))
                .Any(pair => !SameEdition(pair.left, pair.right)));
        ProviderSelection primary = selections
            .OrderByDescending(value => selections.Count(other =>
                other != value && SameEdition(value, other)))
            .ThenByDescending(value => selections.Count(other => other != value
                && CoreCompatible(value.Candidate, other.Candidate)
                && !EditionFactsConflict(value.Candidate, other.Candidate)))
            .ThenByDescending(value => value.Proposal.MatchScore)
            .ThenByDescending(value => value.Candidate.Completeness)
            .ThenBy(value => value.Proposal.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Proposal.Provider.Version, StringComparer.Ordinal)
            .ThenBy(value => value.Candidate.EditionId, StringComparer.Ordinal).First();
        ProviderSelection[] orderedSecondaries = selections.Where(value => value != primary)
            .OrderByDescending(value => value.Proposal.MatchScore)
            .ThenByDescending(value => value.Candidate.Completeness)
            .ThenBy(value => value.Proposal.Provider.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Candidate.EditionId, StringComparer.Ordinal).ToArray();
        bool sharedIsbnAgreement = orderedSecondaries.Any(value =>
            SharedIsbns(primary.Candidate, value.Candidate).Count > 0);
        bool providerDisagreement = withinProviderDisagreement || selections
            .SelectMany((left, index) => selections.Skip(index + 1).Select(right => (left, right)))
            .Any(pair => !CoreCompatible(pair.left.Candidate, pair.right.Candidate)
                || EditionFactsConflict(pair.left.Candidate, pair.right.Candidate));
        bool exactIsbn = primary.Proposal.ReasonCodes.Contains(
            "METADATA.EDITION.ISBN_EXACT", StringComparer.Ordinal);
        bool exactTitleAuthor = primary.Proposal.ReasonCodes.Contains(
                "METADATA.EDITION.TITLE_EXACT", StringComparer.Ordinal)
            && primary.Proposal.ReasonCodes.Contains(
                "METADATA.EDITION.AUTHOR_EXACT", StringComparer.Ordinal);
        FusedEditionMetadataConfidence confidence = sharedIsbnAgreement && !providerDisagreement
            ? FusedEditionMetadataConfidence.High
            : !providerDisagreement && (exactIsbn || exactTitleAuthor)
                ? FusedEditionMetadataConfidence.Medium
                : FusedEditionMetadataConfidence.Low;

        List<string> reasons = [];
        reasons.Add(confidence switch
        {
            FusedEditionMetadataConfidence.High => "METADATA.FUSION.CONFIDENCE_HIGH",
            FusedEditionMetadataConfidence.Medium => "METADATA.FUSION.CONFIDENCE_MEDIUM",
            _ => "METADATA.FUSION.CONFIDENCE_LOW",
        });
        reasons.Add(selections.Length == 1
            ? "METADATA.FUSION.SINGLE_PROVIDER"
            : "METADATA.FUSION.MULTIPLE_PROVIDERS");
        if (sharedIsbnAgreement) reasons.Add("METADATA.FUSION.SHARED_ISBN");
        if (providerDisagreement) reasons.Add("METADATA.FUSION.PROVIDER_DISAGREEMENT");
        if (!providerDisagreement && selections.Length > 1)
            reasons.Add("METADATA.FUSION.PROVIDER_AGREEMENT");
        if (exactIsbn) reasons.Add("METADATA.FUSION.PRIMARY_EXACT_ISBN");
        if (exactTitleAuthor) reasons.Add("METADATA.FUSION.PRIMARY_EXACT_TITLE_AUTHOR");

        (EditionMetadataCandidate Candidate, EditionMetadataFieldSource[] Sources, bool Filled) merged =
            MergeSameEditionFields(
                primary,
                allSelections.Where(value => value.Proposal != primary.Proposal)
                    .OrderByDescending(value => value.Proposal.MatchScore)
                    .ThenByDescending(value => value.Candidate.Completeness)
                    .ThenBy(value => value.Proposal.Provider.Id, StringComparer.Ordinal)
                    .ThenBy(value => value.Candidate.EditionId, StringComparer.Ordinal).ToArray());
        if (merged.Filled) reasons.Add("METADATA.FUSION.MISSING_FIELDS_FILLED");
        return new(
            confidence,
            primary.Proposal.Provider,
            merged.Candidate,
            proposals,
            merged.Sources,
            reasons);
    }

    private static (EditionMetadataCandidate Candidate, EditionMetadataFieldSource[] Sources, bool Filled)
        MergeSameEditionFields(ProviderSelection primary, IReadOnlyList<ProviderSelection> secondaries)
    {
        EditionMetadataCandidate source = primary.Candidate;
        ProviderSelection[] sameEdition = secondaries.Where(value => SameEdition(primary, value)).ToArray();
        List<EditionMetadataFieldSource> fieldSources =
        [
            Source(EditionMetadataField.Title, primary),
            Source(EditionMetadataField.Authors, primary),
        ];
        List<EditionMetadataIdentifier> identifiers = source.Identifiers.ToList();
        if (identifiers.Count > 0) fieldSources.Add(Source(EditionMetadataField.Identifiers, primary));
        foreach (ProviderSelection secondary in sameEdition)
        {
            EditionMetadataIdentifier[] additions = secondary.Candidate.Identifiers
                .Where(value => identifiers.All(existing => existing.CanonicalValue != value.CanonicalValue))
                .ToArray();
            if (additions.Length == 0) continue;
            identifiers.AddRange(additions);
            fieldSources.Add(Source(EditionMetadataField.Identifiers, secondary));
        }

        bool filled = false;
        (string? Publisher, ProviderSelection? Source) publisher = Fill(
            source.Publisher, primary, sameEdition, value => value.Candidate.Publisher);
        (EditionPublicationDate? Date, ProviderSelection? Source) publicationDate = Fill(
            source.PublicationDate, primary, sameEdition, value => value.Candidate.PublicationDate);
        (IReadOnlyList<string> Languages, ProviderSelection? Source) languages = source.Languages.Count > 0
            ? (source.Languages, primary)
            : sameEdition.FirstOrDefault(value => value.Candidate.Languages.Count > 0) is { } languageSource
                ? (languageSource.Candidate.Languages, languageSource)
                : (source.Languages, null);
        (string? Series, ProviderSelection? Source) series = Fill(
            source.Series, primary, sameEdition, value => value.Candidate.Series);
        (decimal? SeriesIndex, ProviderSelection? Source) seriesIndex = Fill(
            source.SeriesIndex, primary, sameEdition, value => value.Candidate.SeriesIndex);
        (EditionCoverReference? Cover, ProviderSelection? Source) cover = Fill(
            source.Cover, primary, sameEdition, value => value.Candidate.Cover);
        AddSource(EditionMetadataField.Publisher, source.Publisher, publisher.Publisher, publisher.Source);
        AddSource(EditionMetadataField.PublicationDate, source.PublicationDate, publicationDate.Date, publicationDate.Source);
        AddSource(EditionMetadataField.Languages, source.Languages.Count > 0 ? source.Languages : null,
            languages.Languages.Count > 0 ? languages.Languages : null, languages.Source);
        AddSource(EditionMetadataField.Series, source.Series, series.Series, series.Source);
        AddSource(EditionMetadataField.SeriesIndex, source.SeriesIndex, seriesIndex.SeriesIndex, seriesIndex.Source);
        AddSource(EditionMetadataField.Cover, source.Cover, cover.Cover, cover.Source);
        return (new(
            source.WorkId,
            source.EditionId,
            source.Title,
            source.Authors,
            identifiers,
            publisher.Publisher,
            publicationDate.Date,
            languages.Languages,
            series.Series,
            seriesIndex.SeriesIndex,
            cover.Cover), fieldSources.Distinct().ToArray(), filled);

        void AddSource<T>(
            EditionMetadataField field,
            T? original,
            T? result,
            ProviderSelection? selectedSource)
        {
            if (result is null || selectedSource is null) return;
            fieldSources.Add(Source(field, selectedSource));
            if (original is null && selectedSource != primary) filled = true;
        }
    }

    private static (T? Value, ProviderSelection? Source) Fill<T>(
        T? current,
        ProviderSelection primary,
        IEnumerable<ProviderSelection> secondaries,
        Func<ProviderSelection, T?> selector)
    {
        if (current is not null) return (current, primary);
        ProviderSelection? source = secondaries.FirstOrDefault(value => selector(value) is not null);
        return source is null ? (default, null) : (selector(source), source);
    }

    private static EditionMetadataFieldSource Source(
        EditionMetadataField field,
        ProviderSelection value) => new(
        field,
        value.Proposal.Provider,
        value.Candidate.EditionId);

    private static bool SameEdition(ProviderSelection first, ProviderSelection second) =>
        first.Proposal.Provider == second.Proposal.Provider
            && first.Candidate.EditionId == second.Candidate.EditionId
        || SharedIsbns(first.Candidate, second.Candidate).Count > 0;

    private static HashSet<string> SharedIsbns(
        EditionMetadataCandidate first,
        EditionMetadataCandidate second)
    {
        HashSet<string> left = first.Identifiers.Where(value => value.Type == "isbn")
            .Select(value => value.CanonicalValue).ToHashSet(StringComparer.Ordinal);
        left.IntersectWith(second.Identifiers.Where(value => value.Type == "isbn")
            .Select(value => value.CanonicalValue));
        return left;
    }

    private static bool CoreCompatible(EditionMetadataCandidate first, EditionMetadataCandidate second)
    {
        bool titleCompatible = CandidateMetadataNormalizer.TitleKeys(first.Title).Intersect(
                CandidateMetadataNormalizer.TitleKeys(second.Title), StringComparer.Ordinal).Any()
            || CandidateMetadataNormalizer.TitleSimilarityPermille(
                CandidateMetadataNormalizer.TitleTokens(first.Title),
                CandidateMetadataNormalizer.TitleTokens(second.Title)) >= 500;
        bool authorCompatible = CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(
            CandidateMetadataNormalizer.AuthorKeys(first.Authors),
            CandidateMetadataNormalizer.AuthorKeys(second.Authors));
        bool languageCompatible = first.Languages.Count == 0 || second.Languages.Count == 0
            || first.Languages.Intersect(second.Languages, StringComparer.Ordinal).Any();
        return titleCompatible && authorCompatible && languageCompatible;
    }

    private static bool EditionFactsConflict(EditionMetadataCandidate first, EditionMetadataCandidate second)
    {
        string[] firstIsbns = first.Identifiers.Where(value => value.Type == "isbn")
            .Select(value => value.CanonicalValue).ToArray();
        string[] secondIsbns = second.Identifiers.Where(value => value.Type == "isbn")
            .Select(value => value.CanonicalValue).ToArray();
        if (firstIsbns.Length > 0 && secondIsbns.Length > 0
            && !firstIsbns.Intersect(secondIsbns, StringComparer.Ordinal).Any())
            return true;
        if (first.Publisher is not null && second.Publisher is not null
            && Normalize(first.Publisher) != Normalize(second.Publisher))
            return true;
        if (DatesConflict(first.PublicationDate, second.PublicationDate)) return true;
        if (first.Series is not null && second.Series is not null
            && CandidateMetadataNormalizer.NormalizeSeries(first.Series)
                != CandidateMetadataNormalizer.NormalizeSeries(second.Series))
            return true;
        return first.SeriesIndex is not null && second.SeriesIndex is not null
            && first.SeriesIndex != second.SeriesIndex;
    }

    private static bool DatesConflict(EditionPublicationDate? first, EditionPublicationDate? second)
    {
        if (first is null || second is null) return false;
        if (first.Year != second.Year) return true;
        if (first.Month is not null && second.Month is not null && first.Month != second.Month) return true;
        return first.Day is not null && second.Day is not null && first.Day != second.Day;
    }

    private static string Normalize(string value) => string.Join(' ',
        CandidateMetadataNormalizer.TitleTokens(value));

    private sealed record ProviderSelection(
        EditionMetadataProposal Proposal,
        EditionMetadataCandidate Candidate);
}

public static class MetadataReviewSubjectPolicy
{
    public static IReadOnlyList<MetadataReviewSubject> Create(
        IReadOnlyList<CalibreBook> books,
        IReadOnlyList<UnifiedCandidateGroup> groups,
        IReadOnlyDictionary<UnifiedCandidateGroupId, CalibreBookId> selectedKeepers,
        IEnumerable<EditionMetadataProposal> providerProposals)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(selectedKeepers);
        ArgumentNullException.ThrowIfNull(providerProposals);
        HashSet<CalibreBookId> bookIds = books.Select(value => value.Id).ToHashSet();
        EditionMetadataProposal[] proposals = providerProposals.ToArray();
        if (proposals.Any(value => !bookIds.Contains(value.BookId)))
            throw new ArgumentException("Edition metadata proposals must reference current books.");
        List<MetadataReviewSubject> results = [];
        HashSet<CalibreBookId> grouped = [];
        foreach (UnifiedCandidateGroup group in groups.OrderBy(value => value.Id.Value, StringComparer.Ordinal))
        {
            CalibreBookId target = selectedKeepers.GetValueOrDefault(group.Id, group.GeneratedKeeperBookId);
            if (!group.Members.Contains(target))
                throw new ArgumentException("A selected metadata target is not a Unified group member.");
            grouped.UnionWith(group.Members);
            results.Add(new(
                new("metadata-review/group/" + group.Id.Value),
                group.Id,
                group.Members,
                target,
                EditionMetadataFusionPolicy.Fuse(proposals.Where(value => group.Members.Contains(value.BookId)))));
        }
        foreach (CalibreBook book in books.Where(value => !grouped.Contains(value.Id)).OrderBy(value => value.Id.Value))
        {
            results.Add(new(
                new($"metadata-review/book/{book.Id.Value}"),
                null,
                [book.Id],
                book.Id,
                EditionMetadataFusionPolicy.Fuse(proposals.Where(value => value.BookId == book.Id))));
        }
        return new ReadOnlyCollection<MetadataReviewSubject>(results.ToArray());
    }
}
