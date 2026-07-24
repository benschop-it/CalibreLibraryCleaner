using System.Diagnostics;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class DirectCalibreProcessRunner(CalibreExecutionOptions options)
{
    public async Task<CalibreCommandResult> RunAsync(
        string executablePath,
        string libraryRoot,
        string commandKind,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> sensitiveValues,
        bool mayTerminateOnCancellation,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutionPathGuard.TryValidateExternalDirectory(
                libraryRoot, options.ControlledConfigDirectory, false,
                out string? controlledConfigDirectory, out _))
            return new(commandKind, false, null, [], string.Empty, string.Empty,
                TimeSpan.Zero, "CALIBRE_CONFIG_LOCATION_UNSAFE");
        try
        {
            Directory.CreateDirectory(controlledConfigDirectory!);
            if (!ExecutionPathGuard.TryValidateExternalDirectory(
                    libraryRoot, controlledConfigDirectory!, true, out controlledConfigDirectory, out _))
                return new(commandKind, false, null, [], string.Empty, string.Empty,
                    TimeSpan.Zero, "CALIBRE_CONFIG_LOCATION_UNSAFE");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(commandKind, false, null, [], string.Empty, string.Empty,
                TimeSpan.Zero, "CALIBRE_CONFIG_LOCATION_UNAVAILABLE");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = controlledConfigDirectory,
        };
        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            inherited[name] = startInfo.Environment.TryGetValue(name, out string? value) ? value : null;
        startInfo.Environment.Clear();
        foreach ((string name, string? value) in inherited.Where(value => !string.IsNullOrWhiteSpace(value.Value)))
            startInfo.Environment[name] = value!;
        startInfo.Environment["CALIBRE_CONFIG_DIRECTORY"] = controlledConfigDirectory!;
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        string[] sanitizedArguments = arguments.Select(value => Sanitize(value, sensitiveValues)).ToArray();
        Stopwatch elapsed = Stopwatch.StartNew();
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new(commandKind, false, null, sanitizedArguments, string.Empty, string.Empty,
                    elapsed.Elapsed, "CALIBRE_PROCESS_NOT_STARTED");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(commandKind, false, null, sanitizedArguments, string.Empty, string.Empty,
                elapsed.Elapsed, "CALIBRE_PROCESS_START_FAILED");
        }

        Task<string> output = DrainBoundedAsync(process.StandardOutput, sensitiveValues);
        Task<string> error = DrainBoundedAsync(process.StandardError, sensitiveValues);
        Task wait = process.WaitForExitAsync(CancellationToken.None);
        Task? timeoutTask = timeout is null ? null : Task.Delay(timeout.Value, CancellationToken.None);
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny([wait, cancellationTask, .. timeoutTask is null ? [] : new[] { timeoutTask }]).ConfigureAwait(false);
        if (completed != wait)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            if (mayTerminateOnCancellation && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }

            if (!mayTerminateOnCancellation)
                await wait.ConfigureAwait(false);
            else
            {
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }

            string capturedOutput = await output.ConfigureAwait(false);
            string capturedError = await error.ConfigureAwait(false);
            if (cancelled) throw new OperationCanceledException(cancellationToken);
            return new(commandKind, true, process.HasExited ? process.ExitCode : null, sanitizedArguments,
                capturedOutput, capturedError, elapsed.Elapsed, "CALIBRE_PROCESS_TIMEOUT");
        }

        await wait.ConfigureAwait(false);
        return new(commandKind, true, process.ExitCode, sanitizedArguments,
            await output.ConfigureAwait(false), await error.ConfigureAwait(false), elapsed.Elapsed,
            process.ExitCode == 0 ? null : "CALIBRE_PROCESS_NONZERO_EXIT");
    }

    private async Task<string> DrainBoundedAsync(StreamReader reader, IReadOnlyList<string> sensitiveValues)
    {
        char[] buffer = new char[4096];
        System.Text.StringBuilder captured = new(Math.Min(options.MaximumCapturedCharacters, 4096));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            int remaining = options.MaximumCapturedCharacters - captured.Length;
            if (remaining > 0) captured.Append(buffer, 0, Math.Min(read, remaining));
        }

        return Sanitize(captured.ToString(), sensitiveValues);
    }

    private static string Sanitize(string value, IReadOnlyList<string> sensitiveValues)
    {
        string sanitized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string sensitive in sensitiveValues.Where(value => !string.IsNullOrWhiteSpace(value))
                     .OrderByDescending(value => value.Length))
            sanitized = sanitized.Replace(sensitive, "<path>", StringComparison.OrdinalIgnoreCase);
        return sanitized;
    }
}
