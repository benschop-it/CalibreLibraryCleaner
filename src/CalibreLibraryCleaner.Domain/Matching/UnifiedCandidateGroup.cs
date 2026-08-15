using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

[Flags]
public enum UnifiedCandidateEvidenceSource
{
    None = 0,
    ExactMetadata = 1 << 0,
    Expanded = 1 << 1,
    Identifier = 1 << 2,
    Title = 1 << 3,
    Author = 1 << 4,
    Series = 1 << 5,
    Language = 1 << 6,
    Binary = 1 << 7,
    Content = 1 << 8,
}

public enum UnifiedCandidateClassification
{
    ToBeReviewed,
    CleanupEligible,
}

public sealed record UnifiedCandidatePolicyVersion
{
    public static UnifiedCandidatePolicyVersion V1 { get; } = new("unified-candidates/1.0.0");
    public static UnifiedCandidatePolicyVersion Current => V1;

    public UnifiedCandidatePolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct UnifiedCandidateGroupId
{
    public UnifiedCandidateGroupId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record UnifiedCandidateReviewFinding
{
    public UnifiedCandidateReviewFinding(string code, IEnumerable<CalibreBookId> relatedBookIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(relatedBookIds);
        CalibreBookId[] related = relatedBookIds.Distinct().OrderBy(value => value.Value).ToArray();
        if (code.Length > 128 || related.Length == 0 || related.Length > 256)
            throw new ArgumentException("A unified candidate review finding is invalid.");
        Code = code.Trim();
        RelatedBookIds = new ReadOnlyCollection<CalibreBookId>(related);
    }

    public string Code { get; }
    public IReadOnlyList<CalibreBookId> RelatedBookIds { get; }
}

public sealed record UnifiedCandidateGroup
{
    public UnifiedCandidateGroup(
        UnifiedCandidateGroupId id,
        IEnumerable<CalibreBookId> members,
        CalibreBookId generatedKeeperBookId,
        string language,
        UnifiedCandidateEvidenceSource evidenceSources,
        IEnumerable<CandidateEvidence> evidence,
        IEnumerable<CandidateContradiction>? contradictions,
        ContentComparisonSummary contentComparison,
        UnifiedCandidateClassification classification,
        IEnumerable<ExactMetadataDuplicateGroupId>? exactMetadataGroupIds,
        IEnumerable<WorkLanguageCandidateGroupId>? expandedGroupIds,
        IEnumerable<UnifiedCandidateReviewFinding>? reviewFindings,
        UnifiedCandidatePolicyVersion policyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(contentComparison);
        ArgumentNullException.ThrowIfNull(policyVersion);
        CalibreBookId[] orderedMembers = members.Distinct().OrderBy(value => value.Value).ToArray();
        CandidateEvidence[] orderedEvidence = evidence.Distinct()
            .OrderByDescending(value => value.Strength)
            .ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
        CandidateContradiction[] orderedContradictions = (contradictions ?? []).Distinct()
            .OrderBy(value => value.Code, StringComparer.Ordinal).ToArray();
        ExactMetadataDuplicateGroupId[] metadataIds = (exactMetadataGroupIds ?? []).Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        WorkLanguageCandidateGroupId[] expandedIds = (expandedGroupIds ?? []).Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        UnifiedCandidateReviewFinding[] findings = (reviewFindings ?? []).Distinct()
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.RelatedBookIds[0].Value).ToArray();
        string normalizedLanguage = language.Trim().ToLowerInvariant();
        if (orderedMembers.Length < 2
            || !orderedMembers.Contains(generatedKeeperBookId)
            || evidenceSources == UnifiedCandidateEvidenceSource.None
            || orderedEvidence.Length == 0
            || metadataIds.Length + expandedIds.Length == 0
            || !Enum.IsDefined(classification))
            throw new ArgumentException("A unified candidate group is invalid.");
        UnifiedCandidateGroupId expectedId = CreateId(policyVersion, orderedMembers);
        if (id != expectedId) throw new ArgumentException("The unified candidate group ID is not canonical.", nameof(id));
        if (classification == UnifiedCandidateClassification.CleanupEligible
            && (findings.Length > 0 || orderedContradictions.Length > 0
                || !evidenceSources.HasFlag(UnifiedCandidateEvidenceSource.Expanded)
                || !evidenceSources.HasFlag(UnifiedCandidateEvidenceSource.Content)))
            throw new ArgumentException("Cleanup-eligible unified groups require uncontradicted Expanded content evidence.");

        Id = id;
        Members = new ReadOnlyCollection<CalibreBookId>(orderedMembers);
        GeneratedKeeperBookId = generatedKeeperBookId;
        Language = normalizedLanguage;
        EvidenceSources = evidenceSources;
        Evidence = new ReadOnlyCollection<CandidateEvidence>(orderedEvidence);
        Contradictions = new ReadOnlyCollection<CandidateContradiction>(orderedContradictions);
        ContentComparison = contentComparison.Validate();
        Classification = classification;
        ExactMetadataGroupIds = new ReadOnlyCollection<ExactMetadataDuplicateGroupId>(metadataIds);
        ExpandedGroupIds = new ReadOnlyCollection<WorkLanguageCandidateGroupId>(expandedIds);
        ReviewFindings = new ReadOnlyCollection<UnifiedCandidateReviewFinding>(findings);
        PolicyVersion = policyVersion;
    }

    public UnifiedCandidateGroupId Id { get; }
    public IReadOnlyList<CalibreBookId> Members { get; }
    public CalibreBookId GeneratedKeeperBookId { get; }
    public string Language { get; }
    public UnifiedCandidateEvidenceSource EvidenceSources { get; }
    public IReadOnlyList<CandidateEvidence> Evidence { get; }
    public IReadOnlyList<CandidateContradiction> Contradictions { get; }
    public ContentComparisonSummary ContentComparison { get; }
    public UnifiedCandidateClassification Classification { get; }
    public IReadOnlyList<ExactMetadataDuplicateGroupId> ExactMetadataGroupIds { get; }
    public IReadOnlyList<WorkLanguageCandidateGroupId> ExpandedGroupIds { get; }
    public IReadOnlyList<UnifiedCandidateReviewFinding> ReviewFindings { get; }
    public UnifiedCandidatePolicyVersion PolicyVersion { get; }

    public static UnifiedCandidateGroupId CreateId(
        UnifiedCandidatePolicyVersion policyVersion,
        IEnumerable<CalibreBookId> members)
    {
        ArgumentNullException.ThrowIfNull(policyVersion);
        ArgumentNullException.ThrowIfNull(members);
        StringBuilder canonical = new();
        Append(canonical, policyVersion.Value);
        foreach (CalibreBookId member in members.Distinct().OrderBy(value => value.Value))
            Append(canonical, member.Value.ToString(CultureInfo.InvariantCulture));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return new($"unified-candidate:v1:{digest}");
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('|');
}

public static class UnifiedCandidateMergePolicy
{
    private static readonly HashSet<string> DecisiveContradictions = new(StringComparer.Ordinal)
    {
        "MATCH.LANGUAGE.CONFLICT",
        "MATCH.SERIES_INDEX.CONFLICT",
        "MATCH.CONTENT.DIFFERENT",
    };
    public static IReadOnlyList<UnifiedCandidateGroup> Merge(
        IEnumerable<ExactMetadataDuplicateGroup> exactMetadataGroups,
        IEnumerable<WorkLanguageCandidateGroup> expandedGroups,
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactMetadataGroups);
        ArgumentNullException.ThrowIfNull(expandedGroups);
        ArgumentNullException.ThrowIfNull(books);
        Dictionary<CalibreBookId, CalibreBook> booksById = books.ToDictionary(value => value.Id);
        WorkLanguageCandidateGroup[] expanded = expandedGroups
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray();
        if (expanded.SelectMany(value => value.Members).Any(value => !booksById.ContainsKey(value))
            || expanded.SelectMany(value => value.Members).GroupBy(value => value).Any(value => value.Count() > 1))
            throw new ArgumentException("Expanded groups must be disjoint and reference current books.", nameof(expandedGroups));
        ExactMetadataDuplicateGroup[] metadata = exactMetadataGroups
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray();
        if (metadata.SelectMany(value => value.Members).Any(value => !booksById.ContainsKey(value)))
            throw new ArgumentException("Metadata groups must reference current books.", nameof(exactMetadataGroups));

        List<Component> components = expanded.Select(Component.FromExpanded).ToList();
        Dictionary<CalibreBookId, Component> assigned = components
            .SelectMany(component => component.Members.Select(member => (member, component)))
            .ToDictionary(value => value.member, value => value.component);
        foreach (ExactMetadataDuplicateGroup group in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Component[] intersections = group.Members.Where(assigned.ContainsKey)
                .Select(value => assigned[value]).Distinct()
                .OrderBy(value => value.SortKey, StringComparer.Ordinal).ToArray();
            CalibreBookId[] unassigned = group.Members.Where(value => !assigned.ContainsKey(value))
                .OrderBy(value => value.Value).ToArray();
            if (intersections.Length == 0)
            {
                Component created = Component.FromMetadata(group);
                components.Add(created);
                foreach (CalibreBookId member in created.Members) assigned[member] = created;
                continue;
            }

            if (intersections.Length == 1)
            {
                Component component = intersections[0];
                component.MetadataGroupIds.Add(group.Id);
                component.AddExactMetadataEvidence();
                AttachCompatible(component, unassigned, booksById, assigned);
                CalibreBookId[] rejected = unassigned.Where(value => !assigned.ContainsKey(value)).ToArray();
                UnifiedCandidateReviewFinding? memberFinding = rejected.Length == 0
                    ? null
                    : new("UNIFIED.METADATA_MEMBER_CONTRADICTED", group.Members);
                if (memberFinding is not null) component.Findings.Add(memberFinding);
                AddRejectedMetadataComponent(group, rejected, components, assigned, memberFinding);
                continue;
            }

            HashSet<CalibreBookId> proposed = intersections.SelectMany(value => value.Members)
                .Concat(unassigned).ToHashSet();
            if (ComponentsCompatible(proposed, intersections, booksById))
            {
                Component merged = Component.Merge(intersections, group, proposed);
                components.RemoveAll(intersections.Contains);
                components.Add(merged);
                foreach (CalibreBookId member in proposed) assigned[member] = merged;
                continue;
            }

            UnifiedCandidateReviewFinding finding = new(
                "UNIFIED.METADATA_OVERLAP_CONTRADICTED",
                group.Members);
            foreach (Component component in intersections)
            {
                component.MetadataGroupIds.Add(group.Id);
                component.AddExactMetadataEvidence();
                component.Findings.Add(finding);
            }
            foreach (CalibreBookId member in unassigned)
            {
                Component? target = intersections.FirstOrDefault(value =>
                    ComponentCompatibleWithMember(value, member, booksById));
                if (target is null) continue;
                target.Members.Add(member);
                target.HasMetadataOnlyAttachment = true;
                assigned[member] = target;
            }
            AddRejectedMetadataComponent(group, unassigned, components, assigned, finding);
        }

        foreach (ExactMetadataDuplicateGroup group in metadata)
        {
            Component[] touched = group.Members.Where(assigned.ContainsKey)
                .Select(value => assigned[value]).Distinct().ToArray();
            CalibreBookId[] unassigned = group.Members.Where(value => !assigned.ContainsKey(value)).ToArray();
            if (unassigned.Length > 0)
                AddFinding(touched, "UNIFIED.METADATA_MEMBER_CONTRADICTED", group.Members);
            if (touched.Length > 1)
                AddFinding(touched, "UNIFIED.METADATA_OVERLAP_CONTRADICTED", group.Members);
        }

        UnifiedCandidatePolicyVersion version = UnifiedCandidatePolicyVersion.Current;
        List<UnifiedCandidateGroup> results = [];
        foreach (Component component in components.Where(value => value.Members.Count >= 2)
                     .OrderBy(value => value.Members.Min(member => member.Value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBook[] memberBooks = component.Members.Select(value => booksById[value]).ToArray();
            CalibreBookId keeper = ExpandedCandidateRetentionPolicy.SelectKeeper(
                memberBooks, epubAssessments, pdfAssessments);
            bool cleanupEligible = component.ExpandedEligibility.All(value =>
                    value == WorkLanguageCleanupEligibility.ExplicitKeeperCleanup)
                && component.ExpandedEligibility.Count > 0
                && !component.HasMetadataOnlyAttachment
                && component.Findings.Count == 0
                && component.Contradictions.Count == 0
                && component.EvidenceSources.HasFlag(UnifiedCandidateEvidenceSource.Content);
            CalibreBookId[] members = component.Members.OrderBy(value => value.Value).ToArray();
            results.Add(new(
                UnifiedCandidateGroup.CreateId(version, members),
                members,
                keeper,
                ResolveLanguage(memberBooks),
                component.EvidenceSources,
                component.Evidence,
                component.Contradictions,
                component.Content,
                cleanupEligible
                    ? UnifiedCandidateClassification.CleanupEligible
                    : UnifiedCandidateClassification.ToBeReviewed,
                component.MetadataGroupIds,
                component.ExpandedGroupIds,
                component.Findings,
                version));
        }
        if (results.SelectMany(value => value.Members).GroupBy(value => value).Any(value => value.Count() > 1))
            throw new InvalidOperationException("Unified candidate groups are not disjoint.");
        return new ReadOnlyCollection<UnifiedCandidateGroup>(results.ToArray());
    }

    private static void AttachCompatible(
        Component component,
        IEnumerable<CalibreBookId> candidates,
        Dictionary<CalibreBookId, CalibreBook> books,
        IDictionary<CalibreBookId, Component> assigned)
    {
        foreach (CalibreBookId member in candidates)
        {
            if (!ComponentCompatibleWithMember(component, member, books)) continue;
            component.Members.Add(member);
            component.HasMetadataOnlyAttachment = true;
            assigned[member] = component;
        }
    }

    private static void AddRejectedMetadataComponent(
        ExactMetadataDuplicateGroup group,
        IEnumerable<CalibreBookId> candidates,
        ICollection<Component> components,
        IDictionary<CalibreBookId, Component> assigned,
        UnifiedCandidateReviewFinding? finding = null)
    {
        CalibreBookId[] rejected = candidates.Where(value => !assigned.ContainsKey(value)).ToArray();
        if (rejected.Length < 2) return;
        Component component = Component.FromMetadata(group, rejected);
        if (finding is not null) component.Findings.Add(finding);
        components.Add(component);
        foreach (CalibreBookId member in rejected) assigned[member] = component;
    }

    private static void AddFinding(
        IEnumerable<Component> components,
        string code,
        IEnumerable<CalibreBookId> relatedBookIds)
    {
        UnifiedCandidateReviewFinding finding = new(code, relatedBookIds);
        foreach (Component component in components)
        {
            if (component.Findings.Any(value => value.Code == code
                    && value.RelatedBookIds.SequenceEqual(finding.RelatedBookIds)))
                continue;
            component.Findings.Add(finding);
        }
    }

    private static bool ComponentsCompatible(
        HashSet<CalibreBookId> members,
        Component[] components,
        Dictionary<CalibreBookId, CalibreBook> books) =>
        components.SelectMany(value => value.Contradictions).All(value => !DecisiveContradictions.Contains(value.Code))
        && components.All(value => value.Content.DifferentPairCount == 0)
        && CompleteBooksCompatible(members.Select(value => books[value]).ToArray());

    private static bool ComponentCompatibleWithMember(
        Component component,
        CalibreBookId member,
        Dictionary<CalibreBookId, CalibreBook> books) =>
        component.Contradictions.All(value => !DecisiveContradictions.Contains(value.Code))
        && component.Content.DifferentPairCount == 0
        && CompleteBooksCompatible(component.Members.Append(member).Select(value => books[value]).ToArray());

    private static bool CompleteBooksCompatible(CalibreBook[] books)
    {
        string[] languages = books.Select(KnownLanguage).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        if (languages.Length > 1) return false;
        for (int first = 0; first < books.Length; first++)
            for (int second = first + 1; second < books.Length; second++)
            {
                CalibreBook left = books[first];
                CalibreBook right = books[second];
                if (!CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(
                        CandidateMetadataNormalizer.AuthorKeys(left.Authors.Select(value => value.Name)),
                        CandidateMetadataNormalizer.AuthorKeys(right.Authors.Select(value => value.Name))))
                    return false;
                string? leftSeries = CandidateMetadataNormalizer.NormalizeSeries(left.PublicationMetadata.Series);
                string? rightSeries = CandidateMetadataNormalizer.NormalizeSeries(right.PublicationMetadata.Series);
                if (leftSeries is not null && leftSeries == rightSeries
                    && left.PublicationMetadata.SeriesIndex is not null
                    && right.PublicationMetadata.SeriesIndex is not null
                    && left.PublicationMetadata.SeriesIndex != right.PublicationMetadata.SeriesIndex)
                    return false;
                if (StrongIdentifiersConflict(left, right) || EditionMarkersConflict(left.Title, right.Title))
                    return false;
            }
        return true;
    }

    private static bool StrongIdentifiersConflict(CalibreBook left, CalibreBook right)
    {
        Dictionary<string, HashSet<string>> first = StrongIdentifiers(left);
        Dictionary<string, HashSet<string>> second = StrongIdentifiers(right);
        return first.Keys.Intersect(second.Keys, StringComparer.Ordinal).Any(type =>
            !first[type].Overlaps(second[type]));
    }

    private static Dictionary<string, HashSet<string>> StrongIdentifiers(CalibreBook book) => book.Identifiers
        .Select(value => CandidateMetadataNormalizer.NormalizeStrongIdentifier(value.Type, value.Value))
        .Where(value => value is not null)
        .Select(value => value!.Split(':', 2))
        .GroupBy(value => value[0], StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(value => value[1]).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static bool EditionMarkersConflict(string first, string second)
    {
        string[] left = CandidateMetadataNormalizer.TitleTokens(first)
            .Where(CandidateMetadataNormalizer.IsEditionMarker)
            .Order(StringComparer.Ordinal).ToArray();
        string[] right = CandidateMetadataNormalizer.TitleTokens(second)
            .Where(CandidateMetadataNormalizer.IsEditionMarker)
            .Order(StringComparer.Ordinal).ToArray();
        return !left.SequenceEqual(right);
    }

    private static string ResolveLanguage(IEnumerable<CalibreBook> books)
    {
        string[] known = books.Select(KnownLanguage).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        return known.Length == 1 ? known[0] : "und";
    }

    private static string? KnownLanguage(CalibreBook book)
    {
        string[] values = book.PublicationMetadata.Languages
            .Select(CandidateMetadataNormalizer.NormalizeLanguage)
            .Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static UnifiedCandidateEvidenceSource EvidenceSources(IEnumerable<CandidateEvidence> evidence)
    {
        UnifiedCandidateEvidenceSource result = UnifiedCandidateEvidenceSource.Expanded;
        foreach (CandidateEvidence value in evidence)
        {
            if (value.Code.Contains("IDENTIFIER", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Identifier;
            if (value.Code.Contains("TITLE", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Title;
            if (value.Code.Contains("AUTHOR", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Author;
            if (value.Code.Contains("SERIES", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Series;
            if (value.Code.Contains("LANGUAGE", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Language;
            if (value.Code.Contains("BINARY", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Binary;
            if (value.Code.Contains("CONTENT", StringComparison.Ordinal)) result |= UnifiedCandidateEvidenceSource.Content;
        }
        return result;
    }

    private sealed class Component
    {
        private Component(IEnumerable<CalibreBookId> members) => Members = members.ToHashSet();

        public HashSet<CalibreBookId> Members { get; }
        public HashSet<ExactMetadataDuplicateGroupId> MetadataGroupIds { get; } = [];
        public HashSet<WorkLanguageCandidateGroupId> ExpandedGroupIds { get; } = [];
        public HashSet<CandidateEvidence> Evidence { get; } = [];
        public HashSet<CandidateContradiction> Contradictions { get; } = [];
        public HashSet<UnifiedCandidateReviewFinding> Findings { get; } = [];
        public HashSet<WorkLanguageCleanupEligibility> ExpandedEligibility { get; } = [];
        public UnifiedCandidateEvidenceSource EvidenceSources { get; private set; }
        public ContentComparisonSummary Content { get; private set; } = new(0, 0, 0, 0, 0, 0);
        public bool HasMetadataOnlyAttachment { get; set; }
        public string SortKey => ExpandedGroupIds.Select(value => value.Value)
            .Concat(MetadataGroupIds.Select(value => value.Value)).Order(StringComparer.Ordinal).First();

        public static Component FromExpanded(WorkLanguageCandidateGroup group)
        {
            Component component = new(group.Members)
            {
                EvidenceSources = UnifiedCandidateMergePolicy.EvidenceSources(group.Evidence),
                Content = group.ContentComparison,
            };
            component.ExpandedGroupIds.Add(group.Id);
            component.Evidence.UnionWith(group.Evidence);
            component.Contradictions.UnionWith(group.Contradictions);
            component.ExpandedEligibility.Add(group.CleanupEligibility);
            return component;
        }

        public static Component FromMetadata(
            ExactMetadataDuplicateGroup group,
            IEnumerable<CalibreBookId>? members = null)
        {
            Component component = new(members ?? group.Members);
            component.MetadataGroupIds.Add(group.Id);
            component.AddExactMetadataEvidence();
            return component;
        }

        public static Component Merge(
            IEnumerable<Component> values,
            ExactMetadataDuplicateGroup metadata,
            IEnumerable<CalibreBookId> members)
        {
            Component[] sourceComponents = values.ToArray();
            Component merged = new(members);
            foreach (Component value in sourceComponents)
            {
                merged.MetadataGroupIds.UnionWith(value.MetadataGroupIds);
                merged.ExpandedGroupIds.UnionWith(value.ExpandedGroupIds);
                merged.Evidence.UnionWith(value.Evidence);
                merged.Contradictions.UnionWith(value.Contradictions);
                merged.Findings.UnionWith(value.Findings);
                merged.ExpandedEligibility.UnionWith(value.ExpandedEligibility);
                merged.EvidenceSources |= value.EvidenceSources;
                merged.Content = Add(merged.Content, value.Content);
                merged.HasMetadataOnlyAttachment |= value.HasMetadataOnlyAttachment;
            }
            merged.MetadataGroupIds.Add(metadata.Id);
            merged.AddExactMetadataEvidence();
            merged.HasMetadataOnlyAttachment |= sourceComponents.Length > 1;
            merged.HasMetadataOnlyAttachment |= metadata.Members.Any(member =>
                sourceComponents.All(value => !value.Members.Contains(member)));
            return merged;
        }

        public void AddExactMetadataEvidence()
        {
            EvidenceSources |= UnifiedCandidateEvidenceSource.ExactMetadata
                | UnifiedCandidateEvidenceSource.Title
                | UnifiedCandidateEvidenceSource.Author;
            Evidence.Add(new("MATCH.METADATA.EXACT_TITLE_AUTHOR", CandidateEvidenceStrength.Strong));
        }

        private static ContentComparisonSummary Add(
            ContentComparisonSummary first,
            ContentComparisonSummary second) => new(
            checked(first.ComparedPairCount + second.ComparedPairCount),
            checked(first.EquivalentPairCount + second.EquivalentPairCount),
            checked(first.HighSimilarityPairCount + second.HighSimilarityPairCount),
            checked(first.AmbiguousPairCount + second.AmbiguousPairCount),
            checked(first.DifferentPairCount + second.DifferentPairCount),
            checked(first.UnavailablePairCount + second.UnavailablePairCount));
    }
}
