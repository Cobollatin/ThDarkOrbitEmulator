using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local menu for the client map catalogue. No official post-selection page was captured.
public static class LocalMapOverview
{
    public sealed record Result(int Status, string Html);

    private static readonly string[] AllowedKeys = ["action", "map", "company", "from", "prc", "lang"];

    // Validates the query before rendering: only known keys, each at most once, and an
    // optional map id that must be one of the catalogue entries served as /xml/maps.xml.
    public static Result Resolve(IQueryCollection query, GameAuthSession session)
    {
        if (Validate(query) is { } invalid)
            return invalid;
        var filterCode = query["company"].ToString();
        CompanyDefinition? filter = null;
        if (filterCode.Length > 0 && (filter = CompanyCatalog.Find(filterCode)) is null)
            return Error(StatusCodes.Status404NotFound, "Unknown company.");
        var mapId = query["map"].ToString();
        if (mapId.Length == 0)
            return new Result(StatusCodes.Status200OK, Page(session, null, filter));
        var map = LocalMapsXml.Maps.FirstOrDefault(entry => entry.Id == mapId);
        if (map is null)
            return Error(StatusCodes.Status404NotFound, "Unknown map.");
        // Like a forum topic outside its category: a map the filtered company does not start on is not found.
        return filter is not null && filter.StarterMap != map.Id
            ? Error(StatusCodes.Status404NotFound, "Unknown map for this company.")
            : new Result(StatusCodes.Status200OK, Page(session, map, filter));
    }

    // Session-independent 400 checks, run by the endpoint before the session lookup (menu-71).
    // Returns null when the query is well formed; an unknown but well-formed map id is left to Resolve (404).
    public static Result? Validate(IQueryCollection query)
    {
        // Menu-189: keys match exactly (MAP=, Company=, ACTION= are 400), like Logbook and Rankings, and
        // messages name only the canonical key so a rejected query is never echoed.
        foreach (var (key, values) in query)
        {
            if (!AllowedKeys.Contains(key, StringComparer.Ordinal))
                return Error(StatusCodes.Status400BadRequest, "Unknown map overview parameter.");
            if (values.Count != 1)
                return Error(StatusCodes.Status400BadRequest, $"Map overview parameter '{key}' must appear once.");
            if (values.ToString().Length > 64)
                return Error(StatusCodes.Status400BadRequest, $"Map overview parameter '{key}' is too long.");
            if (values.ToString().Any(char.IsControl))
                return Error(StatusCodes.Status400BadRequest, $"Map overview parameter '{key}' contains control characters.");
        }
        // The endpoint dispatches the action case-insensitively; only the canonical value is served.
        if (query.ContainsKey("action") && query["action"].ToString() != "internalMapOverview")
            return Error(StatusCodes.Status400BadRequest, "Invalid map overview action.");
        var mapId = query["map"].ToString();
        if (mapId.Length > 0 && (mapId.Length > 4 || !mapId.All(char.IsAsciiDigit) || mapId[0] == '0'))
            return Error(StatusCodes.Status400BadRequest, "Map id is malformed.");
        // Company codes are short lowercase ASCII ids (mmo, eic, vru, tst); unknown well-formed codes are 404 in Resolve.
        var company = query["company"].ToString();
        if (company.Length > 0 && (company.Length > 8 || !company.All(char.IsAsciiLetterLower)))
            return Error(StatusCodes.Status400BadRequest, "Company filter is malformed.");
        return null;
    }

    public static Result Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, "Your session is missing or expired. Sign in again.");

    // Starter maps follow the client naming "{faction}-1"; the home company is the
    // lowest numeric faction that starts there (the test company shares map 1).
    public static string MapName(string mapId) =>
        HomeCompany(mapId) is { } company ? $"{company.NumericFactionId}-1" : $"Map {mapId}";

    private static CompanyDefinition? HomeCompany(string mapId) => CompanyCatalog.Companies
        .Where(company => company.StarterMap == mapId)
        .OrderBy(company => company.NumericFactionId)
        .FirstOrDefault();

    public static string Page(GameAuthSession session) => Page(session, null, null);

    private static IEnumerable<CompanyDefinition> Starters(string mapId) =>
        CompanyCatalog.Companies.Where(company => company.StarterMap == mapId);

    private static string Page(GameAuthSession session, LocalMapsXml.MapEntry? selected, CompanyDefinition? filter)
    {
        var company = CompanyCatalog.Find(session.Company);
        var title = selected is null
            ? "Map and server"
            : $"Map {MapName(selected.Id)} (ID {selected.Id})";
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>
            """).Append(HtmlEncoder.Default.Encode(title)).Append("""
             - DarkOrbit local emulator</title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:850px;margin:5vh auto;padding:24px}a{color:#8cdcff}.card{background:#101e2c;border:1px solid #4c7896;border-radius:6px;padding:18px;margin:14px 0}.card.current{border-color:#8cdcff}</style></head><body><main id="localMapOverview"><h1>Map and server</h1><p>Local emulator map catalogue</p>
            """);
        if (company is not null)
        {
            html.Append("<p id=\"currentMap\">Your company: ")
                .Append(HtmlEncoder.Default.Encode(company.DisplayName))
                .Append(" · starter map ID ").Append(HtmlEncoder.Default.Encode(company.StarterMap))
                .Append(" (").Append(HtmlEncoder.Default.Encode(MapName(company.StarterMap))).Append(")</p>");
        }

        IReadOnlyList<LocalMapsXml.MapEntry> maps = selected is not null ? [selected]
            : filter is not null ? LocalMapsXml.Maps.Where(map => map.Id == filter.StarterMap).ToArray()
            : LocalMapsXml.Maps;
        if (selected is null)
        {
            AppendCompanyTabs(html, filter);
            html.Append("<p id=\"mapCount\">").Append(maps.Count).Append(maps.Count == 1 ? " map" : " maps");
            if (filter is null)
                html.Append(" in the local catalogue</p>");
            else
                html.Append(" where ").Append(HtmlEncoder.Default.Encode(filter.DisplayName)).Append(" starts</p>");
        }
        foreach (var map in maps)
        {
            var isCurrent = company?.StarterMap == map.Id;
            var id = HtmlEncoder.Default.Encode(map.Id);
            html.Append("<article class=\"card").Append(isCurrent ? " current" : string.Empty)
                .Append("\" id=\"map-").Append(id).Append("\" data-map=\"").Append(id).Append("\"><h2>Map ID ").Append(id)
                .Append(" · ").Append(HtmlEncoder.Default.Encode(MapName(map.Id))).Append("</h2>");
            if (isCurrent)
                html.Append("<p class=\"starter\">Your starter map</p>");
            html.Append("<p>Local game server: ").Append(HtmlEncoder.Default.Encode(map.GameServerIp)).Append("</p>");
            var starterCount = Starters(map.Id).Count();
            html.Append("<p class=\"starterCount\" data-starters=\"").Append(starterCount).Append("\">Starting companies: ")
                .Append(starterCount).Append("</p>");
            if (selected is null)
            {
                html.Append("<p><a href=\"/indexInternal.es?action=internalMapOverview&amp;map=").Append(id);
                if (filter is not null)
                    html.Append("&amp;company=").Append(HtmlEncoder.Default.Encode(filter.Code));
                html.Append("\">Details</a></p>");
            }
            else
            {
                html.Append("<p>Home company: ")
                    .Append(HtmlEncoder.Default.Encode(HomeCompany(map.Id)?.DisplayName ?? "None"))
                    .Append("</p><ul class=\"starters\">");
                foreach (var starter in Starters(map.Id))
                    html.Append("<li>").Append(HtmlEncoder.Default.Encode(starter.DisplayName)).Append("</li>");
                html.Append("</ul>");
                // Ships wait in the hangar on the starter map; this menu never enters the world.
                if (isCurrent)
                    html.Append("<p><a href=\"/indexInternal.es?action=internalDock\">Open Hangar</a></p>");
            }
            html.Append("</article>");
        }
        if (selected is not null)
        {
            if (filter is null)
            {
                html.Append("<nav class=\"mapNav\">");
                AppendNeighbour(html, selected, -1, "Previous");
                AppendNeighbour(html, selected, 1, "Next");
                html.Append("</nav>");
            }
            html.Append("<p><a href=\"/indexInternal.es?action=internalMapOverview");
            if (filter is not null)
                html.Append("&amp;company=").Append(HtmlEncoder.Default.Encode(filter.Code));
            html.Append("\">").Append(filter is null ? "All maps" : "Back to company maps").Append("</a></p>");
        }
        html.Append("<p><a href=\"/xml/maps.xml\">Map XML</a> · <a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to menu</a></p></main></body></html>");
        return html.ToString();
    }

    // Company tabs on the overview: "All (n)" plus one tab per company with its starter map count;
    // the active tab is marked with aria-current.
    private static void AppendCompanyTabs(StringBuilder html, CompanyDefinition? filter)
    {
        html.Append("<nav class=\"companyTabs\">");
        AppendTab(html, "All", LocalMapsXml.Maps.Count, null, filter is null);
        foreach (var company in CompanyCatalog.Companies)
        {
            var count = LocalMapsXml.Maps.Count(map => map.Id == company.StarterMap);
            AppendTab(html, company.Code.ToUpperInvariant(), count, company.Code, filter?.Code == company.Code);
        }
        html.Append("</nav>");
    }

    private static void AppendTab(StringBuilder html, string label, int count, string? code, bool current)
    {
        html.Append("<a href=\"/indexInternal.es?action=internalMapOverview");
        if (code is not null)
            html.Append("&amp;company=").Append(HtmlEncoder.Default.Encode(code));
        html.Append('"');
        if (current)
            html.Append(" aria-current=\"page\"");
        html.Append('>').Append(HtmlEncoder.Default.Encode(label)).Append(" (").Append(count).Append(")</a> ");
    }

    // Previous/next links follow catalogue order and stop at the first and last map.
    private static void AppendNeighbour(StringBuilder html, LocalMapsXml.MapEntry selected, int offset, string label)
    {
        var maps = LocalMapsXml.Maps;
        var index = -1;
        for (var i = 0; i < maps.Count; i++)
            if (maps[i].Id == selected.Id)
                index = i;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= maps.Count)
            return;
        var id = HtmlEncoder.Default.Encode(maps[target].Id);
        html.Append("<a rel=\"").Append(offset < 0 ? "prev" : "next")
            .Append("\" href=\"/indexInternal.es?action=internalMapOverview&amp;map=").Append(id).Append("\">")
            .Append(label).Append(": ").Append(HtmlEncoder.Default.Encode(MapName(maps[target].Id))).Append("</a> ");
    }

    private static Result Error(int status, string message) => new(status,
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Map overview error</title></head>" +
        $"<body><main id=\"mapOverviewError\"><h1>Map overview request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
        (status == StatusCodes.Status401Unauthorized
            ? "<a href=\"/\">Sign in</a>"
            : "<a href=\"/indexInternal.es?action=internalMapOverview\">Back to map overview</a>") +
        "</main></body></html>");
}
