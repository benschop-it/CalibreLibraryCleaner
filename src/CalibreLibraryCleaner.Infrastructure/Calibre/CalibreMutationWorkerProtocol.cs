using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal static class CalibreMutationWorkerProtocol
{
    public const string Version = "calibre-mutation-worker-protocol/1.0";
    public const string ReadyKind = "ready";
    public const string ExecuteChunkKind = "executeChunk";
    public const string ChunkResultKind = "chunkResult";
    public const string ShutdownKind = "shutdown";
    public const string StoppedKind = "stopped";

    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static async Task WriteAsync<T>(
        TextWriter writer,
        T message,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new InvalidDataException("The Calibre mutation worker message exceeded its configured bound.");
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        TextReader reader,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string? line = await ReadBoundedLineAsync(reader, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (line is null) throw new EndOfStreamException("The Calibre mutation worker ended unexpectedly.");
        using (JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 }))
            ValidateNoDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(line, SerializerOptions)
            ?? throw new InvalidDataException("The Calibre mutation worker message was empty.");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new(Math.Min(maximumBytes / 2, 4096));
        char[] single = new char[1];
        int byteCount = 0;
        while (true)
        {
            int read = await reader.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            char character = single[0];
            if (character == '\n') return builder.ToString();
            if (character == '\r') continue;
            builder.Append(character);
            byteCount += character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
            if (byteCount > maximumBytes)
                throw new InvalidDataException("The Calibre mutation worker message exceeded its configured bound.");
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The Calibre mutation worker message contains a duplicate property.");
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) ValidateNoDuplicateProperties(item);
        }
    }
}

internal sealed record CalibreMutationWorkerReadyMessage(
    string ProtocolVersion,
    string Kind,
    string LibraryUuid,
    IReadOnlyList<string> Capabilities,
    string? FailureCode = null);

internal sealed record CalibreMutationWorkerRequestMessage(
    string ProtocolVersion,
    string Kind,
    string? ChunkId = null,
    IReadOnlyList<CalibreMutationWorkerOperationMessage>? Operations = null);

internal sealed record CalibreMutationWorkerOperationMessage(
    string OperationId,
    CalibreMutationOperationKind Kind,
    long RecordId,
    string? CanonicalFormat,
    long? TargetRecordId,
    long? ExpectedSizeInBytes,
    string? ExpectedSha256,
    LibraryMetadataField? MetadataField,
    IReadOnlyList<string>? MetadataValues,
    CalibreMetadataSourceIdentity? MetadataSource,
    string? StagedCoverFileName,
    long? StagedCoverSizeInBytes,
    string? StagedCoverSha256);

internal sealed record CalibreMutationWorkerResultMessage(
    string ProtocolVersion,
    string Kind,
    string? ChunkId = null,
    bool MutationStarted = false,
    IReadOnlyList<CalibreMutationWorkerOperationResultMessage>? OperationResults = null,
    string? FailureCode = null);

internal sealed record CalibreMutationWorkerOperationResultMessage(
    string OperationId,
    CalibreMutationOperationKind Kind,
    bool IsSuccess,
    string? FailureCode = null,
    IReadOnlyList<string>? VerifiedMetadataValues = null,
    string? VerifiedManagedPath = null,
    string? VerifiedAuthorSort = null);
