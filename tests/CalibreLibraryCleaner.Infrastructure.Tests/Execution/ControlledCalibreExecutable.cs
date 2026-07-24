using System.Text.Json;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

internal sealed class ControlledCalibreExecutable : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _controlPath;
    private Control _control = new();

    public ControlledCalibreExecutable()
    {
        string sourceAssembly = typeof(TestCalibre.Marker).Assembly.Location;
        string sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        string sourceStem = Path.GetFileNameWithoutExtension(sourceAssembly);
        foreach (string source in Directory.EnumerateFiles(sourceDirectory, $"{sourceStem}.*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(source);
            File.Copy(source, Path.Combine(_directory.Path, name));
        }
        string appHost = Path.Combine(sourceDirectory, $"{sourceStem}.exe");
        ExecutablePath = Path.Combine(_directory.Path, "calibredb.exe");
        File.Copy(appHost, ExecutablePath);
        ConfigDirectory = Path.Combine(_directory.Path, "config");
        _controlPath = Path.Combine(_directory.Path, "calibre-test-control.json");
        Save();
    }

    public string ExecutablePath { get; }
    public string ConfigDirectory { get; }
    public string Root => _directory.Path;

    public void SetVersion(string value) { _control = _control with { Version = value }; Save(); }
    public void SetLogPath(string? value) { _control = _control with { LogPath = value }; Save(); }
    public void SetSleepMilliseconds(int value) { _control = _control with { SleepMilliseconds = value }; Save(); }
    public void SetStandardOutputCharacters(int value) { _control = _control with { StandardOutputCharacters = value }; Save(); }
    public void SetExitCode(int value) { _control = _control with { ExitCode = value }; Save(); }
    public void SetExportSource(string? value) { _control = _control with { ExportSource = value }; Save(); }
    public void SetEnvironmentProbe(string? value) { _control = _control with { EnvironmentProbe = value }; Save(); }

    public void Dispose() => _directory.Dispose();

    private void Save() => File.WriteAllText(_controlPath, JsonSerializer.Serialize(_control));

    private sealed record Control(
        string Version = "9.11.0",
        string? LogPath = null,
        int SleepMilliseconds = 0,
        int StandardOutputCharacters = 0,
        int ExitCode = 0,
        string? ExportSource = null,
        string? EnvironmentProbe = null);
}
