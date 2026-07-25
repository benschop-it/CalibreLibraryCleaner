using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Application.Tests.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

public sealed class ExecuteApprovedRecoveryPlanUseCaseTests
{
    [Fact]
    public async Task VerifiedBackupAndConstructiveRecoveryAlwaysPrecedeDestructiveRecovery()
    {
        Harness harness = new(RecoveryCommandBehavior.Success);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.Recovered,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        result.SemanticPreStateRestored.Should().BeTrue();
        result.RecordIdMappings.Should().ContainSingle().Which.Should().Match<RecoveryRecordIdMapping>(
            value => value.OriginalRecordId == new CalibreBookId(2)
                && value.CleanupTargetRecordId == new CalibreBookId(1)
                && value.RecoveredRecordId == new CalibreBookId(42)
                && value.RestoredFormats.Contains("PDF"));
        int backup = harness.Trace.IndexOf("backup:verified");
        int firstConstructive = harness.Trace.FindIndex(value =>
            value.StartsWith("construct:", StringComparison.Ordinal));
        int lastConstructive = harness.Trace.FindLastIndex(value =>
            value.StartsWith("construct:", StringComparison.Ordinal));
        int firstDestructive = harness.Trace.FindIndex(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        backup.Should().BeLessThan(firstConstructive);
        lastConstructive.Should().BeLessThan(firstDestructive);
        harness.Journal.Events.Should().Contain(value =>
            value.Kind == "CurrentStateBackupVerified");
        harness.Journal.Events.Should().Contain(value =>
            value.Kind == "DestructiveRecoveryStarting");
        RecoveryRecordIdMapping finalizedMapping = harness.Journal.Events
            .Should().ContainSingle(value =>
                value.Kind == "RecordIdMappingFinalized").Which
            .RecordIdMapping!;
        finalizedMapping.OriginalRecordId.Should().Be(new CalibreBookId(2));
        finalizedMapping.RecoveredRecordId.Should().Be(new CalibreBookId(42));
        finalizedMapping.RestoredFormats.Should().Contain("PDF");
        finalizedMapping.Identifiers.Should().Contain("isbn:9780306406157");
        harness.Journal.FinalEntryHash.Should().NotBeNull();
    }

    [Fact]
    public async Task CancellationDuringCurrentStateBackupPreventsEveryMutation()
    {
        Harness harness = new(RecoveryCommandBehavior.CancelDuringBackup);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.CancelledBeforeMutation);
        result.MutationStarted.Should().BeFalse();
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("construct:", StringComparison.Ordinal)
            || value.StartsWith("destroy:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessSuccessWithoutSemanticEffectStopsBeforeDestructionAndIsNotRetried()
    {
        Harness harness = new(RecoveryCommandBehavior.SuccessWithoutEffect);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.PartiallyRecovered,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        result.SemanticPreStateRestored.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.CREATED_RECORD_AMBIGUOUS");
        harness.Trace.Count(value => value == "construct:create").Should().Be(1);
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        harness.Journal.Events.Should().ContainSingle(value =>
            value.Kind == "RecoveryStopped"
            && value.OperationId != null
            && value.OperationId.Value.StartsWith(
                "construct:create:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DestructiveCommandFailureStopsRescansAndRequiresManualIntervention()
    {
        Harness harness = new(RecoveryCommandBehavior.DestructiveFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.DestructiveCommand,
            string.Join(" | ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        harness.Trace.Count(value => value == "destroy:format").Should().Be(1);
        harness.Scanner.ScanCountAfterLastCommand.Should().BeGreaterThan(0);
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.DESTRUCTIVE_COMMAND_FAILED");
    }

    [Fact]
    public async Task OperationVerificationJournalFailureCannotAdvanceOrReachDestruction()
    {
        Harness harness = new(
            RecoveryCommandBehavior.OperationVerificationJournalFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.CrashOrIndeterminate);
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
        harness.Journal.Events.Should().NotContain(value =>
            value.Kind == "OperationVerified");
        A.CallTo(() => harness.Resolutions.RecordResolutionAsync(
            A<RecoveryResolutionEntry>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TerminalSummaryFailureCannotBeReportedOrIndexedAsRecovered()
    {
        Harness harness = new(
            RecoveryCommandBehavior.TerminalPersistenceFailure);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.ManualInterventionRequired);
        result.FailureClassification.Should().Be(
            RecoveryFailureClassification.JournalOrStorage);
        result.SemanticPreStateRestored.Should().BeTrue(
            "the semantic scan passed even though durable success proof did not");
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.TERMINAL_SUMMARY_WRITE_FAILED");
        A.CallTo(() => harness.Resolutions.RecordResolutionAsync(
            A<RecoveryResolutionEntry>._, A<string>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CollateralAffectedRecordMetadataChangeStopsBeforeDestruction()
    {
        Harness harness = new(
            RecoveryCommandBehavior.CollateralMetadataMutation);

        RecoveryExecutionResult result = await harness.ExecuteAsync();

        result.State.Should().Be(RecoveryExecutionState.PartiallyRecovered);
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.PRESERVED_METADATA_CHANGED");
        harness.Trace.Should().NotContain(value =>
            value.StartsWith("destroy:", StringComparison.Ordinal));
    }

    private enum RecoveryCommandBehavior
    {
        Success,
        CancelDuringBackup,
        SuccessWithoutEffect,
        DestructiveFailure,
        OperationVerificationJournalFailure,
        TerminalPersistenceFailure,
        CollateralMetadataMutation,
    }

    private sealed class Harness
    {
        private const string Destination = "C:\\synthetic\\recovery-backup";
        private readonly RecoveryPlan _plan;
        private readonly RecoverySourceInspection _source;
        private readonly RecoveryCurrentStateSnapshot _initial;
        private readonly RecoveryCapabilityProfile _profile;
        private readonly CalibreToolDescriptor _tool;
        private readonly ExecuteApprovedRecoveryPlanUseCase _sut;

        public Harness(RecoveryCommandBehavior behavior)
        {
            (_source, _initial, _profile, _tool) = RecoveryTestData.SourceAndCurrent();
            RecoveryPlan valid = RecoveryTestData.PlanFor(
                _source, _initial, _profile);
            _plan = RecoveryPlanLifecyclePolicy.Approve(
                valid, valid.Definition.RequiredWarningCodes,
                ExecutionTestData.Now.AddMinutes(1));
            Trace = [];
            Scanner = new MutableRecoveryScanner(_source, _initial);
            MutableRecoveryGateway gateway = new(
                behavior, _source.CleanupPlan!, Scanner, Trace);
            Journal = new RecordingRecoveryJournal(behavior);

            IRecoverySourceArtifactReader sourceReader =
                A.Fake<IRecoverySourceArtifactReader>();
            A.CallTo(() => sourceReader.ReadAndVerifyAsync(
                    A<string>._, A<CancellationToken>._))
                .Returns(_source);
            IRecoveryStateBackupService backup = new RecordingBackupService(
                behavior == RecoveryCommandBehavior.CancelDuringBackup, Trace);
            IRecoveryJournalStore journals = A.Fake<IRecoveryJournalStore>();
            A.CallTo(() => journals.CreateAsync(
                    A<RecoveryJournalCreateRequest>._, A<CancellationToken>._))
                .Returns(Journal);
            A.CallTo(() => journals.ReconcileAsync(
                    A<string>._, A<string>._, A<CancellationToken>._))
                .Returns(new RecoveryJournalReconciliationResult(false, []));
            IRecoveryHistoryStore history = A.Fake<IRecoveryHistoryStore>();
            A.CallTo(() => history.HasUnresolvedRecoveryAsync(
                    A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
                .Returns(false);
            Resolutions = A.Fake<IRecoveryResolutionStore>();
            ILibraryMutationLease lease = A.Fake<ILibraryMutationLease>();
            A.CallTo(() => lease.TryAcquireAsync(
                    A<LibraryMutationLeaseRequest>._, A<CancellationToken>._))
                .Returns(new LibraryMutationLeaseAcquisition(
                    new HeldRecoveryLease(), []));
            ICalibreToolDiscovery discovery = A.Fake<ICalibreToolDiscovery>();
            A.CallTo(() => discovery.DiscoverAndProbeAsync(
                    A<string>._, A<CancellationToken>._))
                .Returns(new CalibreToolDiscoveryResult(_tool, []));
            ICalibreExecutionProfileProvider profiles =
                A.Fake<ICalibreExecutionProfileProvider>();
            A.CallTo(() => profiles.EvaluateRecoveryProfile(_tool))
                .Returns(_profile);
            IRecoveryIdGenerator ids = A.Fake<IRecoveryIdGenerator>();
            A.CallTo(() => ids.CreateExecutionId()).Returns(
                new RecoveryExecutionId(Guid.Parse(
                    "dddddddd-1111-2222-3333-eeeeeeeeeeee")));
            IDestructiveRecoveryConfirmation destructive =
                A.Fake<IDestructiveRecoveryConfirmation>();
            A.CallTo(() => destructive.ConfirmAsync(
                    A<DestructiveRecoveryConfirmationRequest>._,
                    A<CancellationToken>._)).Returns(true);
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(ExecutionTestData.Now);

            _sut = new(sourceReader, Scanner, new CurrentStateReconciler(),
                backup, journals, history, Resolutions, lease, discovery,
                profiles, gateway, new RecoveryStateVerifier(), ids,
                destructive, clock);
        }

        public List<string> Trace { get; }
        public MutableRecoveryScanner Scanner { get; }
        public RecordingRecoveryJournal Journal { get; }
        public IRecoveryResolutionStore Resolutions { get; }

        public Task<RecoveryExecutionResult> ExecuteAsync()
        {
            RecoveryExecutionConfirmation confirmation = new(_plan.Id,
                _plan.ArtifactRevision, _plan.ContentDigest,
                _plan.Definition.InputIdentity.SourceExecutionId,
                _plan.Definition.InputIdentity.CurrentLibraryUuid,
                _plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
                _plan.Definition.InputIdentity.FullStateFingerprint,
                _plan.Definition.InputIdentity.RecoveryCapabilityProfile,
                Destination,
                ExecuteApprovedRecoveryPlanUseCase.ComputeDestructiveDigest(
                    _plan.Definition.OperationGraph),
                ExecutionTestData.Now, true, true);
            return _sut.ExecuteAsync(new(_plan, _source,
                _initial.Snapshot.Identity.LibraryRoot, Destination, _tool,
                _profile, confirmation, "1.0.0"), null, CancellationToken.None);
        }
    }

    private sealed class MutableRecoveryScanner(
        RecoverySourceInspection source,
        RecoveryCurrentStateSnapshot initial) : IRecoveryCurrentStateScanner
    {
        private readonly HashSet<CalibreBookId> _originalAffected =
            source.CleanupPlan!.Definition.InvolvedRecordIds.ToHashSet();
        private LibrarySnapshot _snapshot = initial.Snapshot;
        private int _lastCommandScanCount;

        public int ScanCountAfterLastCommand => _lastCommandScanCount;

        public Task<RecoveryCurrentStateScanResult> ScanFreshAsync(
            string libraryRoot,
            IReadOnlyCollection<CalibreBookId> affectedRecordIds,
            IProgress<LibraryScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lastCommandScanCount++;
            RecoveryCoverEvidence[] covers = _snapshot.Books.Select(value =>
                new RecoveryCoverEvidence(value.Id,
                    value.PublicationMetadata.HasCover, null, null)).ToArray();
            HashSet<CalibreBookId> affected = [.. _originalAffected];
            affected.UnionWith(affectedRecordIds);
            RecoveryCurrentStateSnapshot value = new(_snapshot, covers,
                RecoverySnapshotFingerprintPolicy.ComputeFull(_snapshot, covers),
                RecoverySnapshotFingerprintPolicy.ComputeAffected(
                    _snapshot, affected, covers),
                RecoverySnapshotFingerprintPolicy.ComputeUnrelated(
                    _snapshot, affected, covers));
            return Task.FromResult(new RecoveryCurrentStateScanResult(value, []));
        }

        public void ReplaceSnapshot(LibrarySnapshot snapshot)
        {
            _snapshot = snapshot;
            _lastCommandScanCount = 0;
        }

        public LibrarySnapshot Snapshot => _snapshot;
    }

    private sealed class MutableRecoveryGateway(
        RecoveryCommandBehavior behavior,
        CleanupPlan sourcePlan,
        MutableRecoveryScanner scanner,
        List<string> trace) : IRecoveryCalibreGateway
    {
        private static RecoveryCalibreCommandResult Success(
            string kind,
            CalibreBookId? created = null) =>
            new(kind, true, 0, [], string.Empty, string.Empty,
                TimeSpan.Zero, created);

        public Task<RecoveryCalibreCommandResult> CreateEmptyRecordAsync(
            CreateRecoveryRecordCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("construct:create");
            CalibreBookId createdId = new(42);
            if (behavior != RecoveryCommandBehavior.SuccessWithoutEffect)
            {
                ExpectedRecordState expected =
                    sourcePlan.Definition.ExpectedLibraryState.Records.Single(
                        value => value.RecordId == new CalibreBookId(2));
                IEnumerable<CalibreBook> books = scanner.Snapshot.Books.Append(
                    Book(expected, createdId, []));
                if (behavior == RecoveryCommandBehavior.CollateralMetadataMutation)
                    books = books.Select(value => value.Id != new CalibreBookId(1)
                        ? value
                        : new CalibreBook(value.Id, value.Title, value.AuthorSort,
                            value.Authors, value.Identifiers, value.Formats,
                            value.RelativeDirectory,
                            new BookPublicationMetadata(
                                "Unexpected collateral publisher",
                                value.PublicationMetadata.PublicationDate,
                                value.PublicationMetadata.Series,
                                value.PublicationMetadata.SeriesIndex,
                                value.PublicationMetadata.Languages,
                                value.PublicationMetadata.HasCover)));
                ReplaceBooks(books);
            }
            return Task.FromResult(Success("add", createdId));
        }

        public Task<RecoveryCalibreCommandResult> SetMetadataFieldAsync(
            SetRecoveryMetadataFieldCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add($"construct:metadata:{request.Field}");
            return Task.FromResult(Success("set_metadata"));
        }

        public Task<RecoveryCalibreCommandResult> AddOrReplaceFormatAsync(
            RestoreRecoveryFormatCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("construct:format");
            CalibreBook record = scanner.Snapshot.Books.Single(value =>
                value.Id == request.RecordId);
            ExpectedFormatState expected = sourcePlan.Definition.ExpectedLibraryState
                .Records.Single(value => value.RecordId == new CalibreBookId(2))
                .Formats.Single(value => value.Format == request.CanonicalFormat);
            BookFormat format = new(expected.Format, expected.StoredFileName,
                $"Author/Shared ({request.RecordId.Value})/book.pdf",
                FormatFileStatus.Present, expected.Fingerprint,
                expected.Observation);
            ReplaceBooks(scanner.Snapshot.Books.Where(value => value.Id != record.Id)
                .Append(new CalibreBook(record.Id, record.Title, record.AuthorSort,
                    record.Authors, record.Identifiers, [format],
                    record.RelativeDirectory, record.PublicationMetadata)));
            return Task.FromResult(Success("add_format"));
        }

        public Task<RecoveryCalibreCommandResult> RemoveFormatAsync(
            RemoveRecoveryFormatCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("destroy:format");
            if (behavior == RecoveryCommandBehavior.DestructiveFailure)
                return Task.FromResult(new RecoveryCalibreCommandResult(
                    "remove_format", true, 9, [], string.Empty, "failed",
                    TimeSpan.Zero, FailureCode: "CONTROLLED"));
            CalibreBook record = scanner.Snapshot.Books.Single(value =>
                value.Id == request.RecordId);
            ReplaceBooks(scanner.Snapshot.Books.Where(value => value.Id != record.Id)
                .Append(new CalibreBook(record.Id, record.Title, record.AuthorSort,
                    record.Authors, record.Identifiers,
                    record.Formats.Where(value =>
                        value.Format != request.CanonicalFormat),
                    record.RelativeDirectory, record.PublicationMetadata)));
            return Task.FromResult(Success("remove_format"));
        }

        public Task<RecoveryCalibreCommandResult> RemoveRecordAsync(
            RemoveRecoveryRecordCommand request,
            CancellationToken cancellationToken)
        {
            trace.Add("destroy:record");
            ReplaceBooks(scanner.Snapshot.Books.Where(value =>
                value.Id != request.RecordId));
            return Task.FromResult(Success("remove"));
        }

        private void ReplaceBooks(IEnumerable<CalibreBook> books) =>
            scanner.ReplaceSnapshot(new(scanner.Snapshot.Identity,
                scanner.Snapshot.ScannedAt.AddSeconds(1), books, []));

        private static CalibreBook Book(
            ExpectedRecordState expected,
            CalibreBookId id,
            IEnumerable<BookFormat> formats) =>
            new(id, expected.Title, expected.AuthorSort,
                expected.Authors.Select(value => new BookAuthor(
                    value.Id, value.Name, value.SortName)),
                expected.Identifiers.Select(value => new BookIdentifier(
                    value.Type, value.Value)),
                formats, $"Author/Shared ({id.Value})",
                new(expected.Publisher, expected.PublicationDate,
                    expected.Series, expected.SeriesIndex,
                    expected.Languages, expected.HasCover));
    }

    private sealed class RecordingBackupService(
        bool cancel,
        List<string> trace) : IRecoveryStateBackupService
    {
        public Task<RecoveryBackupDestinationValidation> ValidateDestinationAsync(
            string libraryRoot,
            string destination,
            long requiredBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RecoveryBackupDestinationValidation(
                destination, long.MaxValue, []));

        public Task<string> CreateWorkspaceAsync(
            RecoveryExecutionId recoveryExecutionId,
            string canonicalDestinationIdentity,
            CancellationToken cancellationToken) =>
            Task.FromResult($"{canonicalDestinationIdentity}\\recovery-{recoveryExecutionId}");

        public Task<RecoveryCurrentStateBackupResult> CreateAndVerifyAsync(
            CreateRecoveryCurrentStateBackupRequest request,
            CancellationToken cancellationToken)
        {
            trace.Add("backup:started");
            if (cancel) throw new OperationCanceledException(cancellationToken);
            trace.Add("backup:verified");
            RecoveryCurrentStateBackup backup = new(request.RecoveryExecutionId,
                request.Plan.Id, request.Plan.ContentDigest,
                request.CurrentState.Snapshot.Identity.CalibreLibraryUuid,
                request.BundleIdentity, $"{request.BundleIdentity}\\manifest.json",
                new(new string('a', 64)), new(new string('b', 64)),
                request.CurrentState.FullFingerprint, [], new Dictionary<
                    (CalibreBookId RecordId, string Format), string>());
            return Task.FromResult(new RecoveryCurrentStateBackupResult(backup, []));
        }

        public Task<IReadOnlyList<RecoveryIssue>> VerifyAvailableAsync(
            RecoveryCurrentStateBackup backup,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecoveryIssue>>([]);
    }

    private sealed class RecordingRecoveryJournal(
        RecoveryCommandBehavior behavior) : IRecoveryJournalSession
    {
        public List<RecoveryJournalEvent> Events { get; } = [];
        public string JournalIdentity => "C:\\synthetic\\recovery.journal.jsonl";
        public string? FinalEntryHash { get; private set; }
        public bool IsAvailable => true;

        public Task AppendAsync(
            RecoveryJournalEvent value,
            CancellationToken cancellationToken)
        {
            if (behavior
                    == RecoveryCommandBehavior.OperationVerificationJournalFailure
                && value.Kind == "OperationVerified")
                throw new IOException(
                    "Controlled operation-verification journal failure.");
            Events.Add(value);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            RecoveryHistoryEntry value,
            CancellationToken cancellationToken)
        {
            if (behavior == RecoveryCommandBehavior.TerminalPersistenceFailure)
                throw new IOException(
                    "Controlled terminal journal persistence failure.");
            FinalEntryHash = new string('f', 64);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldRecoveryLease : ILibraryMutationLeaseHandle
    {
        public string LeaseIdentity => "held";
        public bool IsHeld => true;
        public LibraryMutationKind MutationKind => LibraryMutationKind.Recovery;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
