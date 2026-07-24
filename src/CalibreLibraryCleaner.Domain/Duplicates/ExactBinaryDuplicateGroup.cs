using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed record ExactBinaryDuplicateGroup
{
    public ExactBinaryDuplicateGroup(
        ExactBinaryDuplicateGroupId id,
        FormatFileFingerprint fingerprint,
        IEnumerable<ExactBinaryDuplicateMember> members)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(members);
        ExactBinaryDuplicateMember[] values = members.ToArray();
        if (values.Length < 2 || values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("An exact binary duplicate group requires at least two distinct files.", nameof(members));
        }

        Id = id;
        Fingerprint = fingerprint;
        Members = new ReadOnlyCollection<ExactBinaryDuplicateMember>(values);
    }

    public ExactBinaryDuplicateGroupId Id { get; }

    public FormatFileFingerprint Fingerprint { get; }

    public IReadOnlyList<ExactBinaryDuplicateMember> Members { get; }

    public int DistinctBookCount => Members.Select(member => member.BookId).Distinct().Count();

    public bool SpansMultipleBookRecords => DistinctBookCount > 1;
}
