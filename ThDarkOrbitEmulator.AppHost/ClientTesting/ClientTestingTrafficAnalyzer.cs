using System.Text.Json;

namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal sealed record TrafficAnalysis(
    int TotalRequests,
    int LocalGatewayHits,
    int ProductionHits,
    int RedirectedHits,
    IReadOnlyList<TrafficHostSummary> ByHost,
    IReadOnlyList<TrafficPathSummary> UnmatchedProductionPaths);

internal sealed record TrafficHostSummary(string Host, int Count, bool IsLocalGateway);

internal sealed record TrafficPathSummary(string Host, string Method, string Path, int Count);

internal static class ClientTestingTrafficAnalyzer
{
    public static TrafficAnalysis Analyze(string trafficLogPath, ClientTestingOptions options, IReadOnlyList<ClientEndpointEntry> catalog)
    {
        if (!File.Exists(trafficLogPath))
        {
            throw new FileNotFoundException(
                $"Traffic log not found: {trafficLogPath}. Run client-test-start-mitm and exercise the game first.");
        }

        var gatewayHost = new Uri(options.LocalGatewayHttpsUrl).Host;
        var catalogKeys = catalog
            .Select(e => $"{e.OriginalHost.ToLowerInvariant()}|{e.Method}|{e.Path}".ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productionPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var localHits = 0;
        var productionHits = 0;
        var redirected = 0;

        foreach (var line in File.ReadLines(trafficLogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            total++;

            var host = root.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "" : "";
            var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "GET" : "GET";
            var path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "/" : "/";
            var isLocal = root.TryGetProperty("is_local_gateway", out var localEl) && localEl.GetBoolean();
            var wasRedirected = root.TryGetProperty("redirected", out var redirEl) && redirEl.GetBoolean();

            if (wasRedirected)
            {
                redirected++;
            }

            if (isLocal || host.Equals(gatewayHost, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                localHits++;
            }
            else
            {
                productionHits++;
                var key = $"{host}|{method}|{path}";
                productionPaths[key] = productionPaths.GetValueOrDefault(key) + 1;

                if (!catalogKeys.Contains(key.ToLowerInvariant()))
                {
                    productionPaths[$"__unmapped__|{key}"] = productionPaths.GetValueOrDefault($"__unmapped__|{key}") + 1;
                }
            }

            byHost[host] = byHost.GetValueOrDefault(host) + 1;
        }

        var hostSummaries = byHost
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new TrafficHostSummary(
                kv.Key,
                kv.Value,
                kv.Key.Equals(gatewayHost, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var unmatched = productionPaths
            .Where(kv => !kv.Key.StartsWith("__unmapped__|", StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var parts = kv.Key.Split('|', 3);
                return new TrafficPathSummary(parts[0], parts[1], parts[2], kv.Value);
            })
            .Take(40)
            .ToList();

        return new TrafficAnalysis(total, localHits, productionHits, redirected, hostSummaries, unmatched);
    }
}
