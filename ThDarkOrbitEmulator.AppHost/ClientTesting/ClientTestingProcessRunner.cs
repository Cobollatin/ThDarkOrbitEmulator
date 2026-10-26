using System.Diagnostics;
using System.Text;

namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

internal static class ClientTestingProcessRunner
{
    public static async Task<ProcessRunResult> RunPowerShellScriptAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var argList = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath };
        argList.AddRange(arguments);
        return await RunAsync("powershell.exe", argList, workingDirectory, cancellationToken);
    }

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var started = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        started.Stop();

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask,
            started.Elapsed);
    }

    public static string TruncateForReport(string text, int maxChars = 12000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        var builder = new StringBuilder();
        builder.AppendLine(text[..maxChars]);
        builder.AppendLine();
        builder.AppendLine($"... truncated ({text.Length - maxChars} more characters)");
        return builder.ToString();
    }
}
