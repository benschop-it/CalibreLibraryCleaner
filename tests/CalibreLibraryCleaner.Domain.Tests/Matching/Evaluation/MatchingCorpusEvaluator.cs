using System.Diagnostics;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public static class MatchingCorpusEvaluator
{
    public const string EvaluationSchemaVersion = "matching-evaluation/1.0";
    public const string KeeperPolicyIdentity = "expanded-candidate-keeper/1.0.0";

    public static MatchingEvaluationReport Evaluate(MatchingCorpus calibration, MatchingCorpus holdout)
    {
        MatchingCorpusLoader.ValidateSplits(calibration, holdout);
        if (!string.Equals(calibration.CorpusVersion, holdout.CorpusVersion, StringComparison.Ordinal))
            throw new MatchingCorpusValidationException("CORPUS.VERSION_MIXED");

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ScenarioMeasurement[] measurements = calibration.Scenarios
            .Select(value => EvaluateScenario(value, calibration.Split))
            .Concat(holdout.Scenarios.Select(value => EvaluateScenario(value, holdout.Split)))
            .OrderBy(value => value.ScenarioId, StringComparer.Ordinal).ToArray();
        stopwatch.Stop();

        MatchingFalseNegative[] falseNegatives = measurements.SelectMany(value => value.FalseNegatives)
            .OrderBy(value => value.ScenarioId, StringComparer.Ordinal)
            .ThenBy(value => value.FirstRecordId, StringComparer.Ordinal)
            .ThenBy(value => value.SecondRecordId, StringComparer.Ordinal).ToArray();
        MatchingFalsePositive[] falsePositives = measurements.SelectMany(value => value.FalsePositives)
            .OrderBy(value => value.ScenarioId, StringComparer.Ordinal)
            .ThenBy(value => value.FirstRecordId, StringComparer.Ordinal)
            .ThenBy(value => value.SecondRecordId, StringComparer.Ordinal).ToArray();
        MatchingKeeperError[] keeperErrors = measurements.SelectMany(value => value.KeeperErrors)
            .OrderBy(value => value.ScenarioId, StringComparer.Ordinal)
            .ThenBy(value => value.WorkKey, StringComparer.Ordinal)
            .ThenBy(value => value.Language, StringComparer.Ordinal).ToArray();

        return new(
            EvaluationSchemaVersion,
            calibration.CorpusVersion,
            MatchingCorpusLoader.CanonicalDigest(calibration),
            MatchingCorpusLoader.CanonicalDigest(holdout),
            new(
                ExactMetadataDuplicateDetector.PolicyVersion.Value,
                MatchingPolicyVersion.Current.Value,
                UnifiedCandidatePolicyVersion.Current.Value,
                KeeperPolicyIdentity),
            Counts(calibration.Scenarios.Concat(holdout.Scenarios)
                .Select(value => value.GenerationTemplateId)
                .Where(value => value is not null).Select(value => value!)),
            MatchingMetricCalculator.Aggregate("Overall", measurements),
            Enum.GetValues<MatchingCorpusSplit>().Select(split =>
                MatchingMetricCalculator.Aggregate(split.ToString(), measurements.Where(value => value.Split == split)))
                .ToArray(),
            measurements.SelectMany(value => value.Tags).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(tag => MatchingMetricCalculator.Aggregate(tag,
                    measurements.Where(value => value.Tags.Contains(tag, StringComparer.Ordinal))))
                .ToArray(),
            falseNegatives,
            falsePositives,
            keeperErrors,
            new(
                stopwatch.ElapsedMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                Environment.Version.ToString()));
    }

    private static ScenarioMeasurement EvaluateScenario(MatchingScenario scenario, MatchingCorpusSplit split)
    {
        CalibreBook[] books = scenario.Records.Select(record => CreateBook(scenario.Id, record)).ToArray();
        Dictionary<CalibreBookId, MatchingRecordFixture> fixtureById = scenario.Records
            .ToDictionary(value => new CalibreBookId(value.CalibreId));
        Dictionary<string, CalibreBookId> idByKey = scenario.Records
            .ToDictionary(value => value.Key, value => new CalibreBookId(value.CalibreId), StringComparer.Ordinal);
        EpubAssessment[] epubAssessments = scenario.Records.Where(value => value.Epub is not null)
            .Select(value => CreateEpubAssessment(value, books.Single(book => book.Id.Value == value.CalibreId)))
            .ToArray();

        IReadOnlyList<ExactMetadataDuplicateGroup> exact = ExactMetadataDuplicateDetector.Detect(books);
        IReadOnlyList<BookMatchingProfile> profiles = BookMatchingProfileFactory.Create(books, epubAssessments);
        BookCandidateGenerationResult generation = BookCandidateGenerator.Generate(profiles);
        Dictionary<BookCandidatePairId, MatchingContentOracle> oracleByPair = scenario.ContentComparisons
            .ToDictionary(value => new BookCandidatePairId(idByKey[value.FirstRecordKey], idByKey[value.SecondRecordKey]));
        Dictionary<BookCandidatePairId, CandidateContentComparison> injected = generation.Pairs
            .Where(value => value.NeedsContentEvidence && oracleByPair.ContainsKey(value.Id))
            .ToDictionary(value => value.Id, value => CreateComparison(oracleByPair[value.Id]));
        Dictionary<CalibreBookId, BookMatchingProfile> profileById = profiles.ToDictionary(value => value.BookId);
        Dictionary<CalibreBookId, CalibreBook> bookById = books.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, BibliographicWorkResolution> bibliographic =
            (scenario.BibliographicResolutions ?? []).ToDictionary(
                value => idByKey[value.RecordKey],
                value => CreateBibliographicResolution(
                    value,
                    bookById[idByKey[value.RecordKey]],
                    profileById[idByKey[value.RecordKey]]));
        IReadOnlyList<BookCandidatePair> enrichedPairs = BibliographicPairEvidencePolicy.Enrich(
            generation.Pairs, bibliographic);
        IReadOnlyList<BookCandidateDecision> decisions = BookCandidateDecisionPolicy.Decide(enrichedPairs, injected);
        IReadOnlyList<WorkLanguageCandidateGroup> expanded = WorkLanguageCandidateClusterer.Cluster(profiles, decisions);
        IReadOnlyList<UnifiedCandidateGroup> unified = UnifiedCandidateMergePolicy.Merge(
            exact, expanded, books, epubAssessments);

        Dictionary<CalibreBookId, UnifiedCandidateGroup> unifiedByMember = unified
            .SelectMany(group => group.Members.Select(member => (member, group)))
            .ToDictionary(value => value.member, value => value.group);
        HashSet<BookCandidatePairId> exactPairs = PairIds(exact.Select(value => value.Members));
        HashSet<BookCandidatePairId> candidatePairs = generation.Pairs.Select(value => value.Id).ToHashSet();
        Dictionary<BookCandidatePairId, BookCandidateDecision> decisionsByPair = decisions
            .ToDictionary(value => value.Pair.Id);
        HashSet<BookCandidatePairId> expandedPairs = PairIds(expanded.Select(value => value.Members));

        int truePositives = 0;
        int falsePositivesCount = 0;
        int falseNegativesCount = 0;
        int trueNegatives = 0;
        int candidateRouteReached = 0;
        int candidateRouteTotal = 0;
        int expandedCandidateReached = 0;
        int expandedCandidateTotal = 0;
        List<MatchingFalseNegative> falseNegatives = [];
        List<MatchingFalsePositive> falsePositives = [];
        BookCandidateGenerationResult? bucketRelaxed = null;
        BookCandidateGenerationResult? fullyRelaxed = null;

        MatchingRecordFixture[] records = scenario.Records.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        for (int firstIndex = 0; firstIndex < records.Length; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1; secondIndex < records.Length; secondIndex++)
            {
                MatchingRecordFixture first = records[firstIndex];
                MatchingRecordFixture second = records[secondIndex];
                BookCandidatePairId pairId = new(idByKey[first.Key], idByKey[second.Key]);
                bool expectedSame = first.WorkKey == second.WorkKey
                    && first.ExpectedLanguage == second.ExpectedLanguage;
                bool predictedSame = unifiedByMember.TryGetValue(pairId.First, out UnifiedCandidateGroup? firstGroup)
                    && unifiedByMember.TryGetValue(pairId.Second, out UnifiedCandidateGroup? secondGroup)
                    && firstGroup.Id == secondGroup.Id;
                if (expectedSame)
                {
                    candidateRouteTotal++;
                    bool exactReached = exactPairs.Contains(pairId);
                    if (exactReached || candidatePairs.Contains(pairId)) candidateRouteReached++;
                    if (!exactReached)
                    {
                        expandedCandidateTotal++;
                        if (candidatePairs.Contains(pairId)) expandedCandidateReached++;
                    }
                    if (predictedSame)
                    {
                        truePositives++;
                    }
                    else
                    {
                        falseNegativesCount++;
                        string category = ClassifyFalseNegative(
                            pairId,
                            profiles,
                            generation,
                            exactPairs,
                            decisionsByPair,
                            expandedPairs,
                            oracleByPair,
                            ref bucketRelaxed,
                            ref fullyRelaxed);
                        falseNegatives.Add(new(scenario.Id, first.Key, second.Key, category));
                    }
                }
                else if (predictedSame)
                {
                    falsePositivesCount++;
                    UnifiedCandidateGroup group = firstGroup!;
                    falsePositives.Add(new(
                        scenario.Id,
                        first.Key,
                        second.Key,
                        group.Id.Value,
                        group.Evidence.Select(value => value.Code).Order(StringComparer.Ordinal).ToArray(),
                        group.Contradictions.Select(value => value.Code).Order(StringComparer.Ordinal).ToArray(),
                        group.ReviewFindings.Select(value => value.Code).Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal).ToArray()));
                }
                else
                {
                    trueNegatives++;
                }
            }
        }

        Dictionary<(string WorkKey, string Language), MatchingRecordFixture[]> expectedAll = scenario.Records
            .GroupBy(value => (value.WorkKey, value.ExpectedLanguage))
            .ToDictionary(group => group.Key, group => group.ToArray());
        Dictionary<(string WorkKey, string Language), MatchingRecordFixture[]> expectedGroups = expectedAll
            .Where(value => value.Value.Length >= 2).ToDictionary();
        int exactComponents = expectedGroups.Count(expected => unified.Any(group =>
            group.Members.ToHashSet().SetEquals(expected.Value.Select(value => idByKey[value.Key]))));
        int overmerged = unified.Count(group => group.Members
            .Select(member => fixtureById[member])
            .Select(value => (value.WorkKey, value.ExpectedLanguage)).Distinct().Take(2).Count() > 1);
        int fragmented = expectedGroups.Count(expected => expected.Value
            .Select(value => PredictedComponent(idByKey[value.Key], unifiedByMember)).Distinct(StringComparer.Ordinal).Count() > 1);
        int crossLanguage = unified.Count(group => group.Members.Select(member => fixtureById[member].ExpectedLanguage)
            .Distinct(StringComparer.Ordinal).Take(2).Count() > 1);
        int correctLanguages = unified.Count(group =>
        {
            string[] languages = group.Members.Select(member => fixtureById[member].ExpectedLanguage)
                .Distinct(StringComparer.Ordinal).ToArray();
            return languages.Length == 1 && string.Equals(languages[0], group.Language, StringComparison.Ordinal);
        });

        Dictionary<(string WorkKey, string Language), MatchingExpectedGroup> keeperTruth = scenario.ExpectedGroups
            .ToDictionary(value => (value.WorkKey, value.Language));
        int correctKeepers = 0;
        int keeperTotal = 0;
        List<MatchingKeeperError> keeperErrors = [];
        foreach (((string workKey, string language), MatchingRecordFixture[] members) in expectedGroups)
        {
            UnifiedCandidateGroup? group = UnifiedGroupContainingAll(members, idByKey, unifiedByMember);
            if (group is null) continue;
            keeperTotal++;
            string actual = fixtureById[group.GeneratedKeeperBookId].Key;
            MatchingExpectedGroup truth = keeperTruth[(workKey, language)];
            if (truth.AcceptableKeeperRecordIds.Contains(actual, StringComparer.Ordinal))
            {
                correctKeepers++;
            }
            else
            {
                keeperErrors.Add(new(
                    scenario.Id,
                    workKey,
                    language,
                    actual,
                    truth.AcceptableKeeperRecordIds.Order(StringComparer.Ordinal).ToArray()));
            }
        }

        IReadOnlyList<MatchingNamedCount> failureCounts = MatchingMetricCalculator.AggregateCounts(
            falseNegatives.Select(value => new MatchingNamedCount(value.Category, 1)));
        return new(
            scenario.Id,
            split,
            scenario.Tags,
            new(truePositives, falsePositivesCount, falseNegativesCount, trueNegatives),
            candidateRouteReached,
            candidateRouteTotal,
            expandedCandidateReached,
            expandedCandidateTotal,
            exactComponents,
            expectedGroups.Count,
            unified.Count,
            overmerged,
            fragmented,
            crossLanguage,
            correctLanguages,
            unified.Count,
            correctKeepers,
            keeperTotal,
            generation.Pairs.Count(value => value.NeedsContentEvidence),
            generation.Pairs.Count(value => value.NeedsContentEvidence && oracleByPair.ContainsKey(value.Id)),
            generation.ProposedDirectedPairCount,
            generation.Pairs.Count,
            generation.RecordsCapped,
            generation.LimitExceeded
                ? 0
                : Math.Max(0, generation.ProposedDirectedPairCount - 2 * generation.Pairs.Count),
            generation.MaximumObservedBucketSize,
            generation.MaximumObservedBucketSize > new BookCandidateGenerationLimits().MaximumOrdinaryBucketSize
                ? 1
                : 0,
            generation.LimitExceeded ? 1 : 0,
            Counts(decisions.Select(value => value.Disposition.ToString())),
            failureCounts,
            Counts(unified.Select(value => value.Classification.ToString())),
            Counts(unified.SelectMany(value => value.ReviewFindings).Select(value => value.Code)),
            falseNegatives,
            falsePositives,
            keeperErrors);
    }

    private static string ClassifyFalseNegative(
        BookCandidatePairId pairId,
        IReadOnlyList<BookMatchingProfile> profiles,
        BookCandidateGenerationResult generation,
        HashSet<BookCandidatePairId> exactPairs,
        Dictionary<BookCandidatePairId, BookCandidateDecision> decisions,
        HashSet<BookCandidatePairId> expandedPairs,
        Dictionary<BookCandidatePairId, MatchingContentOracle> oracles,
        ref BookCandidateGenerationResult? bucketRelaxed,
        ref BookCandidateGenerationResult? fullyRelaxed)
    {
        HashSet<CalibreBookId> profileIds = profiles.Select(value => value.BookId).ToHashSet();
        if (!profileIds.Contains(pairId.First) || !profileIds.Contains(pairId.Second)) return "PROFILE_INELIGIBLE";
        if (exactPairs.Contains(pairId)) return "UNIFIED_MERGE_SPLIT";
        if (generation.LimitExceeded) return "GLOBAL_LIMIT_ABORTED";
        BookCandidatePair? pair = generation.Pairs.SingleOrDefault(value => value.Id == pairId);
        if (pair is null)
        {
            bucketRelaxed ??= BookCandidateGenerator.Generate(profiles,
                new(maximumCandidatesPerRecord: 20, maximumOrdinaryBucketSize: 4096));
            if (bucketRelaxed.Pairs.Any(value => value.Id == pairId)) return "AUTHOR_BUCKET_SUPPRESSED";
            fullyRelaxed ??= BookCandidateGenerator.Generate(profiles,
                new(maximumCandidatesPerRecord: 100, maximumOrdinaryBucketSize: 4096));
            if (fullyRelaxed.Pairs.Any(value => value.Id == pairId)) return "ORDINARY_CAP_DROPPED";
            return "CANDIDATE_NOT_PROPOSED";
        }

        if (!decisions.TryGetValue(pairId, out BookCandidateDecision? decision)) return "UNKNOWN_PIPELINE_GAP";
        if (decision.Disposition == CandidatePairDisposition.Rejected) return "PAIR_REJECTED_CONTRADICTION";
        if (decision.Disposition == CandidatePairDisposition.Weak)
        {
            if (!pair.NeedsContentEvidence) return "CONTENT_NOT_REQUESTED";
            if (!oracles.TryGetValue(pairId, out MatchingContentOracle? oracle)
                || oracle.Classification is "Unavailable" or "Ambiguous")
                return "CONTENT_UNAVAILABLE_OR_WEAK";
            return "PAIR_REMAINED_WEAK";
        }
        if (!expandedPairs.Contains(pairId)) return "COMPONENT_COMPATIBILITY_BLOCKED";
        return "UNIFIED_MERGE_SPLIT";
    }

    private static CalibreBook CreateBook(string scenarioId, MatchingRecordFixture fixture)
    {
        BookFormat[] formats = fixture.Formats.Select(value => CreateFormat(value)).ToArray();
        return new(
            new(fixture.CalibreId),
            fixture.Title,
            fixture.AuthorSort,
            fixture.Authors.Select(value => new BookAuthor(null, value.Name, value.SortName)),
            fixture.Identifiers.Select(value => new BookIdentifier(value.Type, value.Value)),
            formats,
            $"corpus/{scenarioId}/{fixture.Key}",
            new(
                fixture.Publication.Publisher,
                fixture.Publication.PublicationDate is null
                    ? null
                    : DateTimeOffset.Parse(fixture.Publication.PublicationDate,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                fixture.Publication.Series,
                fixture.Publication.SeriesIndex,
                fixture.Publication.Languages,
                fixture.Publication.HasCover));
    }

    private static BookFormat CreateFormat(MatchingFormatFixture fixture)
    {
        if (fixture.Sha256 is null || fixture.SizeInBytes is null)
            return new(fixture.Format, fixture.StoredFileName, fixture.ExpectedRelativePath, FormatFileStatus.Missing);
        FormatFileFingerprint fingerprint = new(fixture.SizeInBytes.Value, new(fixture.Sha256));
        return new(
            fixture.Format,
            fixture.StoredFileName,
            fixture.ExpectedRelativePath,
            FormatFileStatus.Present,
            fingerprint,
            new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
    }

    private static EpubAssessment CreateEpubAssessment(MatchingRecordFixture fixture, CalibreBook book)
    {
        MatchingEpubFixture epub = fixture.Epub!;
        AssessmentStatus status = Enum.Parse<AssessmentStatus>(epub.Status);
        AssessmentFinding finding = status switch
        {
            AssessmentStatus.Completed => new(
                "CORPUS.SCORE", FindingSeverity.Positive, epub.Score!.Value, "Synthetic corpus score."),
            AssessmentStatus.Disqualified => new(
                "CORPUS.DISQUALIFIED", FindingSeverity.Disqualifying, 0, "Synthetic corpus disqualification."),
            _ => new("CORPUS.UNASSESSED", FindingSeverity.Information, 0, "Synthetic corpus assessment."),
        };
        FormatFileFingerprint? fingerprint = book.Formats
            .SingleOrDefault(value => string.Equals(value.ExpectedRelativePath, epub.ExpectedRelativePath,
                StringComparison.Ordinal))?.Fingerprint;
        return new(
            book.Id,
            "EPUB",
            epub.ExpectedRelativePath,
            fingerprint,
            status,
            epub.Score is null ? null : new QualityScore(epub.Score.Value),
            new(epub.AnalyzerVersion),
            new(epub.ScoringModelVersion),
            new(
                opened: true,
                packageParsed: true,
                embeddedTitle: epub.EmbeddedTitle,
                authors: epub.Authors,
                languages: epub.Languages,
                strongIdentifiers: epub.StrongIdentifiers,
                coverage: EpubAssessmentCoverage.Full,
                availableFacets: EpubAssessmentFacet.Metadata),
            [finding]);
    }

    private static CandidateContentComparison CreateComparison(MatchingContentOracle oracle) => new(
        Enum.Parse<ContentSimilarityClassification>(oracle.Classification),
        oracle.ForwardStrictMatches,
        oracle.ReverseStrictMatches,
        oracle.ForwardRelaxedMatches,
        oracle.ReverseRelaxedMatches,
        oracle.MatchedRegionCount,
        oracle.TokenCountRatioPermille,
        oracle.ShingleSimilarityPermille);

    private static BibliographicWorkResolution CreateBibliographicResolution(
        MatchingBibliographicResolution oracle,
        CalibreBook book,
        BookMatchingProfile profile)
    {
        BibliographicProviderIdentity provider = new(oracle.ProviderId, oracle.ProviderVersion);
        BibliographicLookupQuery query = BibliographicLookupQuery.Create(book, profile, provider);
        if (query.Fields != oracle.QueryFields)
            throw new MatchingCorpusValidationException("CORPUS.BIBLIOGRAPHIC_QUERY_FIELDS_MISMATCH");
        return new(
            book.Id,
            provider,
            query.QueryIdentity,
            query.Fields,
            DateTimeOffset.Parse(
                oracle.RetrievedAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            oracle.Status,
            oracle.WorkId,
            oracle.ProblemCode);
    }

    private static HashSet<BookCandidatePairId> PairIds(IEnumerable<IEnumerable<CalibreBookId>> groups)
    {
        HashSet<BookCandidatePairId> result = [];
        foreach (CalibreBookId[] members in groups.Select(value => value.OrderBy(member => member.Value).ToArray()))
            for (int first = 0; first < members.Length; first++)
                for (int second = first + 1; second < members.Length; second++)
                    result.Add(new(members[first], members[second]));
        return result;
    }

    private static string PredictedComponent(
        CalibreBookId member,
        Dictionary<CalibreBookId, UnifiedCandidateGroup> unifiedByMember) =>
        unifiedByMember.TryGetValue(member, out UnifiedCandidateGroup? group)
            ? group.Id.Value
            : $"singleton:{member.Value}";

    private static UnifiedCandidateGroup? UnifiedGroupContainingAll(
        IEnumerable<MatchingRecordFixture> members,
        Dictionary<string, CalibreBookId> idByKey,
        Dictionary<CalibreBookId, UnifiedCandidateGroup> unifiedByMember)
    {
        UnifiedCandidateGroup[] groups = members.Select(value => idByKey[value.Key])
            .Where(unifiedByMember.ContainsKey).Select(value => unifiedByMember[value]).Distinct().ToArray();
        return groups.Length == 1 ? groups[0] : null;
    }

    private static MatchingNamedCount[] Counts(IEnumerable<string> names) =>
        names.GroupBy(value => value, StringComparer.Ordinal)
            .Select(group => new MatchingNamedCount(group.Key, group.Count()))
            .OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
}
