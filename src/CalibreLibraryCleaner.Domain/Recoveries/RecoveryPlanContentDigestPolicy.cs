using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public static class RecoveryPlanContentDigestPolicy
{
    public static RecoveryPlanContentDigest Compute(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CanonicalBody body = new();
        body.Add("schema", RecoveryPlanSchemaVersion.V1.Value);
        body.Add("model", RecoveryModelVersion.V1.Value);
        body.Add("policy", RecoveryPolicyVersion.V1.Value);
        RecoveryInputIdentity input = definition.InputIdentity;
        body.Add("source-plan-id", input.SourcePlanId.ToString());
        body.Add("source-plan-schema", input.SourcePlanSchemaVersion.Value);
        body.Add("source-plan-revision", input.SourcePlanRevision.Value.ToString(CultureInfo.InvariantCulture));
        body.Add("source-plan-digest", input.SourcePlanContentDigest.Value);
        body.Add("source-execution-id", input.SourceExecutionId.ToString());
        body.Add("source-journal-schema", input.SourceJournalSchema);
        body.Add("source-journal-digest", input.SourceJournalFileDigest.Value);
        body.Add("source-journal-final", input.SourceJournalFinalEntryHash);
        body.Add("source-summary-digest", input.SourceTerminalSummaryDigest?.Value ?? "<absent>");
        body.Add("source-manifest-schema", input.OriginalManifestSchema);
        body.Add("source-manifest-internal", input.OriginalManifestInternalDigest.Value);
        body.Add("source-manifest-file", input.OriginalManifestFileDigest.Value);
        body.Add("source-application", input.SourceApplicationVersion);
        body.Add("source-tool-path", input.SourceToolIdentity.CanonicalExecutableIdentity);
        body.Add("source-tool-version", input.SourceToolIdentity.ProductVersion);
        body.Add("source-tool-digest", input.SourceToolIdentity.ExecutableSha256.Value);
        body.Add("source-tool-profile", input.SourceToolIdentity.CapabilityProfile);
        body.Add("library-uuid", input.CurrentLibraryUuid);
        body.Add("library-schema", input.CurrentLibrarySchemaVersion.ToString(CultureInfo.InvariantCulture));
        body.Add("root-identity", input.CanonicalRootIdentityDigest.Value);
        body.Add("full-state", input.FullStateFingerprint.Value);
        body.Add("affected-state", input.AffectedStateFingerprint.Value);
        body.Add("unrelated-state", input.UnrelatedStateFingerprint.Value);
        body.Add("reconciliation-version", input.ReconciliationVersion.Value);
        body.Add("reconciliation-digest", input.ReconciliationDigest.Value);
        body.Add("recovery-capability", input.RecoveryCapabilityProfile);
        body.Add("graph-digest", definition.OperationGraph.Digest.Value);
        body.Add("original-chain-journal", definition.OriginalBackupChain.SourceJournalFileDigest.Value);
        body.Add("original-chain-journal-final", definition.OriginalBackupChain.SourceJournalFinalEntryHash);
        body.Add("original-chain-manifest", definition.OriginalBackupChain.OriginalManifestFileDigest.Value);
        body.Add("original-chain-manifest-internal", definition.OriginalBackupChain.OriginalManifestInternalDigest.Value);
        body.Add("original-chain-recovery-plan-id", definition.OriginalBackupChain.RecoveryPlanId.ToString());
        body.Add("provenance-source-plan", definition.Provenance.SourceCleanupPlanId.ToString());
        body.Add("provenance-source-execution", definition.Provenance.SourceExecutionId.ToString());
        body.Add("provenance-reconciled-at", definition.Provenance.ReconciledAtUtc.ToString(
            "O", CultureInfo.InvariantCulture));
        body.Add("provenance-source-disposition", definition.Provenance.SourceDisposition);
        body.Add("provenance-source-bundle", definition.Provenance.SourceBundleIdentity);

        foreach (SourceOperationProjection sourceOperation in
                 definition.Reconciliation.SourceOperations)
        {
            body.Add("source-operation-id", sourceOperation.OperationId.Value);
            body.Add("source-operation-kind", sourceOperation.Kind.ToString());
            body.Add("source-operation-state", sourceOperation.State.ToString());
            body.Add("source-operation-target", sourceOperation.TargetRecordId.Value.ToString(
                CultureInfo.InvariantCulture));
            body.Add("source-operation-source", sourceOperation.SourceRecordId?.Value.ToString(
                CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("source-operation-format", sourceOperation.Format ?? "<absent>");
            foreach (ExecutionOperationId dependency in sourceOperation.DependencyIds)
                body.Add("source-operation-dependency", dependency.Value);
        }

        foreach (ReconciledRecoveryRecord record in definition.Reconciliation.Records)
        {
            body.Add("logical-record", record.Identity.LogicalRecordId.Value);
            body.Add("original-id", record.Identity.OriginalRecordId.Value.ToString(CultureInfo.InvariantCulture));
            body.Add("current-id", record.Identity.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("recovered-id", record.Identity.RecoveredRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("metadata-fingerprint", record.Identity.MetadataFingerprint);
            body.Add("current-metadata-fingerprint", record.Identity.CurrentMetadataFingerprint ?? "<absent>");
            foreach (string identifier in record.Identity.Identifiers)
                body.Add("record-identifier", identifier);
            foreach (RecoveryFormatIdentity identityFormat in record.Identity.BackedUpFormats)
            {
                body.Add("identity-format", identityFormat.Format);
                body.Add("identity-format-size", identityFormat.Fingerprint.SizeInBytes.ToString(CultureInfo.InvariantCulture));
                body.Add("identity-format-hash", identityFormat.Fingerprint.Sha256.Value);
                body.Add("identity-format-backup", identityFormat.BackupArtifactIdentity);
            }
            body.Add("record-backup-artifact", record.Identity.OriginalBackupArtifactIdentity);
            body.Add("preserved-separate-id", record.Identity.PreservedSeparateRecordId?.Value.ToString(
                CultureInfo.InvariantCulture) ?? "<absent>");
            foreach (RecoveryReconciliationClassification classification in record.Classifications)
                body.Add("record-classification", classification.ToString());
            foreach (ReconciledFormat format in record.Formats)
            {
                body.Add("format", format.Format);
                body.Add("format-classification", format.Classification.ToString());
                body.Add("pre-size", format.OriginalFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
                body.Add("pre-hash", format.OriginalFingerprint?.Sha256.Value ?? "<absent>");
                body.Add("post-size", format.ExpectedPostFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
                body.Add("post-hash", format.ExpectedPostFingerprint?.Sha256.Value ?? "<absent>");
                body.Add("current-size", format.CurrentFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
                body.Add("current-hash", format.CurrentFingerprint?.Sha256.Value ?? "<absent>");
                body.Add("format-current-record", format.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
                body.Add("preserve", format.PreserveCurrentContent ? "true" : "false");
                body.Add("backup-artifact", format.OriginalBackupArtifactIdentity);
                body.Add("source-operation", format.SourceOperationId?.Value ?? "<absent>");
            }
            foreach (CalibreBookId candidate in record.AmbiguousCandidateIds)
                body.Add("ambiguous-candidate", candidate.Value.ToString(CultureInfo.InvariantCulture));
        }
        foreach (RecoveryIssue issue in definition.Reconciliation.Issues)
            AddIssue(body, "reconciliation", issue);

        foreach (RecoveryOperation operation in definition.OperationGraph.Operations)
        {
            body.Add("operation-id", operation.Id.Value);
            body.Add("operation-kind", operation.Kind.ToString());
            body.Add("operation-phase", operation.Phase.ToString());
            body.Add("operation-record", operation.LogicalRecordId.Value);
            body.Add("operation-original-id", operation.OriginalRecordId.Value.ToString(CultureInfo.InvariantCulture));
            body.Add("operation-current-id", operation.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("operation-proposed-id", operation.ProposedRecoveredRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("operation-format", operation.Format ?? "<absent>");
            body.Add("operation-backup", operation.OriginalBackupArtifactIdentity ?? "<not-applicable>");
            body.Add("operation-pre-size", operation.OriginalBackupFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("operation-pre-hash", operation.OriginalBackupFingerprint?.Sha256.Value ?? "<absent>");
            body.Add("operation-current-size", operation.ExpectedCurrentFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("operation-current-hash", operation.ExpectedCurrentFingerprint?.Sha256.Value ?? "<absent>");
            body.Add("operation-risk", operation.Risk.ToString());
            body.Add("operation-capability", operation.RequiredCapability);
            body.Add("operation-reason", operation.Reason);
            body.Add("operation-journal", operation.SourceJournalEvidence);
            foreach (RecoveryOperationId dependency in operation.DependencyIds) body.Add("dependency", dependency.Value);
            foreach (RecoveryOperationId dependency in operation.PreservationDependencyIds) body.Add("preservation-dependency", dependency.Value);
            body.Add("verification-code", operation.Verification.Code);
            body.Add("verification-record", operation.Verification.LogicalRecordId.Value);
            body.Add("verification-description", operation.Verification.Description);
            body.Add("verification-current-id", operation.Verification.ExpectedCurrentRecordId?.Value.ToString(
                CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("verification-format", operation.Verification.Format ?? "<absent>");
            body.Add("verification-size", operation.Verification.ExpectedFingerprint?.SizeInBytes.ToString(
                CultureInfo.InvariantCulture) ?? "<absent>");
            body.Add("verification-hash", operation.Verification.ExpectedFingerprint?.Sha256.Value ?? "<absent>");
            body.Add("verification-present", operation.Verification.ExpectedPresent ? "true" : "false");
        }

        foreach (ExpectedRecoveredRecordState expected in definition.ExpectedFinalState.Records)
        {
            body.Add("expected-record", expected.LogicalRecordId.Value);
            body.Add("expected-current-id", expected.ExpectedCurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "<new-id>");
            AddExpectedRecord(body, expected.OriginalState);
            foreach (string field in expected.SupportedMetadataFields.Order(StringComparer.Ordinal))
                body.Add("expected-field", field);
        }
        foreach (PreservedContentExpectation preserved in definition.ExpectedFinalState.PreservedContent)
        {
            body.Add("preserved-record", preserved.LogicalRecordId.Value);
            body.Add("preserved-current-id", preserved.CurrentRecordId.Value.ToString(CultureInfo.InvariantCulture));
            body.Add("preserved-format", preserved.Format);
            body.Add("preserved-size", preserved.Fingerprint.SizeInBytes.ToString(CultureInfo.InvariantCulture));
            body.Add("preserved-hash", preserved.Fingerprint.Sha256.Value);
            body.Add("preserved-reason", preserved.PreservationReason);
        }
        foreach ((CalibreBookId recordId, string format) in definition.ExpectedFinalState.ExpectedAbsentFormats)
        {
            body.Add("absent-format-record", recordId.Value.ToString(CultureInfo.InvariantCulture));
            body.Add("absent-format", format);
        }
        foreach (CalibreBookId recordId in definition.ExpectedFinalState.ExpectedAbsentRecords)
            body.Add("absent-record", recordId.Value.ToString(CultureInfo.InvariantCulture));
        body.Add("expected-unrelated-state", definition.ExpectedFinalState.UnrelatedStateFingerprint.Value);
        foreach (RecoveryIssue issue in definition.Issues)
            AddIssue(body, "definition", issue);
        return RecoveryPlanContentDigest.FromCanonical(body.ToString());
    }

    private static void AddIssue(
        CanonicalBody body,
        string scope,
        RecoveryIssue issue)
    {
        body.Add("issue-scope", scope);
        body.Add("issue-severity", issue.Severity.ToString());
        body.Add("issue-code", issue.Code);
        body.Add("issue-subject", issue.Subject);
        body.Add("issue-explanation", issue.Explanation);
        body.Add("issue-logical-record", issue.LogicalRecordId?.Value ?? "<absent>");
        body.Add("issue-current-record", issue.CurrentRecordId?.Value.ToString(
            CultureInfo.InvariantCulture) ?? "<absent>");
        body.Add("issue-format", issue.Format ?? "<absent>");
        foreach ((string key, string value) in issue.Evidence)
        {
            body.Add("issue-evidence-key", key);
            body.Add("issue-evidence-value", value);
        }
    }

    private static void AddExpectedRecord(CanonicalBody body, ExpectedRecordState record)
    {
        body.Add("expected-original-id", record.RecordId.Value.ToString(CultureInfo.InvariantCulture));
        body.Add("expected-title", record.Title);
        body.Add("expected-author-sort", record.AuthorSort);
        foreach (ExpectedAuthorState author in record.Authors)
        {
            body.Add("expected-author-id", author.Id.Value.ToString(CultureInfo.InvariantCulture));
            body.Add("expected-author-name", author.Name);
            body.Add("expected-author-sort-name", author.SortName);
        }
        foreach (ExpectedIdentifierState identifier in record.Identifiers)
        {
            body.Add("expected-identifier-type", identifier.Type);
            body.Add("expected-identifier-value", identifier.Value);
        }
        body.Add("expected-publisher", record.Publisher ?? "<absent>");
        body.Add("expected-publication-date", record.PublicationDate?.ToString("O", CultureInfo.InvariantCulture) ?? "<absent>");
        body.Add("expected-series", record.Series ?? "<absent>");
        body.Add("expected-series-index", record.SeriesIndex?.ToString(CultureInfo.InvariantCulture) ?? "<absent>");
        foreach (string language in record.Languages) body.Add("expected-language", language);
        body.Add("expected-has-cover", record.HasCover ? "true" : "false");
        body.Add("expected-relative-directory", record.RelativeDirectory);
        foreach (ExpectedFormatState format in record.Formats)
        {
            body.Add("expected-format", format.Format);
            body.Add("expected-format-file", format.StoredFileName);
            body.Add("expected-format-path", format.RelativePath);
            body.Add("expected-format-status", format.Status.ToString());
            body.Add("expected-format-size", format.Fingerprint.SizeInBytes.ToString(CultureInfo.InvariantCulture));
            body.Add("expected-format-hash", format.Fingerprint.Sha256.Value);
            body.Add("expected-format-observation-source", format.ObservationSourceVersion);
            body.Add("expected-format-observation-length", format.Observation.Length.ToString(CultureInfo.InvariantCulture));
            body.Add("expected-format-observation-created", format.Observation.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            body.Add("expected-format-observation-written", format.Observation.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            body.Add("expected-format-observation-attributes", format.Observation.Attributes.ToString(CultureInfo.InvariantCulture));
        }
    }

    private sealed class CanonicalBody
    {
        private readonly StringBuilder _builder = new();

        public void Add(string discriminator, string value)
        {
            Append(discriminator);
            Append(value);
        }

        public override string ToString() => _builder.ToString();

        private void Append(string value) =>
            _builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}
