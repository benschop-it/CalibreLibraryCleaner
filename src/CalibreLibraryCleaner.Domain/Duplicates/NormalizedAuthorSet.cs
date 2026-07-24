using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public sealed class NormalizedAuthorSet : IEquatable<NormalizedAuthorSet>
{
    internal NormalizedAuthorSet(IEnumerable<NormalizedAuthorName> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        NormalizedAuthorName[] values = names
            .Distinct()
            .OrderBy(name => name.Value, StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("A normalized author set requires at least one author.", nameof(names));
        }

        Names = new ReadOnlyCollection<NormalizedAuthorName>(values);
    }

    public IReadOnlyList<NormalizedAuthorName> Names { get; }

    public bool Equals(NormalizedAuthorSet? other) =>
        other is not null && Names.SequenceEqual(other.Names);

    public override bool Equals(object? obj) => obj is NormalizedAuthorSet other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (NormalizedAuthorName name in Names)
        {
            hash.Add(name);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(" | ", Names.Select(name => name.Value));
}
