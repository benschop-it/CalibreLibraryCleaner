namespace CalibreLibraryCleaner.Infrastructure.LocalModels;

public sealed record OllamaEmbeddingOptions
{
    public OllamaEmbeddingOptions(
        string? modelId = null,
        string? modelVersion = null,
        string? runtimeVersion = null,
        int dimensions = 256,
        int maximumBatchSize = 8,
        int maximumResponseBytes = 2 * 1024 * 1024,
        TimeSpan? requestTimeout = null)
    {
        string? normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        string? normalizedVersion = string.IsNullOrWhiteSpace(modelVersion) ? null : modelVersion.Trim();
        string? normalizedRuntime = string.IsNullOrWhiteSpace(runtimeVersion) ? null : runtimeVersion.Trim();
        if ((normalizedModel is null) != (normalizedVersion is null)
            || (normalizedModel is null) != (normalizedRuntime is null)
            || normalizedModel is { Length: > 128 }
            || normalizedVersion is { Length: > 160 }
            || normalizedRuntime is { Length: > 128 })
            throw new ArgumentException(
                "Ollama runtime, model ID, and immutable model version must be configured together.");
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBatchSize, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResponseBytes, 8 * 1024 * 1024);
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        ModelId = normalizedModel;
        ModelVersion = normalizedVersion;
        RuntimeVersion = normalizedRuntime;
        Dimensions = dimensions;
        MaximumBatchSize = maximumBatchSize;
        MaximumResponseBytes = maximumResponseBytes;
        RequestTimeout = timeout;
    }

    public bool Enabled => ModelId is not null;
    public string? ModelId { get; }
    public string? ModelVersion { get; }
    public string? RuntimeVersion { get; }
    public int Dimensions { get; }
    public int MaximumBatchSize { get; }
    public int MaximumResponseBytes { get; }
    public TimeSpan RequestTimeout { get; }

    public override string ToString() =>
        $"OllamaEmbedding(Enabled={Enabled}, Dimensions={Dimensions}, Batch={MaximumBatchSize}, Timeout={RequestTimeout})";
}
