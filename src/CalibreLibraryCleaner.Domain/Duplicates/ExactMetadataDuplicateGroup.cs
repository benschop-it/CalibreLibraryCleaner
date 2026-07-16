using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactMetadataDuplicateGroup
{
    public ExactMetadataDuplicateGroup(
        NormalizedBookIdentity identity,
        IEnumerable<CalibreBookId> members)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(members);
        CalibreBookId[] values = members.Distinct().OrderBy(member => member.Value).ToArray();
        if (values.Length < 2)
        {
            throw new ArgumentException(
                "An exact metadata duplicate group requires at least two distinct book records.",
                nameof(members));
        }

        Id = ExactMetadataDuplicateGroupId.From(identity);
        Identity = identity;
        MatchReason = ExactMetadataDuplicateMatchReason.TitleAndAuthorSetEqual;
        Members = new ReadOnlyCollection<CalibreBookId>(values);
    }

    public ExactMetadataDuplicateGroupId Id { get; }

    public NormalizedBookIdentity Identity { get; }

    public ExactMetadataDuplicateMatchReason MatchReason { get; }

    public IReadOnlyList<CalibreBookId> Members { get; }
}
