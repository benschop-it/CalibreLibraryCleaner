using System.Text.Json;

string[] values = args;
string controlPath = Path.Combine(AppContext.BaseDirectory, "calibre-test-control.json");
Control control = File.Exists(controlPath)
    ? JsonSerializer.Deserialize<Control>(File.ReadAllText(controlPath)) ?? new()
    : new();
string version = control.Version;
if (values is ["-e", _, "--", _, string libraryUuid, string quarantineRoot, string expectedQuarantineUuid])
{
    IReadOnlyList<string> capabilities = new List<string>
    {
        "setMetadata", "refreshManagedName", "transferFormat", "removeFormat", "removeRecord",
        "copyRecordToQuarantine", "removeRecordWithContent",
    };
    if (!string.IsNullOrWhiteSpace(control.LogPath))
        File.AppendAllText(control.LogPath, JsonSerializer.Serialize(values) + Environment.NewLine);
    WriteWorkerMessage(new
    {
        protocolVersion = "calibre-mutation-worker-protocol/1.6",
        kind = "ready",
        libraryUuid,
        capabilities,
        quarantineLibraryUuid = string.IsNullOrEmpty(quarantineRoot)
            ? null
            : string.IsNullOrEmpty(expectedQuarantineUuid)
                ? "test-quarantine-uuid"
                : expectedQuarantineUuid,
        quarantineLibraryExisted = string.IsNullOrEmpty(quarantineRoot)
            ? (bool?)null
            : Directory.Exists(quarantineRoot),
    });
    string[] verifiedCoverValues = ["true"];
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        using JsonDocument message = JsonDocument.Parse(line);
        JsonElement root = message.RootElement;
        string kind = root.GetProperty("kind").GetString()!;
        if (kind == "shutdown")
        {
            WriteWorkerMessage(new
            {
                protocolVersion = "calibre-mutation-worker-protocol/1.6",
                kind = "stopped",
                mutationStarted = false,
            });
            return 0;
        }
        JsonElement operations = root.GetProperty("operations");
        WriteWorkerMessage(new
        {
            protocolVersion = "calibre-mutation-worker-protocol/1.6",
            kind = "chunkResult",
            chunkId = root.GetProperty("chunkId").GetString(),
            mutationStarted = true,
            operationResults = operations.EnumerateArray().Select(operation => new
            {
                operationId = operation.GetProperty("operationId").GetString(),
                kind = operation.GetProperty("kind").GetString(),
                isSuccess = true,
                isSkipped = operation.GetProperty("operationId").GetString()!.StartsWith(
                    "skip-unchanged", StringComparison.Ordinal),
                skipCode = operation.GetProperty("operationId").GetString()!.StartsWith(
                    "skip-unchanged", StringComparison.Ordinal)
                    ? "metadata_values_invalid"
                    : null,
                verifiedMetadataValues = operation.GetProperty("kind").GetString() == "setMetadata"
                    && !operation.GetProperty("operationId").GetString()!.StartsWith(
                        "skip-unchanged", StringComparison.Ordinal)
                    ? operation.GetProperty("metadataField").GetString() == "cover"
                        ? verifiedCoverValues
                        : operation.GetProperty("metadataValues").EnumerateArray()
                            .Select(value => value.GetString()).ToArray()
                    : null,
                verifiedManagedPath = operation.GetProperty("kind").GetString() == "setMetadata"
                    && !operation.GetProperty("operationId").GetString()!.StartsWith(
                        "skip-unchanged", StringComparison.Ordinal)
                    ? "Verified/Managed/Path"
                    : operation.GetProperty("kind").GetString() == "refreshManagedName"
                        ? $"Verified Author/Verified Title ({operation.GetProperty("recordId").GetInt64()})"
                        : operation.GetProperty("kind").GetString() == "copyRecordToQuarantine"
                            ? $"Copied Author/Copied Title ({operation.GetProperty("recordId").GetInt64() + 1000})"
                            : null,
                verifiedAuthorSort = operation.GetProperty("kind").GetString() == "setMetadata"
                    && !operation.GetProperty("operationId").GetString()!.StartsWith(
                        "skip-unchanged", StringComparison.Ordinal)
                    ? "Verified Author Sort"
                    : null,
                verifiedAuthorSortValues = operation.GetProperty("kind").GetString() == "setMetadata"
                    && operation.TryGetProperty("metadataAuthorSortValues", out JsonElement sorts)
                    && sorts.ValueKind == JsonValueKind.Array
                    ? sorts.EnumerateArray().Select(value => value.GetString()).ToArray()
                    : null,
                verifiedFormats = operation.GetProperty("kind").GetString() == "refreshManagedName"
                    ? new[]
                    {
                        new VerifiedFormat("EPUB", "Verified Title - Verified Author.epub", 10, new string('a', 64),
                            new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0)),
                    }
                    : operation.GetProperty("kind").GetString() == "copyRecordToQuarantine"
                        ? operation.GetProperty("quarantineFormats").EnumerateArray().Select(format => new VerifiedFormat(
                            format.GetProperty("format").GetString()!,
                            Path.GetFileName(format.GetProperty("expectedRelativePath").GetString()!)!,
                            format.GetProperty("expectedSizeInBytes").GetInt64(),
                            format.GetProperty("expectedSha256").GetString()!,
                            new(format.GetProperty("expectedSizeInBytes").GetInt64(),
                                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))).ToArray()
                        : operation.GetProperty("kind").GetString() == "setMetadata"
                            && operation.GetProperty("metadataField").GetString() is "title" or "authors"
                            && !operation.GetProperty("operationId").GetString()!.StartsWith(
                                "skip-unchanged", StringComparison.Ordinal)
                            ? Array.Empty<VerifiedFormat>() : null,
                quarantineRecordId = operation.GetProperty("kind").GetString() == "copyRecordToQuarantine"
                    ? operation.GetProperty("recordId").GetInt64() + 1000
                    : (long?)null,
            }).ToArray(),
        });
    }
    return 0;
}
if (values.SequenceEqual(["--version"]))
{
    Console.WriteLine($"calibredb.exe (calibre {version})");
    return 0;
}

if (values.Contains("--help", StringComparer.Ordinal))
{
    string command = values.FirstOrDefault() ?? "global";
    Console.WriteLine(command switch
    {
        "add_format" => "calibredb add_format [options] id ebook_file --dont-replace",
        "remove" => "calibredb remove [options] ids --permanent",
        "export" => "calibredb export [options] ids --dont-save-extra-files --dont-update-metadata --to-dir --single-dir",
        "add" => "calibredb add [options] files --empty --title --authors",
        "set_metadata" => "calibredb set_metadata [options] id --field",
        "remove_format" => "calibredb remove_format id formats",
        _ => "calibredb --with-library export add add_format remove remove_format set_metadata",
    });
    return 0;
}

string? logPath = control.LogPath;
if (!string.IsNullOrWhiteSpace(logPath))
    File.AppendAllText(logPath, JsonSerializer.Serialize(values) + Environment.NewLine);

if (control.SleepMilliseconds > 0)
    await Task.Delay(control.SleepMilliseconds);
if (control.StandardOutputCharacters > 0)
    Console.Write(new string('x', control.StandardOutputCharacters));
if (!string.IsNullOrWhiteSpace(control.EnvironmentProbe))
    Console.Write(Environment.GetEnvironmentVariable(control.EnvironmentProbe) ?? "<missing>");

int exportIndex = Array.IndexOf(values, "export");
if (exportIndex >= 0)
{
    int destinationIndex = Array.IndexOf(values, "--to-dir");
    string? fixture = control.ExportSource;
    if (destinationIndex >= 0 && destinationIndex + 1 < values.Length && !string.IsNullOrWhiteSpace(fixture))
    {
        string destination = values[destinationIndex + 1];
        foreach (string directory in Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(fixture, directory)));
        foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}

return control.ExitCode;

static void WriteWorkerMessage<T>(T message)
{
    Console.WriteLine(JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
    Console.Out.Flush();
}

internal sealed record Control(
    string Version = "9.11.0",
    string? LogPath = null,
    int SleepMilliseconds = 0,
    int StandardOutputCharacters = 0,
    int ExitCode = 0,
    string? ExportSource = null,
    string? EnvironmentProbe = null);

internal sealed record VerifiedFormat(
    string Format, string StoredFileName, long SizeInBytes, string Sha256, VerifiedObservation Observation);

internal sealed record VerifiedObservation(
    long Length, DateTimeOffset CreationTimeUtc, DateTimeOffset LastWriteTimeUtc, int Attributes);
