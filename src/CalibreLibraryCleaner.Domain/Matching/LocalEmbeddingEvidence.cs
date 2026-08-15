using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public sealed record LocalEmbeddingModelIdentity
{
    public LocalEmbeddingModelIdentity(
        string runtimeId,
        string runtimeVersion,
        string modelId,
        string modelVersion,
        int dimensions)
    {
        RuntimeId = Bound(runtimeId, 64, nameof(runtimeId));
        RuntimeVersion = Bound(runtimeVersion, 128, nameof(runtimeVersion));
        ModelId = Bound(modelId, 128, nameof(modelId));
        ModelVersion = Bound(modelVersion, 160, nameof(modelVersion));
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4096);
        Dimensions = dimensions;
    }

    public string RuntimeId { get; }
    public string RuntimeVersion { get; }
    public string ModelId { get; }
    public string ModelVersion { get; }
    public int Dimensions { get; }

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record LocalEmbeddingInput
{
    public const string PreprocessingVersion = "local-metadata-embedding-input/1.0.0";

    public LocalEmbeddingInput(CalibreBookId bookId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string bounded = text.Trim();
        if (bounded.Length > 512) throw new ArgumentOutOfRangeException(nameof(text));
        BookId = bookId;
        Text = bounded;
        InputIdentity = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(PreprocessingVersion + "\n" + bounded)));
    }

    public CalibreBookId BookId { get; }
    public string Text { get; }
    public string InputIdentity { get; }

    public static LocalEmbeddingInput Create(CalibreBook book, BookMatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(profile);
        if (book.Id != profile.BookId)
            throw new ArgumentException("The embedding input book and profile must agree.");
        string title = Normalize(book.Title);
        string authors = string.Join("; ", book.Authors.Select(value => Normalize(value.Name))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string language = profile.Languages.Count == 1 ? profile.Languages[0] : "und";
        return new(book.Id, $"title: {title} | authors: {authors} | language: {language}");
    }

    private static string Normalize(string value) => string.Join(' ', value.Trim().Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed record LocalEmbeddingVector
{
    public LocalEmbeddingVector(
        CalibreBookId bookId,
        string inputIdentity,
        LocalEmbeddingModelIdentity model,
        IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(values);
        if (inputIdentity.Length != 64 || !inputIdentity.All(Uri.IsHexDigit))
            throw new ArgumentException("Embedding input identity is invalid.", nameof(inputIdentity));
        float[] bounded = values.Take(model.Dimensions + 1).ToArray();
        if (bounded.Length != model.Dimensions
            || bounded.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Embedding vector is invalid.", nameof(values));
        double magnitudeSquared = bounded.Sum(value => (double)value * value);
        if (magnitudeSquared <= 0) throw new ArgumentException("Embedding vector has no magnitude.", nameof(values));
        BookId = bookId;
        InputIdentity = inputIdentity.ToLowerInvariant();
        Model = model;
        Values = new ReadOnlyCollection<float>(bounded);
    }

    public CalibreBookId BookId { get; }
    public string InputIdentity { get; }
    public LocalEmbeddingModelIdentity Model { get; }
    public IReadOnlyList<float> Values { get; }
}

public enum LocalEmbeddingComparisonStatus
{
    Available,
    Unavailable,
}

public sealed record LocalEmbeddingComparison
{
    public const string PolicyVersion = "local-embedding-cosine/1.0.0";

    public LocalEmbeddingComparison(
        BookCandidatePairId pairId,
        LocalEmbeddingModelIdentity model,
        string firstInputIdentity,
        string secondInputIdentity,
        LocalEmbeddingComparisonStatus status,
        int? similarityPermille,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (firstInputIdentity.Length != 64 || !firstInputIdentity.All(Uri.IsHexDigit)
            || secondInputIdentity.Length != 64 || !secondInputIdentity.All(Uri.IsHexDigit)
            || !Enum.IsDefined(status)
            || (status == LocalEmbeddingComparisonStatus.Available) != similarityPermille.HasValue
            || similarityPermille is < -1000 or > 1000
            || problemCode is { Length: > 128 })
            throw new ArgumentException("Local embedding comparison is invalid.");
        PairId = pairId;
        Model = model;
        FirstInputIdentity = firstInputIdentity.ToLowerInvariant();
        SecondInputIdentity = secondInputIdentity.ToLowerInvariant();
        Status = status;
        SimilarityPermille = similarityPermille;
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public BookCandidatePairId PairId { get; }
    public LocalEmbeddingModelIdentity Model { get; }
    public string FirstInputIdentity { get; }
    public string SecondInputIdentity { get; }
    public LocalEmbeddingComparisonStatus Status { get; }
    public int? SimilarityPermille { get; }
    public string? ProblemCode { get; }
}

public static class LocalEmbeddingComparisonPolicy
{
    public static LocalEmbeddingComparison Compare(
        LocalEmbeddingVector first,
        LocalEmbeddingVector second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.BookId == second.BookId || first.Model != second.Model)
            throw new ArgumentException("Embedding vectors must describe distinct books and one model.");
        double dot = 0;
        double firstMagnitude = 0;
        double secondMagnitude = 0;
        for (int index = 0; index < first.Values.Count; index++)
        {
            dot += first.Values[index] * second.Values[index];
            firstMagnitude += first.Values[index] * first.Values[index];
            secondMagnitude += second.Values[index] * second.Values[index];
        }
        double similarity = dot / Math.Sqrt(firstMagnitude * secondMagnitude);
        int permille = (int)Math.Round(
            Math.Clamp(similarity, -1d, 1d) * 1000d,
            MidpointRounding.AwayFromZero);
        BookCandidatePairId pairId = new(first.BookId, second.BookId);
        LocalEmbeddingVector orderedFirst = pairId.First == first.BookId ? first : second;
        LocalEmbeddingVector orderedSecond = pairId.Second == second.BookId ? second : first;
        return new(
            pairId,
            first.Model,
            orderedFirst.InputIdentity,
            orderedSecond.InputIdentity,
            LocalEmbeddingComparisonStatus.Available,
            permille);
    }
}

public sealed record LocalEmbeddingComparisonCacheKey
{
    public LocalEmbeddingComparisonCacheKey(
        BookCandidatePairId pairId,
        LocalEmbeddingModelIdentity model,
        string firstInputIdentity,
        string secondInputIdentity)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (firstInputIdentity.Length != 64 || !firstInputIdentity.All(Uri.IsHexDigit)
            || secondInputIdentity.Length != 64 || !secondInputIdentity.All(Uri.IsHexDigit))
            throw new ArgumentException("Embedding comparison cache inputs are invalid.");
        PairId = pairId;
        Model = model;
        FirstInputIdentity = firstInputIdentity.ToLowerInvariant();
        SecondInputIdentity = secondInputIdentity.ToLowerInvariant();
        StringBuilder canonical = new();
        Append(canonical, LocalEmbeddingInput.PreprocessingVersion);
        Append(canonical, LocalEmbeddingComparison.PolicyVersion);
        Append(canonical, model.RuntimeId);
        Append(canonical, model.RuntimeVersion);
        Append(canonical, model.ModelId);
        Append(canonical, model.ModelVersion);
        Append(canonical, model.Dimensions.ToString(CultureInfo.InvariantCulture));
        Append(canonical, FirstInputIdentity);
        Append(canonical, SecondInputIdentity);
        Value = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public BookCandidatePairId PairId { get; }
    public LocalEmbeddingModelIdentity Model { get; }
    public string FirstInputIdentity { get; }
    public string SecondInputIdentity { get; }
    public string Value { get; }

    public static LocalEmbeddingComparisonCacheKey Create(
        BookCandidatePairId pairId,
        LocalEmbeddingModelIdentity model,
        IReadOnlyDictionary<CalibreBookId, LocalEmbeddingInput> inputs) => new(
        pairId,
        model,
        inputs[pairId.First].InputIdentity,
        inputs[pairId.Second].InputIdentity);

    private static void Append(StringBuilder target, string value) => target
        .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
        .Append(':').Append(value).Append('|');
}
