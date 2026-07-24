using System.Globalization;
using System.Text;

namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanContentDigestPolicy
{
    private const string CanonicalVersion = "cleanup-plan-body-canonical/1.0";

    public static CleanupPlanContentDigest Compute(CleanupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        StringBuilder value = new();
        Add(value, CanonicalVersion);
        ExpectedLibraryState library = definition.ExpectedLibraryState;
        Add(value, library.LibraryUuid);
        Add(value, library.SchemaVersion);
        Add(value, library.GroupId.Value);
        Add(value, library.RecommendationModelVersion.Value);
        Add(value, library.RecommendationInputVersion.Value);
        foreach (ExpectedRecordState record in library.Records)
        {
            Add(value, record.RecordId.Value);
            Add(value, record.Title);
            Add(value, record.AuthorSort);
            foreach (ExpectedAuthorState author in record.Authors)
            {
                Add(value, author.Id.Value);
                Add(value, author.Name);
                Add(value, author.SortName);
            }
            foreach (ExpectedIdentifierState identifier in record.Identifiers)
            {
                Add(value, identifier.Type);
                Add(value, identifier.Value);
            }
            Add(value, record.Publisher);
            Add(value, record.PublicationDate);
            Add(value, record.Series);
            Add(value, record.SeriesIndex);
            foreach (string language in record.Languages) Add(value, language);
            Add(value, record.HasCover);
            Add(value, record.RelativeDirectory);
            foreach (ExpectedFormatState format in record.Formats) AddFormat(value, format);
        }

        Add(value, definition.TargetRecordId.Value);
        Add(value, definition.MetadataRetention.TargetRecordId.Value);
        Add(value, definition.MetadataRetention.SourceRecordId.Value);
        Add(value, definition.MetadataRetention.ExpectedMetadataStateRecordId.Value);
        foreach (FormatRetentionInstruction retention in definition.FormatRetentions)
        {
            Add(value, retention.Id);
            Add(value, retention.Format);
            Add(value, retention.TargetRecordId.Value);
            AddFormat(value, retention.SourceState);
            Add(value, retention.Mode.ToString());
            Add(value, retention.ReviewedSelectionReference);
            foreach (string condition in retention.Preconditions) Add(value, condition);
        }
        foreach (FormatRemovalInstruction removal in definition.FormatRemovals)
        {
            Add(value, removal.RecordId.Value);
            Add(value, removal.Format);
            Add(value, removal.RelativePath);
            AddFormat(value, removal.ExpectedState);
            Add(value, removal.Reason.ToString());
            Add(value, removal.RetainedFormatInstructionId);
            Add(value, removal.BackupRequirementId);
            Add(value, removal.BytesIdenticalToRetainedSource);
        }
        foreach (RecordRemovalInstruction removal in definition.RecordRemovals)
        {
            Add(value, removal.RecordId.Value);
            foreach (string id in removal.BackupRequirementIds) Add(value, id);
            foreach (string id in removal.RequiredRetainedFormatInstructionIds) Add(value, id);
            foreach (string condition in removal.Preconditions) Add(value, condition);
        }
        foreach (BackupRequirement backup in definition.BackupRequirements)
        {
            Add(value, backup.Id);
            Add(value, backup.Kind.ToString());
            Add(value, backup.RecordId?.Value);
            Add(value, backup.Format);
            Add(value, backup.Required);
            Add(value, backup.Explanation);
        }

        CleanupPlanProvenance provenance = definition.Provenance;
        Add(value, provenance.GroupId.Value);
        Add(value, provenance.NormalizedTitle);
        foreach (string author in provenance.NormalizedAuthors) Add(value, author);
        Add(value, provenance.GroupReasonCode);
        Add(value, provenance.RecommendationModelVersion.Value);
        Add(value, provenance.RecommendationInputVersion.Value);
        Add(value, provenance.GeneratedConfidence.ToString());
        Add(value, provenance.GeneratedMetadataSourceRecordId.Value);
        AddSelections(value, provenance.GeneratedFormatSelections);
        foreach (string code in provenance.GeneratedReasonCodes) Add(value, code);
        foreach (string code in provenance.GeneratedWarningCodes) Add(value, code);
        Add(value, provenance.ReviewStatus.ToString());
        Add(value, provenance.Freshness.ToString());
        Add(value, provenance.ReviewedMetadataSourceRecordId.Value);
        AddSelections(value, provenance.ReviewedFormatSelections);
        Add(value, provenance.UserOverride.RequestedStatus.ToString());
        Add(value, provenance.UserOverride.ReviewedAtUtc);
        Add(value, provenance.UserOverride.MetadataSourceRecordId?.Value);
        foreach (string action in provenance.UserOverride.FormatActions) Add(value, action);
        foreach (var id in provenance.UserOverride.RetainedSeparateRecordIds) Add(value, id.Value);
        Add(value, provenance.SchemaVersion.Value);
        Add(value, provenance.PolicyVersion.Value);
        Add(value, provenance.CreatedAtUtc);
        return CleanupPlanContentDigest.FromCanonical(value.ToString());
    }

    private static void AddSelections(StringBuilder target, IEnumerable<CleanupPlanFormatSelectionProvenance> selections)
    {
        foreach (CleanupPlanFormatSelectionProvenance selection in selections)
        {
            Add(target, selection.Format);
            Add(target, selection.ResolutionStatus.ToString());
            Add(target, selection.SelectedRecordId?.Value);
            foreach (var id in selection.CandidateRecordIds) Add(target, id.Value);
            foreach (string code in selection.ReasonCodes) Add(target, code);
            foreach (string code in selection.WarningCodes) Add(target, code);
        }
    }

    private static void AddFormat(StringBuilder target, ExpectedFormatState format)
    {
        Add(target, format.RecordId.Value);
        Add(target, format.Format);
        Add(target, format.StoredFileName);
        Add(target, format.RelativePath);
        Add(target, format.Status.ToString());
        Add(target, format.Fingerprint.SizeInBytes);
        Add(target, format.Fingerprint.Sha256.Value);
        Add(target, format.Observation.Length);
        Add(target, format.Observation.CreationTimeUtc);
        Add(target, format.Observation.LastWriteTimeUtc);
        Add(target, format.Observation.Attributes);
        Add(target, format.ObservationSourceVersion);
    }

    private static void Add(StringBuilder target, object? item)
    {
        if (item is null)
        {
            target.Append("N;");
            return;
        }

        string value = item switch
        {
            DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "1" : "0",
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => item.ToString() ?? string.Empty,
        };
        target.Append('V').Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append(';');
    }
}
