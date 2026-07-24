using CalibreLibraryCleaner.Application.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}
