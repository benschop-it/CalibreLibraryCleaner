using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

internal static class PdfWorkerProtocol
{
    public const string Version = "pdf-worker-protocol/1.0";

    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64,
        };
        options.Converters.Add(new CalibreBookIdConverter());
        options.Converters.Add(new Sha256DigestConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static async Task WriteAsync<T>(
        TextWriter writer,
        T message,
        int maximumBytes,
        CancellationToken cancellationToken,
        PdfWorkerMessageBudget? budget = null,
        Action<int>? observeByteCount = null)
    {
        string json = JsonSerializer.Serialize(message, SerializerOptions);
        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > maximumBytes)
        {
            throw new InvalidDataException("The PDF worker protocol message exceeded its configured bound.");
        }

        observeByteCount?.Invoke(byteCount);
        budget?.Consume(checked(byteCount + 1));
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        TextReader reader,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action<int>? observeByteCount = null,
        PdfWorkerMessageBudget? budget = null)
    {
        BoundedLine? boundedLine = await ReadBoundedLineAsync(reader, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (boundedLine is null)
        {
            throw new EndOfStreamException("The PDF worker protocol ended unexpectedly.");
        }

        string line = boundedLine.Value.Value;
        observeByteCount?.Invoke(boundedLine.Value.ByteCount);
        budget?.Consume(checked(boundedLine.Value.ByteCount + 1));
        using (JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }))
        {
            ValidateNoDuplicateProperties(document.RootElement);
        }

        T? result = JsonSerializer.Deserialize<T>(line, SerializerOptions);
        return result ?? throw new InvalidDataException("The PDF worker protocol message was empty.");
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException("The PDF worker protocol message contains a duplicate property.");
                    }

                    ValidateNoDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateNoDuplicateProperties(item);
                }

                break;
        }
    }

    private static async Task<BoundedLine?> ReadBoundedLineAsync(TextReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        StringBuilder builder = new(Math.Min(maximumBytes / 2, 4096));
        char[] single = new char[1];
        int byteCount = 0;
        while (true)
        {
            int read = await reader.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0 ? null : new(builder.ToString(), byteCount);
            }

            char character = single[0];
            if (character == '\n')
            {
                return new(builder.ToString(), byteCount);
            }

            if (character != '\r')
            {
                builder.Append(character);
                byteCount += character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                if (byteCount > maximumBytes)
                {
                    throw new InvalidDataException("The PDF worker protocol message exceeded its configured bound.");
                }
            }
        }
    }

    private readonly record struct BoundedLine(string Value, int ByteCount);

    private sealed class CalibreBookIdConverter : JsonConverter<CalibreBookId>
    {
        public override CalibreBookId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, CalibreBookId value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Value);
    }

    private sealed class Sha256DigestConverter : JsonConverter<Sha256Digest>
    {
        public override Sha256Digest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("The SHA-256 value is required."));

        public override void Write(Utf8JsonWriter writer, Sha256Digest value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

internal sealed class PdfWorkerMessageBudget(int maximumBytes)
{
    private int consumedBytes;

    public void Consume(int bytes)
    {
        int total = Interlocked.Add(ref consumedBytes, bytes);
        if (total > maximumBytes)
        {
            throw new InvalidDataException("The PDF worker protocol response exceeded its configured total bound.");
        }
    }
}

internal sealed record PdfWorkerRequestMessage(string ProtocolVersion, string Kind, PdfInspectionRequest Request);

internal sealed record PdfWorkerSelectionMessage(string ProtocolVersion, string Kind, IReadOnlyList<int> SelectedPages);

internal sealed record PdfWorkerOutputMessage(
    string ProtocolVersion,
    string Kind,
    PdfDocumentHeaderFacts? Header = null,
    PdfInspectionProgress? Progress = null,
    PdfInspectionResult? Result = null,
    PdfInspectionProblemCode? FailureCode = null);
