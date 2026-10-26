using ThDarkOrbitEmulator.AppHost.Operations;

namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal static class ClientTestingReports
{
    public static string BuildWorkflowGuide(ClientTestingPaths paths) =>
        MarkdownReportBuilder.Build("Client testing (local only)", sb =>
        {
            sb.AppendLine("On-demand workflow to point the **DarkOrbit Unity client** at your local **`gateway-players`** stack.");
            sb.AppendLine();
            sb.AppendLine("**Requirements:** Windows, game installed locally, `mitmproxy` on PATH, Aspire stack running.");
            sb.AppendLine();
            sb.AppendLine("## Steps");
            sb.AppendLine();
            sb.AppendLine("1. **`client-test-endpoints`** — catalog of hosts/paths the emulator can serve (from hardcoded-URL targets).");
            sb.AppendLine("2. **`client-test-scan-urls`** — run `Find-HardcodedUrls.ps1` on the client install (prerequisite for replace).");
            sb.AppendLine("3. Edit **`client-testing/url-replacements.json`** (copy from `url-replacements.template.json`). Byte length must not grow.");
            sb.AppendLine("4. **`client-test-replace-preview`** — dry-run `Replace-HardcodedUrls.ps1`.");
            sb.AppendLine("5. **`client-test-replace-apply`** — patch binaries (confirmation).");
            sb.AppendLine("6. **`client-test-start-mitm`** — `_reversing/Mitmproxy/launch_redirect.py` (local mode, `redirect=true` → `170.33.13.202` → gateway).");
            sb.AppendLine("7. Play the game; **`client-test-analyze-traffic`** — reads `client-traffic.jsonl` (optional log from that addon).");
            sb.AppendLine("8. Iterate URL replacements until traffic hits `localhost` / gateway ports.");
            sb.AppendLine("9. **`client-test-replace-rollback`** when done testing.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Setting", "Value"],
                [
                    ["Client path", $"`{paths.Options.ClientInstallPath}`"],
                    ["Executable", $"`{paths.Options.ClientExecutablePath}`"],
                    ["Gateway (HTTPS)", $"`{paths.Options.LocalGatewayHttpsUrl}`"],
                    ["Output dir", $"`{paths.OutputDirectory}`"],
                    ["Mapping file", $"`{paths.MappingPath}`"],
                    ["Traffic log", $"`{paths.TrafficLogPath}`"],
                    ["mitmproxy addon", $"`{paths.LaunchRedirectScript}`"],
                ]);
        });

    public static string BuildEndpointCatalog(IReadOnlyList<ClientEndpointEntry> entries, ClientTestingPaths paths)
    {
        var hosts = entries.Select(e => e.OriginalHost).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h).ToList();
        var gameCount = entries.Count(e => e.EndpointType == "game");
        var mockCount = entries.Count(e => e.EndpointType == "mock");

        return MarkdownReportBuilder.Build("Client-reachable endpoints (hardcoded URL targets)", sb =>
        {
            sb.AppendLine("Endpoints the emulator implements for hosts the Unity client may call via **hardcoded URLs**.");
            sb.AppendLine("Use this list to decide which URLs to patch with `Replace-HardcodedUrls.ps1` vs leave as **cannot change** (remove mocks later).");
            sb.AppendLine();
            sb.AppendLine($"Discovered from `{paths.FeaturesDirectory}` ({entries.Count} routes, {hosts.Count} hosts).");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Host", "Routes"],
                hosts.Select(h => new[] { h, entries.Count(e => e.OriginalHost.Equals(h, StringComparison.OrdinalIgnoreCase)).ToString() }).ToArray());
            sb.AppendLine();
            sb.AppendLine("## All routes");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Host", "Method", "Path", "Type", "Source", "Notes"],
                entries.Select(e => new[]
                {
                    e.OriginalHost,
                    e.Method,
                    e.Path,
                    e.EndpointType,
                    Path.GetFileName(e.SourceFile),
                    string.IsNullOrWhiteSpace(e.Notes) ? "" : e.Notes
                }).ToArray());
            sb.AppendLine();
            sb.AppendLine($"**Summary:** {gameCount} game routes, {mockCount} third-party mocks, {entries.Count - gameCount - mockCount} other.");
            sb.AppendLine();
            sb.AppendLine("### Suggested `url-replacements.json` hosts (game-critical)");
            sb.AppendLine();
            foreach (var host in hosts.Where(h => h.Contains("darkorbit", StringComparison.OrdinalIgnoreCase)
                                                  || h.Contains("bpsecure", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine($"- `https://{host}` → `{paths.Options.LocalGatewayHttpsUrl}` (same byte length required)");
            }
        });
    }

    public static string BuildScanResult(ProcessRunResult result, ClientTestingPaths paths)
    {
        var latestJsonl = Directory.Exists(paths.OutputDirectory)
            ? Directory.GetFiles(paths.OutputDirectory, "hardcoded-url-scan-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return MarkdownReportBuilder.Build("Hardcoded URL scan", sb =>
        {
            sb.AppendLine($"Exit code: **{result.ExitCode}** ({result.Duration.TotalSeconds:F1}s)");
            sb.AppendLine();
            if (latestJsonl is not null)
            {
                sb.AppendLine($"Latest report: `{latestJsonl}`");
            }

            sb.AppendLine();
            sb.AppendLine("### Console output");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(ClientTestingProcessRunner.TruncateForReport(result.StandardOutput));
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                sb.AppendLine(result.StandardError);
            }

            sb.AppendLine("```");
        });
    }

    public static string BuildReplaceResult(string title, ProcessRunResult result, ClientTestingPaths paths)
    {
        return MarkdownReportBuilder.Build(title, sb =>
        {
            sb.AppendLine($"Exit code: **{result.ExitCode}** ({result.Duration.TotalSeconds:F1}s)");
            sb.AppendLine($"Mapping: `{paths.MappingPath}`");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(ClientTestingProcessRunner.TruncateForReport(result.StandardOutput));
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                sb.AppendLine(result.StandardError);
            }

            sb.AppendLine("```");
        });
    }

    public static string BuildMitmStartReport(ProcessRunResult? result, ClientTestingPaths paths, bool started)
    {
        return MarkdownReportBuilder.Build("mitmproxy (local mode)", sb =>
        {
            if (started)
            {
                sb.AppendLine("mitmproxy started in the background.");
                sb.AppendLine();
                sb.AppendLine($"PID file: `{paths.MitmPidFile}`");
                sb.AppendLine($"Traffic log: `{paths.TrafficLogPath}`");
                sb.AppendLine($"PID log: `{paths.PidLogPath}`");
                sb.AppendLine();
                sb.AppendLine("Stop with **`client-test-stop-mitm`**, then run **`client-test-analyze-traffic`**.");
            }
            else if (result is not null)
            {
                sb.AppendLine($"Failed to start (exit {result.ExitCode}).");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(result.StandardError);
                sb.AppendLine(result.StandardOutput);
                sb.AppendLine("```");
            }
        });
    }

    public static string BuildTrafficAnalysis(TrafficAnalysis analysis, ClientTestingPaths paths, ClientTestingOptions options)
    {
        return MarkdownReportBuilder.Build("Traffic analysis (local vs production)", sb =>
        {
            sb.AppendLine($"Log: `{paths.TrafficLogPath}`");
            sb.AppendLine($"Local gateway: `{options.LocalGatewayHttpsUrl}`");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Metric", "Count"],
                [
                    ["Total requests", analysis.TotalRequests.ToString()],
                    ["Local gateway hits", analysis.LocalGatewayHits.ToString()],
                    ["Production / other hosts", analysis.ProductionHits.ToString()],
                    ["mitmproxy redirects applied", analysis.RedirectedHits.ToString()],
                ]);
            sb.AppendLine();
            sb.AppendLine("## By host");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Host", "Requests", "Local?"],
                analysis.ByHost.Select(h => new[]
                {
                    h.Host,
                    h.Count.ToString(),
                    h.IsLocalGateway ? "yes" : "no"
                }).ToArray());
            if (analysis.UnmatchedProductionPaths.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Production traffic (patch via url-replacements.json or enable ClientTesting:MitmRedirectAll)");
                sb.AppendLine();
                MarkdownReportBuilder.WriteTable(
                    sb,
                    ["Host", "Method", "Path", "Count"],
                    analysis.UnmatchedProductionPaths.Select(p => new[]
                    {
                        p.Host,
                        p.Method,
                        p.Path,
                        p.Count.ToString()
                    }).ToArray());
            }
            else if (analysis.ProductionHits == 0 && analysis.TotalRequests > 0)
            {
                sb.AppendLine();
                sb.AppendLine("All captured traffic went to the local gateway.");
            }
        });
    }
}
