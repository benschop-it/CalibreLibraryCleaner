using System.Reflection;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public static class MatchingCorpusResources
{
    public static MatchingCorpus LoadCorpus(string fileName)
    {
        using Stream stream = Open(fileName);
        return MatchingCorpusLoader.Load(stream);
    }

    public static string LoadText(string fileName)
    {
        using Stream stream = Open(fileName);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static Stream Open(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Assembly assembly = typeof(MatchingCorpusResources).Assembly;
        string suffix = $".Matching.Corpus.{fileName}";
        string[] matches = assembly.GetManifestResourceNames()
            .Where(value => value.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new MatchingCorpusValidationException("CORPUS.RESOURCE_NOT_FOUND");
        return assembly.GetManifestResourceStream(matches[0])
            ?? throw new MatchingCorpusValidationException("CORPUS.RESOURCE_NOT_FOUND");
    }
}
