using System.Globalization;

namespace CalibreLibraryCleaner.Domain.Libraries;

public readonly record struct CalibreBookId
{
    public CalibreBookId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
