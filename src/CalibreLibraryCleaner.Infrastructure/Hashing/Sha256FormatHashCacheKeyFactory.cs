using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Hashing;

internal sealed class Sha256FormatHashCacheKeyFactory : IFormatHashCacheKeyFactory
{
    public FormatHashCacheKey Create(ResolvedFormatPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.LibraryRoot));
        string relativePath = path.RelativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        StringBuilder canonical = new();
        Append(canonical, FormatHashCacheKey.PolicyVersion);
        Append(canonical, canonicalRoot.ToUpperInvariant());
        Append(canonical, relativePath.ToUpperInvariant());
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return new(digest);
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
