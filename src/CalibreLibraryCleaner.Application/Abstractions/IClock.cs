namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IClock
{
    DateTimeOffset GetUtcNow();
}
