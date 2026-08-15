using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IFormatHashCacheKeyFactory
{
    FormatHashCacheKey Create(ResolvedFormatPath path);
}
