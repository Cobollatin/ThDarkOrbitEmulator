using System.Text.RegularExpressions;

namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal sealed record ClientEndpointEntry(
    string OriginalHost,
    string Method,
    string Path,
    string ApiRoute,
    string SourceFile,
    string EndpointType,
    bool ClientReplaceable,
    string Notes);

internal static partial class ClientEndpointCatalog
{
    [GeneratedRegex(@"//\s*DNS:\s*(.+?)\s*\|\s*(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+([^\|\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DnsCommentRegex();

    [GeneratedRegex(@"DNS:([A-Za-z0-9.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DnsTokenRegex();

    [GeneratedRegex(@"\b(Get|Post|Put|Delete|Patch|Head|Options)\(""(/[^""]+)""\)", RegexOptions.IgnoreCase)]
    private static partial Regex RouteRegex();

    private static readonly (string Host, string Method, string Path, string Type, string Notes)[] ManualEntries =
    [
        ("gbl3.darkorbit.com", "GET", "/unityApi/login.php", "game", "Unity login (no DNS comment in source)"),
        ("gbl3.darkorbit.com", "GET", "/api/v1/identity/me", "cms", "Authenticated profile via gateway"),
        ("gbl3.darkorbit.com", "POST", "/api/v1/identity/login", "cms", "CMS login via gateway"),
        ("gbl3.darkorbit.com", "POST", "/api/v1/identity/register", "cms", "CMS register via gateway"),
    ];

    public static IReadOnlyList<ClientEndpointEntry> Discover(string featuresDirectory)
    {
        if (!Directory.Exists(featuresDirectory))
        {
            throw new DirectoryNotFoundException($"ApiService features path not found: {featuresDirectory}");
        }

        var entries = new List<ClientEndpointEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(featuresDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var relativeFile = file.Replace('\\', '/');

            foreach (Match commentMatch in DnsCommentRegex().Matches(text))
            {
                var dnsPart = commentMatch.Groups[1].Value;
                var method = commentMatch.Groups[2].Value.ToUpperInvariant();
                var path = commentMatch.Groups[3].Value.Trim();

                foreach (var host in ExtractHosts(dnsPart))
                {
                    AddEntry(entries, seen, host, method, path, relativeFile, InferType(path), notes: "");
                }
            }

            var routeMatches = RouteRegex().Matches(text);
            if (routeMatches.Count == 0)
            {
                continue;
            }

            var primaryRoute = routeMatches[0].Groups[2].Value;
            var primaryMethod = routeMatches[0].Groups[1].Value.ToUpperInvariant();
            var dnsHosts = DnsTokenRegex().Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dnsHosts.Count == 0)
            {
                continue;
            }

            foreach (var host in dnsHosts)
            {
                if (DnsCommentRegex().IsMatch(text))
                {
                    continue;
                }

                AddEntry(entries, seen, host, primaryMethod, primaryRoute, relativeFile, InferType(primaryRoute), "Inferred from WithTags DNS");
            }
        }

        foreach (var manual in ManualEntries)
        {
            AddEntry(entries, seen, manual.Host, manual.Method, manual.Path, "manual", manual.Type, manual.Notes);
        }

        return entries
            .OrderBy(e => e.OriginalHost, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ExtractHosts(string dnsPart)
    {
        var hosts = new List<string>();
        foreach (Match match in DnsTokenRegex().Matches("DNS:" + dnsPart))
        {
            var host = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.Add(host);
            }
        }

        if (hosts.Count == 0 && !dnsPart.Contains('|', StringComparison.Ordinal))
        {
            hosts.Add(dnsPart.Trim());
        }

        return hosts.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddEntry(
        List<ClientEndpointEntry> entries,
        HashSet<string> seen,
        string host,
        string method,
        string path,
        string sourceFile,
        string endpointType,
        string notes)
    {
        var key = $"{host}|{method}|{path}";
        if (!seen.Add(key))
        {
            return;
        }

        entries.Add(new ClientEndpointEntry(
            OriginalHost: host,
            Method: method,
            Path: path,
            ApiRoute: path,
            SourceFile: sourceFile,
            EndpointType: endpointType,
            ClientReplaceable: true,
            Notes: notes));
    }

    private static string InferType(string path)
    {
        if (path.StartsWith("/unityApi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/flashAPI", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/index", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/ProjectApi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Sas/", StringComparison.OrdinalIgnoreCase))
        {
            return "game";
        }

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return "api";
        }

        return "mock";
    }
}
