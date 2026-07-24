using System.Text.Json;

string[] values = args;
string controlPath = Path.Combine(AppContext.BaseDirectory, "calibre-test-control.json");
Control control = File.Exists(controlPath)
    ? JsonSerializer.Deserialize<Control>(File.ReadAllText(controlPath)) ?? new()
    : new();
string version = control.Version;
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
        _ => "calibredb --with-library export add_format remove",
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

internal sealed record Control(
    string Version = "9.11.0",
    string? LogPath = null,
    int SleepMilliseconds = 0,
    int StandardOutputCharacters = 0,
    int ExitCode = 0,
    string? ExportSource = null,
    string? EnvironmentProbe = null);
