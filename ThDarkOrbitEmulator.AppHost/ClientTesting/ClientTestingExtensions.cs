using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ThDarkOrbitEmulator.AppHost.Operations;

namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal static class ClientTestingExtensions
{
    public static IResourceBuilder<ProjectResource> WithClientTestingCommands(
        this IResourceBuilder<ProjectResource> builder,
        ClientTestingPaths paths)
    {
        builder.WithCommand("client-test-guide", "Client test: Workflow", _ => RunGuideAsync(paths), ClientCommand("Book", "Local-only DarkOrbit client testing workflow."));
        builder.WithCommand("client-test-endpoints", "Client test: Endpoint catalog", _ => RunEndpointCatalogAsync(paths), ClientCommand("List", "Hosts/paths the emulator can serve for hardcoded client URLs."));
        builder.WithCommand("client-test-scan-urls", "Client test: Scan hardcoded URLs", ctx => RunScanUrlsAsync(paths, ctx), ClientCommand("Search", "Run Find-HardcodedUrls.ps1 on the game install (slow)."));
        builder.WithCommand("client-test-replace-preview", "Client test: Replace preview", ctx => RunReplacePreviewAsync(paths, ctx), ClientCommand("Edit", "Dry-run Replace-HardcodedUrls.ps1."));
        builder.WithCommand("client-test-replace-apply", "Client test: Replace apply", ctx => RunReplaceApplyAsync(paths, ctx), ClientCommand("Checkmark", "Apply URL patches to client binaries.", confirm: true));
        builder.WithCommand("client-test-replace-rollback", "Client test: Replace rollback", ctx => RunReplaceRollbackAsync(paths, ctx), ClientCommand("ArrowUndo", "Restore client files from manifest.", confirm: true));
        builder.WithCommand("client-test-start-mitm", "Client test: Start mitmproxy", ctx => RunStartMitmAsync(paths, ctx), ClientCommand("Play", "Start mitmproxy local mode and optionally launch DarkOrbit.exe."));
        builder.WithCommand("client-test-stop-mitm", "Client test: Stop mitmproxy", _ => RunStopMitmAsync(paths), ClientCommand("Stop", "Stop background mitmproxy process."));
        builder.WithCommand("client-test-analyze-traffic", "Client test: Analyze traffic", _ => RunAnalyzeTrafficAsync(paths), ClientCommand("ChartMultiple", "Compare mitm log vs local gateway and catalog."));
        builder.WithCommand("client-test-patch-uris-status", "Client test: URI patch status", ctx => RunPatchClientUrisAsync(paths, ctx, "Status", "Client URI patch status"), ClientCommand("Info", "Inspect IL2CPP global-metadata.dat, config.txt, and update.ini URI state."));
        builder.WithCommand("client-test-patch-uris-apply", "Patch Client URIs (Localhost)", ctx => RunPatchClientUrisAsync(paths, ctx, "Apply", "Patch Client URIs (Localhost)"), ClientCommand("Wrench", "Patch global-metadata.dat, config.txt, and update.ini to point to local gateway.", confirm: true));
        builder.WithCommand("client-test-patch-uris-reset", "Reset Client URIs (Original)", ctx => RunPatchClientUrisAsync(paths, ctx, "Reset", "Reset Client URIs (Original)"), ClientCommand("ArrowReset", "Restore global-metadata.dat, config.txt, and update.ini from .orig.bak backups.", confirm: true));

        return builder;
    }

    private static CommandOptions ClientCommand(string icon, string description, bool confirm = false) =>
        new()
        {
            Description = description,
            IconName = icon,
            ConfirmationMessage = confirm ? "This modifies local game files or stops mitmproxy. Continue?" : null,
        };

    private static Task<ExecuteCommandResult> RunGuideAsync(ClientTestingPaths paths)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            return Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Client testing guide.",
                ClientTestingReports.BuildWorkflowGuide(paths)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResults.Failure(ex.Message));
        }
    }

    private static Task<ExecuteCommandResult> RunEndpointCatalogAsync(ClientTestingPaths paths)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            var entries = ClientEndpointCatalog.Discover(paths.FeaturesDirectory);
            return Task.FromResult(OperationsCommandResults.MarkdownReport(
                $"Endpoint catalog ({entries.Count} routes).",
                ClientTestingReports.BuildEndpointCatalog(entries, paths)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResults.Failure(ex.Message));
        }
    }

    private static async Task<ExecuteCommandResult> RunScanUrlsAsync(ClientTestingPaths paths, ExecuteCommandContext context)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            Directory.CreateDirectory(paths.OutputDirectory);

            if (!File.Exists(paths.FindUrlsScript))
            {
                throw new FileNotFoundException($"Script not found: {paths.FindUrlsScript}");
            }

            var result = await ClientTestingProcessRunner.RunPowerShellScriptAsync(
                paths.FindUrlsScript,
                [
                    "-TargetPath", paths.Options.ClientInstallPath,
                    "-OutputDirectory", paths.OutputDirectory,
                ],
                paths.RepoRoot,
                context.CancellationToken);

            var markdown = ClientTestingReports.BuildScanResult(result, paths);
            return result.ExitCode == 0
                ? OperationsCommandResults.MarkdownReport("URL scan completed.", markdown)
                : CommandResults.Failure($"Scan failed with exit code {result.ExitCode}. See report output.");
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> RunPatchClientUrisAsync(
        ClientTestingPaths paths,
        ExecuteCommandContext context,
        string action,
        string title)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            if (!File.Exists(paths.PatchClientUrisScript))
            {
                throw new FileNotFoundException($"Script not found: {paths.PatchClientUrisScript}");
            }

            var result = await ClientTestingProcessRunner.RunPowerShellScriptAsync(
                paths.PatchClientUrisScript,
                [
                    "-Action", action,
                    "-ClientRoot", paths.Options.ClientInstallPath,
                    "-TargetBaseUrl", paths.Options.LocalGatewayHttpsUrl,
                ],
                paths.RepoRoot,
                context.CancellationToken);

            var markdown = ClientTestingReports.BuildReplaceResult(title, result, paths);
            return result.ExitCode == 0
                ? OperationsCommandResults.MarkdownReport($"{title} completed.", markdown)
                : CommandResults.Failure($"{title} failed with exit code {result.ExitCode}.");
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static Task<ExecuteCommandResult> RunReplacePreviewAsync(ClientTestingPaths paths, ExecuteCommandContext context) =>
        RunReplaceAsync(paths, context, dryRun: true, title: "Replace preview (dry run)");

    private static Task<ExecuteCommandResult> RunReplaceApplyAsync(ClientTestingPaths paths, ExecuteCommandContext context) =>
        RunReplaceAsync(paths, context, dryRun: false, title: "Replace apply");

    private static async Task<ExecuteCommandResult> RunReplaceAsync(
        ClientTestingPaths paths,
        ExecuteCommandContext context,
        bool dryRun,
        string title)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            EnsureMappingFile(paths);

            var latestScan = Directory.Exists(paths.OutputDirectory)
                ? Directory.GetFiles(paths.OutputDirectory, "hardcoded-url-scan-*.jsonl")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            var args = new List<string>
            {
                "-Mode", "Apply",
                "-TargetPath", paths.Options.ClientInstallPath,
                "-MappingPath", paths.MappingPath,
                "-OutputDirectory", paths.OutputDirectory,
                "-AllowShorterBinaryReplacement",
            };

            if (latestScan is not null)
            {
                args.Add("-ScanReportPath");
                args.Add(latestScan);
            }

            if (dryRun)
            {
                args.Add("-DryRun");
            }

            var result = await ClientTestingProcessRunner.RunPowerShellScriptAsync(
                paths.ReplaceUrlsScript,
                args,
                paths.RepoRoot,
                context.CancellationToken);

            var markdown = ClientTestingReports.BuildReplaceResult(title, result, paths);
            return result.ExitCode == 0
                ? OperationsCommandResults.MarkdownReport($"{title} completed.", markdown)
                : CommandResults.Failure($"{title} failed with exit code {result.ExitCode}.");
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> RunReplaceRollbackAsync(ClientTestingPaths paths, ExecuteCommandContext context)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            var manifest = Directory.Exists(paths.OutputDirectory)
                ? Directory.GetFiles(paths.OutputDirectory, "url-replacement-*.manifest.json")
                    .Where(f => !f.Contains(".dry-run.", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            if (manifest is null)
            {
                throw new FileNotFoundException(
                    $"No apply manifest in {paths.OutputDirectory}. Run client-test-replace-apply first.");
            }

            var result = await ClientTestingProcessRunner.RunPowerShellScriptAsync(
                paths.ReplaceUrlsScript,
                ["-Mode", "Rollback", "-ManifestPath", manifest],
                paths.RepoRoot,
                context.CancellationToken);

            var markdown = ClientTestingReports.BuildReplaceResult("Replace rollback", result, paths);
            return result.ExitCode == 0
                ? OperationsCommandResults.MarkdownReport("Rollback completed.", markdown)
                : CommandResults.Failure("Rollback failed.");
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> RunStartMitmAsync(ClientTestingPaths paths, ExecuteCommandContext context)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            Directory.CreateDirectory(paths.OutputDirectory);

            if (File.Exists(paths.MitmPidFile))
            {
                var existingPid = await File.ReadAllTextAsync(paths.MitmPidFile, context.CancellationToken);
                if (int.TryParse(existingPid.Trim(), out var pid) && IsProcessRunning(pid))
                {
                    return OperationsCommandResults.MarkdownReport(
                        "mitmproxy already running.",
                        ClientTestingReports.BuildMitmStartReport(null, paths, started: true));
                }
            }

            if (!File.Exists(paths.LaunchRedirectScript))
            {
                throw new FileNotFoundException(paths.LaunchRedirectScript);
            }

            if (File.Exists(paths.TrafficLogPath))
            {
                File.Delete(paths.TrafficLogPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = paths.Options.MitmProxyExecutable,
                WorkingDirectory = paths.RepoRoot,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add(paths.Options.MitmLocalMode);
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(paths.LaunchRedirectScript);
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add("launch=true");
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add($"launch_exe={paths.Options.ClientExecutablePath}");
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add(paths.Options.MitmRedirectAll ? "redirect_all=true" : "redirect=true");
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add("track_child_pids=true");
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add($"pid_log_file={paths.PidLogPath}");
            startInfo.ArgumentList.Add("--set");
            startInfo.ArgumentList.Add($"flow_log_file={paths.TrafficLogPath}");

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start mitmproxy.");

            await File.WriteAllTextAsync(paths.MitmPidFile, process.Id.ToString(), context.CancellationToken);

            return OperationsCommandResults.MarkdownReport(
                "mitmproxy started.",
                ClientTestingReports.BuildMitmStartReport(null, paths, started: true));
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static Task<ExecuteCommandResult> RunStopMitmAsync(ClientTestingPaths paths)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            if (!File.Exists(paths.MitmPidFile))
            {
                return Task.FromResult(OperationsCommandResults.MarkdownReport(
                    "No mitmproxy PID file.",
                    "mitmproxy was not started from the dashboard (or already stopped)."));
            }

            var pidText = File.ReadAllText(paths.MitmPidFile).Trim();
            if (int.TryParse(pidText, out var pid) && IsProcessRunning(pid))
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }

            File.Delete(paths.MitmPidFile);
            return Task.FromResult(OperationsCommandResults.MarkdownReport(
                "mitmproxy stopped.",
                $"Stopped process {pidText}. Run **client-test-analyze-traffic** next."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResults.Failure(ex.Message));
        }
    }

    private static Task<ExecuteCommandResult> RunAnalyzeTrafficAsync(ClientTestingPaths paths)
    {
        try
        {
            ClientTestingLocalGuard.EnsureAllowed(paths.Options);
            var entries = ClientEndpointCatalog.Discover(paths.FeaturesDirectory);
            var analysis = ClientTestingTrafficAnalyzer.Analyze(paths.TrafficLogPath, paths.Options, entries);
            return Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Traffic analysis ready.",
                ClientTestingReports.BuildTrafficAnalysis(analysis, paths, paths.Options)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResults.Failure(ex.Message));
        }
    }

    private static void EnsureMappingFile(ClientTestingPaths paths)
    {
        if (File.Exists(paths.MappingPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(paths.MappingPath)!);
        if (File.Exists(paths.MappingTemplatePath))
        {
            File.Copy(paths.MappingTemplatePath, paths.MappingPath);
            return;
        }

        File.WriteAllText(
            paths.MappingPath,
            "[\n  { \"from\": \"https://gbl3.darkorbit.com\", \"to\": \"" + paths.Options.LocalGatewayHttpsUrl + "\" }\n]");
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var _ = Process.GetProcessById(pid);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

