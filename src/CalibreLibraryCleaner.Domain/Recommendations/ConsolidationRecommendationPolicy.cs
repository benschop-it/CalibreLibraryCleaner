using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed class ConsolidationRecommendationPolicy
{
    private const string InputPolicyVersion = "recommendation-input/1.0.1";
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "UNKNOWN", "UNKNOWN AUTHOR", "UNTITLED", "N/A", "NONE", "-", "?",
    };
    private static readonly HashSet<string> DecisiveEpubRules = new(StringComparer.Ordinal)
    {
        "EPUB.OPEN", "EPUB.ARCHIVE_SAFETY", "EPUB.PACKAGE", "EPUB.NAVIGATION", "EPUB.SPINE.NON_EMPTY",
        "EPUB.SPINE.RESOURCE_EXISTS", "EPUB.RESOURCE.INTERNAL_EXISTS", "EPUB.TEXT.SUBSTANTIAL",
        "EPUB.CHAPTER.EMPTY", "EPUB.STRUCTURE.REPEATED_REFERENCE",
    };
    private static readonly string[] DoiPrefixes = ["https://doi.org/", "http://doi.org/", "doi:"];
    private static readonly string[] EditionMarkers = ["REVISED", "EXPANDED", "ABRIDGED", "UNABRIDGED", "ILLUSTRATED", "ANNIVERSARY EDITION"];

    public ConsolidationRecommendationPolicy(int clearlyPreferredEpubScoreDelta = 10, int materialPublicationYearGap = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clearlyPreferredEpubScoreDelta, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(materialPublicationYearGap, 1);
        ClearlyPreferredEpubScoreDelta = clearlyPreferredEpubScoreDelta;
        MaterialPublicationYearGap = materialPublicationYearGap;
    }

    public int ClearlyPreferredEpubScoreDelta { get; }
    public int MaterialPublicationYearGap { get; }

    public ConsolidationRecommendation Generate(
        LibraryIdentity libraryIdentity,
        ExactMetadataDuplicateGroup group,
        IReadOnlyList<CalibreBook> members,
        IReadOnlyList<ExactBinaryDuplicateGroup> exactBinaryGroups,
        IReadOnlyList<FormatAssessment> epubAssessments,
        IReadOnlyList<LibraryFinding> findings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryIdentity);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(exactBinaryGroups);
        ArgumentNullException.ThrowIfNull(epubAssessments);
        ArgumentNullException.ThrowIfNull(findings);
        cancellationToken.ThrowIfCancellationRequested();

        CalibreBook[] orderedMembers = members.OrderBy(book => book.Id.Value).ToArray();
        RecommendationInputVersion inputVersion = BuildInputVersion(
            libraryIdentity,
            group,
            orderedMembers,
            exactBinaryGroups,
            epubAssessments,
            findings,
            cancellationToken);
        if (orderedMembers.Length < 2
            || !orderedMembers.Select(book => book.Id).SequenceEqual(group.Members)
            || orderedMembers.Select(book => book.Id).Distinct().Count() != orderedMembers.Length)
        {
            return Unsupported(group, inputVersion, "Recommendation inputs do not resolve every group member exactly once.");
        }

        if (!ExactBinaryEvidenceIsConsistent(orderedMembers, exactBinaryGroups))
        {
            return Unsupported(group, inputVersion, "Exact-binary evidence does not match the current format fingerprints.");
        }

        if (!AssessmentEvidenceIsStructurallyConsistent(orderedMembers, epubAssessments))
        {
            return Unsupported(group, inputVersion, "EPUB assessment associations are incomplete or inconsistent.");
        }

        List<RecommendationReason> reasons = [];
        List<RecommendationWarning> warnings = [];
        HashSet<CalibreBookId> retainedSeparate = DetectRetainedSeparate(
            orderedMembers,
            warnings,
            cancellationToken);
        CalibreBook[] cohort = orderedMembers.Where(book => !retainedSeparate.Contains(book.Id)).ToArray();
        if (cohort.Length < 2)
        {
            foreach (CalibreBook book in orderedMembers)
            {
                AddRetainedSeparateReason(book.Id, reasons, warnings);
            }

            return new(
                group.Id,
                group.Members,
                RecommendationModelVersion.V1,
                inputVersion,
                null,
                [],
                orderedMembers.Select(book => new RecordRecommendation(
                    book.Id,
                    RecordRecommendationKind.RetainedSeparate,
                    RecommendationDecisionStrength.Ambiguous,
                    ["RECORD.RETAIN_SEPARATELY"])),
                reasons,
                warnings,
                RecommendationConfidence.ManualReviewRequired);
        }

        MetadataSourceSelection? metadataSource = SelectMetadata(cohort, findings, reasons, warnings);
        if (metadataSource is null)
        {
            warnings.Add(new(
                "INPUT.INCOMPLETE",
                RecommendationWarningSeverity.Blocking,
                RecommendationSubjectKind.Metadata,
                "No group member has a usable stored title and complete usable author set."));
            return new(
                group.Id,
                group.Members,
                RecommendationModelVersion.V1,
                inputVersion,
                null,
                [],
                [],
                reasons,
                warnings,
                RecommendationConfidence.Unsupported);
        }

        Dictionary<(CalibreBookId BookId, string Path), FormatAssessment> assessments = epubAssessments
            .Where(value => cohort.Any(book => book.Id == value.CalibreBookId))
            .ToDictionary(value => (value.CalibreBookId, NormalizeRelativePath(value.ExpectedRelativePath)));
        AddFormatAvailabilityWarning(cohort, warnings);
        List<FormatSourceSelection> formatSelections = [];
        foreach (IGrouping<string, RecommendationFormatCandidate> formatGroup in cohort
                     .SelectMany(book => book.Formats.Select(format => CreateCandidate(book.Id, format, assessments)))
                     .GroupBy(candidate => candidate.Format, StringComparer.Ordinal)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            formatSelections.Add(SelectFormat(
                formatGroup.Key,
                formatGroup.ToArray(),
                metadataSource.SelectedBookId,
                exactBinaryGroups,
                reasons,
                warnings));
        }

        List<RecordRecommendation> records = [];
        foreach (CalibreBook book in orderedMembers.Where(book => retainedSeparate.Contains(book.Id)))
        {
            AddRetainedSeparateReason(book.Id, reasons, warnings);
            records.Add(new(
                book.Id,
                RecordRecommendationKind.RetainedSeparate,
                RecommendationDecisionStrength.Ambiguous,
                ["RECORD.RETAIN_SEPARATELY"]));
        }

        foreach (CalibreBook book in cohort)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CanBeProposedRedundant(book, metadataSource, formatSelections))
            {
                reasons.Add(new(
                    "RECORD.PROPOSED_REDUNDANT_EXACT_FORMATS",
                    RecommendationSubjectKind.Record,
                    "This record is potentially redundant only because every available format is byte-identical to a selected same-format source and it contributes no selected metadata or format.",
                    book.Id));
                records.Add(new(
                    book.Id,
                    RecordRecommendationKind.ProposedRedundant,
                    RecommendationDecisionStrength.Safe,
                    ["RECORD.PROPOSED_REDUNDANT_EXACT_FORMATS"]));
            }
        }

        RecommendationConfidence confidence = DeriveConfidence(
            metadataSource,
            formatSelections,
            records,
            warnings);
        return new(
            group.Id,
            group.Members,
            RecommendationModelVersion.V1,
            inputVersion,
            metadataSource,
            formatSelections,
            records,
            reasons,
            warnings,
            confidence);
    }

    private HashSet<CalibreBookId> DetectRetainedSeparate(
        IReadOnlyList<CalibreBook> members,
        List<RecommendationWarning> warnings,
        CancellationToken cancellationToken)
    {
        HashSet<CalibreBookId> result = [];
        ApplyLanguageConflict(members, result, warnings);
        ApplySeriesConflict(members, result, warnings);
        ApplyConsensusSignal(members, book => EditionSignature(book.Title), "METADATA.EDITION_WORDING_CONFLICT", "Stored edition wording conflicts and requires separate review.", result, warnings);

        foreach (string type in new[] { "ISBN", "DOI", "ASIN", "OCLC" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyStrongIdentifierConflict(members, type, result, warnings);
        }

        Dictionary<int, CalibreBook[]> years = members
            .Where(book => book.PublicationMetadata.PublicationYear is not null)
            .GroupBy(book => book.PublicationMetadata.PublicationYear!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (years.Count > 1)
        {
            int min = years.Keys.Min();
            int max = years.Keys.Max();
            warnings.Add(new(
                "METADATA.PUBLICATION_YEAR_CONFLICT",
                max - min >= MaterialPublicationYearGap ? RecommendationWarningSeverity.ManualReview : RecommendationWarningSeverity.Advisory,
                RecommendationSubjectKind.Metadata,
                $"Known stored publication years differ ({min} through {max})."));
            if (max - min >= MaterialPublicationYearGap)
            {
                KeyValuePair<int, CalibreBook[]>[] consensus = years.Where(pair => pair.Value.Length >= 2).ToArray();
                if (consensus.Length == 1)
                {
                    foreach (CalibreBook outlier in years.Where(pair => pair.Key != consensus[0].Key).SelectMany(pair => pair.Value)) result.Add(outlier.Id);
                }
                else
                {
                    foreach (CalibreBook member in members) result.Add(member.Id);
                }
            }
        }

        return result;
    }

    private static void ApplyLanguageConflict(
        IReadOnlyList<CalibreBook> members,
        HashSet<CalibreBookId> retainedSeparate,
        List<RecommendationWarning> warnings)
    {
        (CalibreBook Book, HashSet<string> Languages)[] known = members
            .Select(book => (book, book.PublicationMetadata.Languages.Select(NormalizeText).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal)))
            .Where(value => value.Item2.Count > 0)
            .Select(value => (value.book, value.Item2))
            .ToArray();
        bool hasDisjointPair = known.SelectMany((left, index) => known.Skip(index + 1).Select(right => (left, right)))
            .Any(pair => !pair.left.Languages.Overlaps(pair.right.Languages));
        if (!hasDisjointPair)
        {
            return;
        }

        warnings.Add(new(
            "METADATA.LANGUAGE_CONFLICT",
            RecommendationWarningSeverity.ManualReview,
            RecommendationSubjectKind.Metadata,
            "Disjoint non-empty stored language sets may indicate a translation or different edition."));
        IGrouping<string, (CalibreBook Book, HashSet<string> Languages)>[] signatures = known
            .GroupBy(value => string.Join('|', value.Languages.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToArray();
        IGrouping<string, (CalibreBook Book, HashSet<string> Languages)>[] consensus = signatures.Where(value => value.Count() >= 2).ToArray();
        if (consensus.Length == 1)
        {
            HashSet<string> consensusLanguages = consensus[0].First().Languages;
            foreach ((CalibreBook book, HashSet<string> languages) in known.Where(value => !value.Languages.Overlaps(consensusLanguages))) retainedSeparate.Add(book.Id);
        }
        else
        {
            foreach (CalibreBook member in members) retainedSeparate.Add(member.Id);
        }
    }

    private static void ApplyConsensusSignal(
        IReadOnlyList<CalibreBook> members,
        Func<CalibreBook, string?> selector,
        string warningCode,
        string explanation,
        HashSet<CalibreBookId> retainedSeparate,
        List<RecommendationWarning> warnings)
    {
        IGrouping<string, CalibreBook>[] values = members
            .Select(book => (Book: book, Value: selector(book)))
            .Where(value => value.Value is not null)
            .GroupBy(value => value.Value!, value => value.Book, StringComparer.Ordinal)
            .ToArray();
        if (values.Length <= 1)
        {
            return;
        }

        warnings.Add(new(warningCode, RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Metadata, explanation));
        IGrouping<string, CalibreBook>[] consensus = values.Where(value => value.Count() >= 2).ToArray();
        if (consensus.Length == 1)
        {
            foreach (CalibreBook outlier in values.Where(value => !ReferenceEquals(value, consensus[0])).SelectMany(value => value)) retainedSeparate.Add(outlier.Id);
        }
        else
        {
            foreach (CalibreBook member in members) retainedSeparate.Add(member.Id);
        }
    }

    private static void ApplyStrongIdentifierConflict(
        IReadOnlyList<CalibreBook> members,
        string type,
        HashSet<CalibreBookId> retainedSeparate,
        List<RecommendationWarning> warnings)
    {
        (CalibreBook Book, HashSet<string> Values)[] known = members
            .Select(book => (book, StrongIdentifierValues(book, type)))
            .Where(value => value.Item2.Count > 0)
            .Select(value => (value.book, value.Item2))
            .ToArray();
        bool hasDisjointPair = known.SelectMany((left, index) => known.Skip(index + 1).Select(right => (left, right)))
            .Any(pair => !pair.left.Values.Overlaps(pair.right.Values));
        if (!hasDisjointPair)
        {
            return;
        }

        warnings.Add(new(
            "IDENTIFIER.STRONG_CONFLICT",
            RecommendationWarningSeverity.ManualReview,
            RecommendationSubjectKind.Metadata,
            $"Valid stored {type} identifiers are disjoint and may describe different editions."));
        IGrouping<string, (CalibreBook Book, HashSet<string> Values)>[] consensus = known
            .GroupBy(value => string.Join('|', value.Values.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .ToArray();
        if (consensus.Length == 1)
        {
            HashSet<string> consensusValues = consensus[0].First().Values;
            foreach ((CalibreBook book, HashSet<string> values) in known.Where(value => !value.Values.Overlaps(consensusValues)))
            {
                retainedSeparate.Add(book.Id);
            }
        }
        else
        {
            foreach (CalibreBook member in members)
            {
                retainedSeparate.Add(member.Id);
            }
        }
    }

    private static void ApplySeriesConflict(
        IReadOnlyList<CalibreBook> members,
        HashSet<CalibreBookId> retainedSeparate,
        List<RecommendationWarning> warnings)
    {
        ApplyConsensusSignal(
            members,
            book => NormalizeOptional(book.PublicationMetadata.Series),
            "METADATA.SERIES_CONFLICT",
            "Stored series names conflict and may indicate a different edition.",
            retainedSeparate,
            warnings);

        foreach (IGrouping<string, CalibreBook> seriesGroup in members
                     .Where(book => NormalizeOptional(book.PublicationMetadata.Series) is not null)
                     .GroupBy(book => NormalizeOptional(book.PublicationMetadata.Series)!, StringComparer.Ordinal))
        {
            IGrouping<decimal, CalibreBook>[] indices = seriesGroup
                .Where(book => book.PublicationMetadata.SeriesIndex is not null)
                .GroupBy(book => book.PublicationMetadata.SeriesIndex!.Value)
                .ToArray();
            if (indices.Length <= 1)
            {
                continue;
            }

            if (!warnings.Any(value => value.Code == "METADATA.SERIES_CONFLICT"))
            {
                warnings.Add(new(
                    "METADATA.SERIES_CONFLICT",
                    RecommendationWarningSeverity.ManualReview,
                    RecommendationSubjectKind.Metadata,
                    "Known stored indices for the same series conflict and may indicate a different edition."));
            }

            IGrouping<decimal, CalibreBook>[] consensus = indices.Where(group => group.Count() >= 2).ToArray();
            if (consensus.Length == 1)
            {
                foreach (CalibreBook outlier in indices.Where(group => group.Key != consensus[0].Key).SelectMany(group => group))
                {
                    retainedSeparate.Add(outlier.Id);
                }
            }
            else
            {
                foreach (CalibreBook book in seriesGroup.Where(book => book.PublicationMetadata.SeriesIndex is not null))
                {
                    retainedSeparate.Add(book.Id);
                }
            }
        }
    }

    private static MetadataSourceSelection? SelectMetadata(
        IReadOnlyList<CalibreBook> cohort,
        IReadOnlyList<LibraryFinding> findings,
        List<RecommendationReason> reasons,
        List<RecommendationWarning> warnings)
    {
        AddMetadataInputWarnings(cohort, warnings);
        MetadataCandidateComparison[] comparisons = cohort
            .Select(book => new MetadataCandidateComparison(book.Id, BuildMetadataVector(book, cohort, findings)))
            .OrderByDescending(value => value.Vector.CoreUsable)
            .ThenByDescending(value => value.Vector.CatalogIntegrity)
            .ThenBy(value => value.Vector.ConflictCount)
            .ThenByDescending(value => value.Vector.CompletenessCount)
            .ThenByDescending(value => value.Vector.GroupConsistencyCount)
            .ThenByDescending(value => value.Vector.ValidStrongIdentifierCount)
            .ThenBy(value => value.BookId.Value)
            .ToArray();
        if (comparisons.Length == 0 || !comparisons[0].Vector.CoreUsable)
        {
            return null;
        }

        MetadataCandidateComparison selected = comparisons[0];
        MetadataCandidateComparison? runnerUp = comparisons.Skip(1).FirstOrDefault();
        bool tie = runnerUp is not null && selected.Vector == runnerUp.Vector;
        List<string> selectionReasons = [];
        if (tie)
        {
            AddMetadataReason("METADATA.TIE_BROKEN_BY_RECORD_ID", "Stored metadata quality facts are equal; the lowest stable Calibre record ID is used only as a deterministic fallback, not as a quality signal.");
        }
        else if (runnerUp is not null)
        {
            if (selected.Vector.CoreUsable != runnerUp.Vector.CoreUsable) AddMetadataReason("METADATA.CORE_USABILITY", "The selected record has usable stored title and complete usable authorship.");
            if (selected.Vector.CatalogIntegrity != runnerUp.Vector.CatalogIntegrity) AddMetadataReason("METADATA.CATALOG_INTEGRITY", "The selected record has stronger catalog-integrity evidence.");
            if (selected.Vector.ConflictCount != runnerUp.Vector.ConflictCount) AddMetadataReason("METADATA.FEWER_CONFLICTS", "The selected record has fewer contradictions with known group metadata consensus.");
            if (selected.Vector.CompletenessCount != runnerUp.Vector.CompletenessCount) AddMetadataReason("METADATA.MORE_COMPLETE", "The selected record has more complete stored metadata in the documented fixed fields.");
            if (selected.Vector.GroupConsistencyCount != runnerUp.Vector.GroupConsistencyCount) AddMetadataReason("METADATA.MORE_CONSISTENT", "The selected record agrees with more known group metadata consensus values.");
            if (selected.Vector.ValidStrongIdentifierCount != runnerUp.Vector.ValidStrongIdentifierCount) AddMetadataReason("METADATA.MORE_VALID_IDENTIFIERS", "The selected record contains more locally validated strong identifiers.");
        }

        if (selectionReasons.Count == 0) AddMetadataReason("METADATA.MORE_COMPLETE", "This record leads the documented metadata comparison vector without combining or rewriting stored fields.");
        if (cohort.Any(book => book.PublicationMetadata.Languages.Count == 0))
        {
            warnings.Add(new(
                "METADATA.LANGUAGE_INCOMPLETE",
                RecommendationWarningSeverity.Advisory,
                RecommendationSubjectKind.Metadata,
                "At least one record has no stored language, so language consistency is incomplete."));
        }

        return new(
            selected.BookId,
            comparisons,
            tie ? RecommendationDecisionStrength.Safe : RecommendationDecisionStrength.Strong,
            selectionReasons);

        void AddMetadataReason(string code, string explanation)
        {
            reasons.Add(new(code, RecommendationSubjectKind.Metadata, explanation, selected.BookId));
            selectionReasons.Add(code);
        }
    }

    private static MetadataQualityVector BuildMetadataVector(
        CalibreBook book,
        IReadOnlyList<CalibreBook> cohort,
        IReadOnlyList<LibraryFinding> findings)
    {
        bool titleUsable = IsUsable(book.Title);
        bool authorsUsable = book.Authors.Count > 0 && book.Authors.All(author => IsUsable(author.Name));
        bool integrity = !findings.Any(finding => finding.BookId == book.Id
            && finding.Code is "AUTHOR_REFERENCE_MISSING" or "CATALOG_VALUE_INVALID");
        int strongIdentifiers = book.Identifiers.Count(identifier => NormalizeStrongIdentifier(identifier.Type, identifier.Value) is not null);
        BookPublicationMetadata metadata = book.PublicationMetadata;
        int completeness = 0;
        if (!string.IsNullOrWhiteSpace(book.AuthorSort) && !Placeholders.Contains(NormalizeText(book.AuthorSort))) completeness++;
        if (strongIdentifiers > 0) completeness++;
        if (metadata.Languages.Count > 0) completeness++;
        if (metadata.PublicationDate is not null) completeness++;
        if (metadata.Publisher is not null && !Placeholders.Contains(NormalizeText(metadata.Publisher))) completeness++;
        if (metadata.Series is not null && metadata.SeriesIndex is not null) completeness++;
        if (metadata.HasCover) completeness++;
        int conflicts = GroupConflictCount(book, cohort);
        int consistency = GroupConsistencyCount(book, cohort);
        return new(titleUsable && authorsUsable, integrity, conflicts, completeness, consistency, strongIdentifiers);
    }

    private static int GroupConsistencyCount(CalibreBook book, IReadOnlyList<CalibreBook> cohort)
    {
        int count = 0;
        if (KnownValuesAgree(cohort.Select(value => NormalizeOptional(value.PublicationMetadata.Publisher)), NormalizeOptional(book.PublicationMetadata.Publisher))) count++;
        if (KnownValuesAgree(cohort.Select(value => LanguageSignature(value.PublicationMetadata.Languages)), LanguageSignature(book.PublicationMetadata.Languages))) count++;
        if (KnownValuesAgree(cohort.Select(value => value.PublicationMetadata.PublicationYear?.ToString(CultureInfo.InvariantCulture)), book.PublicationMetadata.PublicationYear?.ToString(CultureInfo.InvariantCulture))) count++;
        if (KnownValuesAgree(cohort.Select(value => SeriesSignature(value.PublicationMetadata)), SeriesSignature(book.PublicationMetadata))) count++;
        return count;
    }

    private static void AddMetadataInputWarnings(
        IReadOnlyList<CalibreBook> cohort,
        List<RecommendationWarning> warnings)
    {
        foreach (CalibreBook book in cohort)
        {
            bool invalidStrongIdentifier = book.Identifiers.Any(identifier =>
                CanonicalIdentifierType(identifier.Type).Length > 0
                && NormalizeStrongIdentifier(identifier.Type, identifier.Value) is null);
            if (invalidStrongIdentifier)
            {
                warnings.Add(new(
                    "IDENTIFIER.STRONG_INVALID",
                    RecommendationWarningSeverity.Advisory,
                    RecommendationSubjectKind.Metadata,
                    "At least one claimed strong identifier is invalid and provides no recommendation strength.",
                    book.Id));
            }

            bool placeholder = !IsUsable(book.Title)
                || book.Authors.Any(author => !IsUsable(author.Name))
                || !string.IsNullOrWhiteSpace(book.AuthorSort) && Placeholders.Contains(NormalizeText(book.AuthorSort))
                || book.PublicationMetadata.Publisher is not null && Placeholders.Contains(NormalizeText(book.PublicationMetadata.Publisher));
            if (placeholder)
            {
                warnings.Add(new(
                    "METADATA.PLACEHOLDER_VALUE",
                    RecommendationWarningSeverity.Advisory,
                    RecommendationSubjectKind.Metadata,
                    "A frozen placeholder value lowers metadata-source quality; stored metadata was not rewritten.",
                    book.Id));
            }
        }
    }

    private static void AddFormatAvailabilityWarning(
        IReadOnlyList<CalibreBook> cohort,
        List<RecommendationWarning> warnings)
    {
        foreach (CalibreBook book in cohort.Where(book => book.Formats.Count == 0))
        {
            warnings.Add(new(
                "FORMAT.NO_DECLARED_FORMATS",
                RecommendationWarningSeverity.ManualReview,
                RecommendationSubjectKind.Record,
                "This record has no declared formats, so its contribution and redundancy cannot be concluded automatically.",
                book.Id));
        }

        HashSet<string>[] sets = cohort.Select(book => book.Formats
                .Select(format => format.Format.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal))
            .ToArray();
        bool substantiallyDifferent = sets.SelectMany((left, index) => sets.Skip(index + 1).Select(right => (left, right)))
            .Any(pair => pair.left.Count > 0 && pair.right.Count > 0
                && (!pair.left.Overlaps(pair.right)
                    || pair.left.Except(pair.right, StringComparer.Ordinal).Count()
                    + pair.right.Except(pair.left, StringComparer.Ordinal).Count() >= 2));
        if (substantiallyDifferent)
        {
            warnings.Add(new(
                "FORMAT.SUBSTANTIALLY_DIFFERENT_AVAILABILITY",
                RecommendationWarningSeverity.Advisory,
                RecommendationSubjectKind.Group,
                "Group records have substantially different declared format sets; complementary formats remain retained but confidence is lowered."));
        }
    }

    private static int GroupConflictCount(CalibreBook book, IReadOnlyList<CalibreBook> cohort)
    {
        int count = 0;
        if (ContradictsConsensus(cohort.Select(value => NormalizeOptional(value.PublicationMetadata.Publisher)), NormalizeOptional(book.PublicationMetadata.Publisher))) count++;
        if (ContradictsConsensus(cohort.Select(value => LanguageSignature(value.PublicationMetadata.Languages)), LanguageSignature(book.PublicationMetadata.Languages))) count++;
        if (ContradictsConsensus(cohort.Select(value => value.PublicationMetadata.PublicationYear?.ToString(CultureInfo.InvariantCulture)), book.PublicationMetadata.PublicationYear?.ToString(CultureInfo.InvariantCulture))) count++;
        if (ContradictsConsensus(cohort.Select(value => SeriesSignature(value.PublicationMetadata)), SeriesSignature(book.PublicationMetadata))) count++;
        return count;
    }

    private static bool ContradictsConsensus(IEnumerable<string?> values, string? candidate)
    {
        string? consensus = UniqueConsensus(values);
        return candidate is not null && consensus is not null && !string.Equals(candidate, consensus, StringComparison.Ordinal);
    }

    private static string? UniqueConsensus(IEnumerable<string?> values)
    {
        var consensus = values.Where(value => value is not null)
            .GroupBy(value => value!, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .ToArray();
        return consensus.Length == 1 || consensus.Length > 1 && consensus[0].Count() > consensus[1].Count()
            ? consensus[0].Key
            : null;
    }

    private static bool KnownValuesAgree(IEnumerable<string?> values, string? candidate)
    {
        string? consensus = UniqueConsensus(values);
        if (consensus is null)
        {
            string[] known = values.Where(value => value is not null).Distinct(StringComparer.Ordinal).ToArray()!;
            consensus = known.Length == 1 ? known[0] : null;
        }

        return candidate is not null && consensus is not null && string.Equals(consensus, candidate, StringComparison.Ordinal);
    }

    private FormatSourceSelection SelectFormat(
        string format,
        RecommendationFormatCandidate[] candidates,
        CalibreBookId metadataSource,
        IReadOnlyList<ExactBinaryDuplicateGroup> exactBinaryGroups,
        List<RecommendationReason> reasons,
        List<RecommendationWarning> warnings)
    {
        RecommendationFormatCandidate[] present = candidates.Where(value => value.FileStatus == FormatFileStatus.Present).ToArray();
        RecommendationFormatCandidate[] unavailable = candidates.Where(value => value.FileStatus != FormatFileStatus.Present).ToArray();
        if (present.Length == 0)
        {
            const string warningCode = "FORMAT.FILE_UNAVAILABLE";
            warnings.Add(new(warningCode, RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Format, "No declared candidate file is currently available; every declaration remains visible.", format: format));
            return new(format, candidates, null, FormatResolutionStatus.Unavailable, [], RecommendationDecisionStrength.Unsupported, [], [warningCode]);
        }

        if (unavailable.Length > 0)
        {
            warnings.Add(new(
                "FORMAT.FILE_UNAVAILABLE",
                RecommendationWarningSeverity.ManualReview,
                RecommendationSubjectKind.Format,
                "At least one declared candidate file is unavailable and could conceal a unique alternative.",
                format: format));
        }

        if (present.Length == 1)
        {
            RecommendationFormatCandidate source = present[0];
            string code = "FORMAT.ONLY_AVAILABLE_SOURCE";
            reasons.Add(new(code, RecommendationSubjectKind.Format, "This is the only safely present source for the format; no quality comparison was made.", source.BookId, format));
            if (format == "EPUB" && source.Assessment?.Status == AssessmentStatus.Disqualified)
            {
                warnings.Add(new("EPUB.ONLY_SOURCE_DISQUALIFIED", RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Assessment, "The sole EPUB is retained because it is the only available copy, but its assessment is disqualified.", source.BookId, format));
            }

            return new(format, candidates, source, FormatResolutionStatus.Selected, [], RecommendationDecisionStrength.Strong, [code], unavailable.Length > 0 ? ["FORMAT.FILE_UNAVAILABLE"] : []);
        }

        if (AreAllExactBinary(present, exactBinaryGroups))
        {
            RecommendationFormatCandidate source = present
                .OrderBy(value => value.BookId == metadataSource ? 0 : 1)
                .ThenBy(value => value.BookId.Value)
                .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
                .First();
            string code = "FORMAT.EXACT_BINARY_EQUIVALENT";
            reasons.Add(new(code, RecommendationSubjectKind.Format, "All present alternatives have the same byte length and SHA-256. Record ID/path ordering is used only for deterministic source choice.", source.BookId, format));
            ProposedFormatAlternative[] exclusions = present.Where(value => value != source)
                .Select(value => new ProposedFormatAlternative(value.BookId, value.ExpectedRelativePath, code))
                .ToArray();
            return new(format, candidates, source, FormatResolutionStatus.Selected, exclusions, RecommendationDecisionStrength.Safe, [code], unavailable.Length > 0 ? ["FORMAT.FILE_UNAVAILABLE"] : []);
        }

        if (format != "EPUB")
        {
            return UnresolvedUnassessed(format, candidates, warnings);
        }

        RecommendationFormatCandidate? preferred = SelectPreferredEpub(present, reasons, warnings);
        if (preferred is null)
        {
            const string warningCode = "EPUB.SCORES_TOO_CLOSE";
            if (!warnings.Any(value => value.Code == warningCode && value.Format == format))
            {
                warnings.Add(new(
                    warningCode,
                    RecommendationWarningSeverity.ManualReview,
                    RecommendationSubjectKind.Assessment,
                    "Non-identical EPUB assessments are close, contradictory, missing, stale, or incomparable; no source is proposed.",
                    format: format,
                    evidence: BuildEpubDecisionEvidence(present, null)));
            }

            return new(format, candidates, null, FormatResolutionStatus.UnresolvedConflict, [], RecommendationDecisionStrength.Ambiguous, [], [warningCode]);
        }

        string reasonCode = preferred.Assessment?.Status == AssessmentStatus.Completed
            && present.Any(value => value.Assessment?.Status == AssessmentStatus.Disqualified)
            ? "EPUB.VALID_OVER_DISQUALIFIED"
            : "EPUB.MATERIAL_QUALITY_ADVANTAGE";
        return new(format, candidates, preferred, FormatResolutionStatus.Selected, [], RecommendationDecisionStrength.Strong, [reasonCode], unavailable.Length > 0 ? ["FORMAT.FILE_UNAVAILABLE"] : []);
    }

    private RecommendationFormatCandidate? SelectPreferredEpub(
        RecommendationFormatCandidate[] candidates,
        List<RecommendationReason> reasons,
        List<RecommendationWarning> warnings)
    {
        if (candidates.Any(candidate => !AssessmentMatches(candidate)))
        {
            warnings.Add(new("ASSESSMENT.STALE_OR_INCOMPARABLE", RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Assessment, "An EPUB assessment is absent or does not match the current candidate fingerprint.", format: "EPUB"));
            return null;
        }

        AnalyzerVersion[] analyzerVersions = candidates.Select(value => value.Assessment!.AnalyzerVersion).Distinct().ToArray();
        ScoringModelVersion[] scoringVersions = candidates.Select(value => value.Assessment!.ScoringModelVersion).Distinct().ToArray();
        if (analyzerVersions.Length != 1 || scoringVersions.Length != 1)
        {
            warnings.Add(new("ASSESSMENT.STALE_OR_INCOMPARABLE", RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Assessment, "EPUB assessments use incompatible analyzer or scoring-model versions.", format: "EPUB"));
            return null;
        }

        RecommendationFormatCandidate[] completed = candidates.Where(value => value.Assessment!.Status == AssessmentStatus.Completed).ToArray();
        if (completed.Length == 1 && candidates.Length > 1 && candidates.All(value => value == completed[0]
                || value.Assessment!.Status == AssessmentStatus.Disqualified && HasDecisiveDisqualifier(value.Assessment)))
        {
            RecommendationFormatCandidate source = completed[0];
            reasons.Add(new("EPUB.VALID_OVER_DISQUALIFIED", RecommendationSubjectKind.Assessment, "A completed EPUB assessment is preferred over alternatives disqualified by decisive safety or readability findings; this does not assert content equivalence.", source.BookId, "EPUB", BuildEpubDecisionEvidence(candidates, source)));
            return source;
        }

        if (completed.Length != candidates.Length)
        {
            return null;
        }

        RecommendationFormatCandidate best = completed.OrderByDescending(value => value.Assessment!.Score!.Value.Value).ThenBy(value => value.BookId.Value).First();
        foreach (RecommendationFormatCandidate competitor in completed.Where(value => value != best))
        {
            int gap = best.Assessment!.Score!.Value.Value - competitor.Assessment!.Score!.Value.Value;
            if (gap < ClearlyPreferredEpubScoreDelta || !HasDecisiveAdvantage(best.Assessment, competitor.Assessment))
            {
                return null;
            }
        }

        reasons.Add(new(
            "EPUB.MATERIAL_QUALITY_ADVANTAGE",
            RecommendationSubjectKind.Assessment,
            $"The EPUB score is at least {ClearlyPreferredEpubScoreDelta} points above every competitor and decisive structural/readability findings support the difference; no content-equivalence claim is made.",
            best.BookId,
            "EPUB",
            BuildEpubDecisionEvidence(candidates, best)));
        return best;
    }

    private static bool HasDecisiveAdvantage(FormatAssessment best, FormatAssessment competitor)
    {
        Dictionary<string, int> bestAdjustments = DecisiveAdjustments(best);
        Dictionary<string, int> competitorAdjustments = DecisiveAdjustments(competitor);
        bool decisiveDifference = DecisiveEpubRules.Any(rule =>
            bestAdjustments.GetValueOrDefault(rule) - competitorAdjustments.GetValueOrDefault(rule) >= 4);
        bool countervailing = best.Findings.Any(finding => DecisiveEpubRules.Contains(finding.RuleId)
            && finding.Severity is FindingSeverity.Error or FindingSeverity.Disqualifying
            && !competitor.Findings.Any(other => other.RuleId == finding.RuleId
                && other.Severity is FindingSeverity.Error or FindingSeverity.Disqualifying));
        return decisiveDifference && !countervailing;
    }

    private static Dictionary<string, int> DecisiveAdjustments(FormatAssessment assessment) => assessment.Findings
        .Where(finding => DecisiveEpubRules.Contains(finding.RuleId))
        .GroupBy(finding => finding.RuleId, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Sum(finding => finding.ScoreAdjustment), StringComparer.Ordinal);

    private static bool HasDecisiveDisqualifier(FormatAssessment assessment) => assessment.Findings.Any(finding =>
        finding.Severity == FindingSeverity.Disqualifying && DecisiveEpubRules.Contains(finding.RuleId));

    private static Dictionary<string, string> BuildEpubDecisionEvidence(
        RecommendationFormatCandidate[] candidates,
        RecommendationFormatCandidate? selected)
    {
        Dictionary<string, string> evidence = new(StringComparer.Ordinal)
        {
            ["selectedRecordId"] = selected?.BookId.Value.ToString(CultureInfo.InvariantCulture) ?? "none",
        };
        foreach (RecommendationFormatCandidate candidate in candidates.OrderBy(value => value.BookId.Value).Take(12))
        {
            string prefix = $"record.{candidate.BookId.Value}.";
            if (candidate.Assessment is null)
            {
                evidence[prefix + "status"] = "missing";
                continue;
            }

            FormatAssessment assessment = candidate.Assessment;
            evidence[prefix + "status"] = assessment.Status.ToString();
            evidence[prefix + "score"] = assessment.Score?.Value.ToString(CultureInfo.InvariantCulture) ?? "not-scored";
            evidence[prefix + "analyzerVersion"] = assessment.AnalyzerVersion.Value;
            evidence[prefix + "scoringModelVersion"] = assessment.ScoringModelVersion.Value;
            evidence[prefix + "decisiveFindings"] = BoundEvidence(string.Join(',', assessment.Findings
                .Where(finding => DecisiveEpubRules.Contains(finding.RuleId))
                .Select(finding => $"{finding.RuleId}:{finding.Severity}:{finding.ScoreAdjustment}")
                .Order(StringComparer.Ordinal)));
            evidence[prefix + "decisiveExplanations"] = BoundEvidence(string.Join(" | ", assessment.Findings
                .Where(finding => DecisiveEpubRules.Contains(finding.RuleId))
                .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.Explanation, StringComparer.Ordinal)
                .Take(4)
                .Select(finding => $"{finding.RuleId}: {finding.Explanation}")));
        }

        if (candidates.Length > 12)
        {
            evidence["omittedCandidateCount"] = (candidates.Length - 12).ToString(CultureInfo.InvariantCulture);
        }

        return evidence;
    }

    private static string BoundEvidence(string value) => value.Length <= 512 ? value : value[..512];

    private static bool AssessmentMatches(RecommendationFormatCandidate candidate) => candidate.Assessment is not null
        && candidate.Fingerprint is not null
        && candidate.Assessment.ObservedFingerprint == candidate.Fingerprint;

    private static FormatSourceSelection UnresolvedUnassessed(
        string format,
        RecommendationFormatCandidate[] candidates,
        List<RecommendationWarning> warnings)
    {
        const string code = "FORMAT.UNASSESSED_NONIDENTICAL_CONFLICT";
        warnings.Add(new(code, RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Format, "Non-identical same-format files have no quality analyzer; all alternatives remain unresolved and none is ranked by size, filename, timestamp, path, metadata source, or record ID.", format: format));
        return new(format, candidates, null, FormatResolutionStatus.UnresolvedConflict, [], RecommendationDecisionStrength.Ambiguous, [], [code]);
    }

    private static bool AreAllExactBinary(
        RecommendationFormatCandidate[] candidates,
        IReadOnlyList<ExactBinaryDuplicateGroup> groups)
    {
        foreach (ExactBinaryDuplicateGroup group in groups)
        {
            if (candidates.All(candidate => group.Members.Any(member => member.BookId == candidate.BookId
                    && string.Equals(member.Format, candidate.Format, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(member.ExpectedRelativePath, candidate.ExpectedRelativePath, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExactBinaryEvidenceIsConsistent(
        IReadOnlyList<CalibreBook> members,
        IReadOnlyList<ExactBinaryDuplicateGroup> groups)
    {
        Dictionary<(CalibreBookId BookId, string Format, string Path), BookFormat> formats = [];
        foreach (CalibreBook book in members)
        {
            foreach (BookFormat format in book.Formats)
            {
                if (!formats.TryAdd((book.Id, format.Format.ToUpperInvariant(), format.ExpectedRelativePath), format)) return false;
            }
        }

        HashSet<(CalibreBookId BookId, string Format, string Path)> seen = [];
        foreach (ExactBinaryDuplicateGroup group in groups.Where(group => group.Members.Any(member => members.Any(book => book.Id == member.BookId))))
        {
            foreach (ExactBinaryDuplicateMember member in group.Members.Where(member => members.Any(book => book.Id == member.BookId)))
            {
                var key = (member.BookId, member.Format.ToUpperInvariant(), member.ExpectedRelativePath);
                if (!seen.Add(key)
                    || !formats.TryGetValue(key, out BookFormat? format)
                    || format.FileStatus != FormatFileStatus.Present
                    || format.Fingerprint != group.Fingerprint)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AssessmentEvidenceIsStructurallyConsistent(
        IReadOnlyList<CalibreBook> members,
        IReadOnlyList<FormatAssessment> assessments)
    {
        HashSet<(CalibreBookId BookId, string Format, string Path)> seen = [];
        foreach (FormatAssessment assessment in assessments.Where(value => members.Any(book => book.Id == value.CalibreBookId)))
        {
            if (!seen.Add((assessment.CalibreBookId, assessment.Format, assessment.ExpectedRelativePath))
                || !string.Equals(assessment.Format, "EPUB", StringComparison.Ordinal)
                || !members.Any(book => book.Id == assessment.CalibreBookId
                    && book.Formats.Any(format => string.Equals(format.Format, "EPUB", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(NormalizeRelativePath(format.ExpectedRelativePath), NormalizeRelativePath(assessment.ExpectedRelativePath), StringComparison.Ordinal))))
            {
                return false;
            }
        }

        return true;
    }

    private static RecommendationFormatCandidate CreateCandidate(
        CalibreBookId bookId,
        BookFormat format,
        Dictionary<(CalibreBookId BookId, string Path), FormatAssessment> assessments)
    {
        assessments.TryGetValue((bookId, NormalizeRelativePath(format.ExpectedRelativePath)), out FormatAssessment? assessment);
        return new(bookId, format.Format, format.ExpectedRelativePath, format.FileStatus, format.Fingerprint, assessment);
    }

    private static string NormalizeRelativePath(string value) => value.Replace('\\', '/');

    private static bool CanBeProposedRedundant(
        CalibreBook book,
        MetadataSourceSelection metadataSource,
        IReadOnlyList<FormatSourceSelection> selections)
    {
        if (book.Id == metadataSource.SelectedBookId || book.Formats.Count == 0
            || book.Formats.Any(format => format.FileStatus != FormatFileStatus.Present))
        {
            return false;
        }

        foreach (BookFormat format in book.Formats)
        {
            FormatSourceSelection? selection = selections.FirstOrDefault(value => value.Format == format.Format);
            if (selection is null || selection.ResolutionStatus != FormatResolutionStatus.Selected
                || selection.ProposedSource?.BookId == book.Id
                || !selection.ProposedExcludedAlternatives.Any(value => value.BookId == book.Id
                    && value.ExpectedRelativePath == format.ExpectedRelativePath
                    && value.ReasonCode == "FORMAT.EXACT_BINARY_EQUIVALENT"))
            {
                return false;
            }
        }

        return true;
    }

    private static RecommendationConfidence DeriveConfidence(
        MetadataSourceSelection metadata,
        IReadOnlyList<FormatSourceSelection> formats,
        IReadOnlyList<RecordRecommendation> records,
        List<RecommendationWarning> warnings)
    {
        if (formats.Any(value => value.ResolutionStatus is FormatResolutionStatus.UnresolvedConflict or FormatResolutionStatus.Unavailable)
            || records.Any(value => value.Kind == RecordRecommendationKind.RetainedSeparate)
            || warnings.Any(value => value.Severity is RecommendationWarningSeverity.ManualReview or RecommendationWarningSeverity.Blocking))
        {
            return RecommendationConfidence.ManualReviewRequired;
        }

        if (warnings.Count > 0)
        {
            return RecommendationConfidence.Low;
        }

        if (formats.All(value => value.Strength == RecommendationDecisionStrength.Safe)
            && metadata.Strength == RecommendationDecisionStrength.Safe
            && warnings.Count == 0)
        {
            return RecommendationConfidence.Deterministic;
        }

        if (formats.Any(value => value.Strength == RecommendationDecisionStrength.Strong))
        {
            return formats.Any(value => value.ReasonCodes.Contains("FORMAT.ONLY_AVAILABLE_SOURCE", StringComparer.Ordinal))
                ? RecommendationConfidence.Medium
                : RecommendationConfidence.High;
        }

        return RecommendationConfidence.Medium;
    }

    private static ConsolidationRecommendation Unsupported(
        ExactMetadataDuplicateGroup group,
        RecommendationInputVersion inputVersion,
        string explanation)
    {
        RecommendationWarning warning = new("INPUT.INCOMPLETE", RecommendationWarningSeverity.Blocking, RecommendationSubjectKind.Group, explanation);
        return new(group.Id, group.Members, RecommendationModelVersion.V1, inputVersion, null, [], [], [], [warning], RecommendationConfidence.Unsupported);
    }

    private static void AddRetainedSeparateReason(
        CalibreBookId bookId,
        List<RecommendationReason> reasons,
        List<RecommendationWarning> warnings)
    {
        reasons.Add(new("RECORD.RETAIN_SEPARATELY", RecommendationSubjectKind.Record, "This record and all its formats remain separate because conservative stored metadata evidence makes cross-record consolidation unsafe.", bookId));
        if (!warnings.Any(value => value.Code == "RECORD.RETAIN_SEPARATELY" && value.BookId == bookId))
        {
            warnings.Add(new("RECORD.RETAIN_SEPARATELY", RecommendationWarningSeverity.ManualReview, RecommendationSubjectKind.Record, "Review this record separately; none of its formats participates in the proposed consolidated selection.", bookId));
        }
    }

    private static RecommendationInputVersion BuildInputVersion(
        LibraryIdentity library,
        ExactMetadataDuplicateGroup group,
        IReadOnlyList<CalibreBook> members,
        IReadOnlyList<ExactBinaryDuplicateGroup> binaryGroups,
        IReadOnlyList<FormatAssessment> assessments,
        IReadOnlyList<LibraryFinding> findings,
        CancellationToken cancellationToken)
    {
        StringBuilder canonical = new();
        Append(canonical, InputPolicyVersion);
        Append(canonical, library.CalibreLibraryUuid);
        Append(canonical, library.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, group.Id.Value);
        foreach (CalibreBook book in members.OrderBy(value => value.Id.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(canonical, book.Id.Value.ToString(CultureInfo.InvariantCulture));
            Append(canonical, book.Title);
            Append(canonical, book.AuthorSort);
            foreach (BookAuthor author in book.Authors) { Append(canonical, author.Name); Append(canonical, author.SortName); }
            foreach (BookIdentifier identifier in book.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal)) { Append(canonical, identifier.Type); Append(canonical, identifier.Value); }
            BookPublicationMetadata publication = book.PublicationMetadata;
            Append(canonical, publication.Publisher ?? string.Empty);
            Append(canonical, publication.PublicationDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            Append(canonical, publication.Series ?? string.Empty);
            Append(canonical, publication.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            foreach (string language in publication.Languages) Append(canonical, language);
            Append(canonical, publication.HasCover ? "1" : "0");
            foreach (BookFormat format in book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal))
            {
                Append(canonical, format.Format); Append(canonical, format.ExpectedRelativePath); Append(canonical, format.FileStatus.ToString());
                Append(canonical, format.Fingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Append(canonical, format.Fingerprint?.Sha256.Value ?? string.Empty);
            }
        }

        foreach (ExactBinaryDuplicateGroup binary in binaryGroups.OrderBy(value => value.Id.Value, StringComparer.Ordinal))
        {
            if (!binary.Members.Any(value => group.Members.Contains(value.BookId))) continue;
            Append(canonical, binary.Id.Value);
            foreach (ExactBinaryDuplicateMember member in binary.Members.OrderBy(value => value.BookId.Value).ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal))
            { Append(canonical, member.BookId.Value.ToString(CultureInfo.InvariantCulture)); Append(canonical, member.Format); Append(canonical, member.ExpectedRelativePath); }
        }

        foreach (FormatAssessment assessment in assessments.Where(value => group.Members.Contains(value.CalibreBookId)).OrderBy(value => value.CalibreBookId.Value).ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal))
        {
            Append(canonical, assessment.CalibreBookId.Value.ToString(CultureInfo.InvariantCulture)); Append(canonical, assessment.ExpectedRelativePath); Append(canonical, assessment.Status.ToString());
            Append(canonical, assessment.ObservedFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Append(canonical, assessment.ObservedFingerprint?.Sha256.Value ?? string.Empty);
            Append(canonical, assessment.Score?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty); Append(canonical, assessment.AnalyzerVersion.Value); Append(canonical, assessment.ScoringModelVersion.Value);
            foreach (AssessmentFinding finding in assessment.Findings.Where(value => DecisiveEpubRules.Contains(value.RuleId))) { Append(canonical, finding.RuleId); Append(canonical, finding.ScoreAdjustment.ToString(CultureInfo.InvariantCulture)); Append(canonical, finding.Severity.ToString()); }
        }

        foreach (LibraryFinding finding in findings
                     .Where(value => value.BookId is not null && group.Members.Contains(value.BookId.Value))
                     .OrderBy(value => value.BookId!.Value.Value)
                     .ThenBy(value => value.Format, StringComparer.Ordinal)
                     .ThenBy(value => value.RelativePath, StringComparer.Ordinal)
                     .ThenBy(value => value.Code, StringComparer.Ordinal)
                     .ThenBy(value => value.Severity))
        {
            Append(canonical, finding.BookId!.Value.Value.ToString(CultureInfo.InvariantCulture));
            Append(canonical, finding.Code);
            Append(canonical, finding.Severity.ToString());
            Append(canonical, finding.Format ?? string.Empty);
            Append(canonical, finding.RelativePath ?? string.Empty);
            foreach ((string key, string value) in finding.Evidence.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                Append(canonical, key);
                Append(canonical, value);
            }
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new($"{InputPolicyVersion}:{Convert.ToHexString(digest).ToLowerInvariant()}");
    }

    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');

    private static HashSet<string> StrongIdentifierValues(CalibreBook book, string type) => book.Identifiers
        .Where(value => CanonicalIdentifierType(value.Type) == type)
        .Select(value => NormalizeStrongIdentifier(value.Type, value.Value))
        .Where(value => value is not null)
        .Cast<string>()
        .ToHashSet(StringComparer.Ordinal);

    private static string? NormalizeStrongIdentifier(string type, string value)
    {
        string canonicalType = CanonicalIdentifierType(type);
        string trimmed = value.Trim();
        return canonicalType switch
        {
            "ISBN" => NormalizeIsbn(trimmed),
            "DOI" => NormalizeDoi(trimmed),
            "ASIN" => NormalizeAsin(trimmed),
            "OCLC" => NormalizeOclc(trimmed),
            _ => null,
        };
    }

    private static string CanonicalIdentifierType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "ISBN" or "ISBN10" or "ISBN13" => "ISBN",
        "DOI" => "DOI",
        "ASIN" or "AMAZON" => "ASIN",
        "OCLC" or "WORLD-CAT" or "WORLDCAT" => "OCLC",
        _ => string.Empty,
    };

    private static string? NormalizeIsbn(string value)
    {
        string isbn = new(value.Where(character => char.IsDigit(character) || character is 'X' or 'x').ToArray());
        isbn = isbn.ToUpperInvariant();
        if (isbn.Length == 10)
        {
            int sum = 0;
            for (int index = 0; index < 10; index++)
            {
                if (index < 9 && !char.IsDigit(isbn[index])) return null;
                sum += (10 - index) * (isbn[index] == 'X' ? 10 : isbn[index] - '0');
            }
            return sum % 11 == 0 ? isbn : null;
        }
        if (isbn.Length == 13 && isbn.All(char.IsDigit))
        {
            int sum = 0;
            for (int index = 0; index < 12; index++) sum += (isbn[index] - '0') * (index % 2 == 0 ? 1 : 3);
            return (10 - sum % 10) % 10 == isbn[12] - '0' ? isbn : null;
        }
        return null;
    }

    private static string? NormalizeDoi(string value)
    {
        string doi = value.Trim();
        foreach (string prefix in DoiPrefixes)
        {
            if (doi.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { doi = doi[prefix.Length..].Trim(); break; }
        }
        doi = doi.ToUpperInvariant();
        return doi.StartsWith("10.", StringComparison.Ordinal) && doi.Contains('/') && !doi.Any(char.IsWhiteSpace) ? doi : null;
    }

    private static string? NormalizeAsin(string value)
    {
        string asin = value.Trim().ToUpperInvariant();
        return asin.Length == 10 && asin.All(char.IsLetterOrDigit) ? asin : null;
    }

    private static string? NormalizeOclc(string value)
    {
        string oclc = value.Trim().ToUpperInvariant();
        foreach (string prefix in new[] { "OCLC", "OCM", "OCN", "ON" }) if (oclc.StartsWith(prefix, StringComparison.Ordinal)) { oclc = oclc[prefix.Length..].TrimStart(':', ' '); break; }
        return oclc.Length > 0 && oclc.All(char.IsDigit) ? oclc.TrimStart('0') is { Length: > 0 } normalized ? normalized : "0" : null;
    }

    private static string? LanguageSignature(IReadOnlyList<string> languages)
    {
        string[] values = languages.Select(NormalizeText).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? null : string.Join('|', values);
    }

    private static string? SeriesSignature(BookPublicationMetadata metadata)
        => NormalizeOptional(metadata.Series);

    private static string? EditionSignature(string title)
    {
        string normalized = NormalizeText(title);
        string[] markers = EditionMarkers
            .Where(marker => normalized.Contains(marker, StringComparison.Ordinal))
            .ToArray();
        System.Text.RegularExpressions.Match edition = System.Text.RegularExpressions.Regex.Match(normalized, @"\b(?:\d+(?:ST|ND|RD|TH)|FIRST|SECOND|THIRD|FOURTH) EDITION\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return markers.Length == 0 && !edition.Success ? null : string.Join('|', markers.Append(edition.Success ? edition.Value : string.Empty).Where(value => value.Length > 0).Order(StringComparer.Ordinal));
    }

    private static bool IsUsable(string value)
    {
        string normalized = NormalizeText(value);
        return normalized.Length > 0 && !Placeholders.Contains(normalized);
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : NormalizeText(value);
    private static string NormalizeText(string value) => value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant().Normalize(NormalizationForm.FormC);
}
