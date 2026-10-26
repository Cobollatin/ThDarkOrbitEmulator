using System.Text;
using System.Globalization;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexEs;

// The client contains the externalDownload URL, but no response or installer
// contract was captured. This page only connects an existing client to local menus.
public static class LocalLauncherDownload
{
    public const string Action = "externalDownload";
    public const int MaxValueLength = 64;

    // "from", "prc", and "lang" are tolerated because other local menu links carry them.
    private static readonly string[] OptionalKeys = ["from", "prc", "lang", "section"];

    public sealed record Result(int Status, string Html);

    public sealed record Section(string Id, string Title);

    // Emulator-defined sections; "overview" is also served when no section is given.
    public static IReadOnlyList<Section> Sections { get; } =
    [
        new("overview", "Overview"),
        new("requirements", "Requirements"),
        new("connection", "Connection"),
        new("troubleshooting", "Troubleshooting"),
    ];

    // Unity requests observed in the Live and Redirect captures after sign-in.
    public static IReadOnlyList<string> CapturedClientPaths { get; } =
    [
        "/unityApi/login.php",
        "/unityApi/links.php",
        "/unityApi/news.php",
        "/flashAPI/brNews.php",
    ];

    private const string Head = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>
        """;

    private const string Style = """
         - DarkOrbit local emulator</title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:850px;margin:6vh auto;padding:24px}a{color:#8cdcff}.card,#launcherSession{background:#101e2c;border:1px solid #4c7896;border-radius:6px;padding:18px;margin:14px 0}nav a{margin-right:14px}nav a[aria-current]{color:#e3f2fc;font-weight:bold}</style></head><body>
        """;

    private const string OverviewBody = """
        <h1>Client and launcher</h1><p>This local emulator works with a separately installed DarkOrbit Unity client. It does not distribute an installer or update the client.</p><section class="card"><h2>Already have the client?</h2><p>Start your installed client using the project’s Redirect capture mode. Once signed in, choose a company and open the local menu.</p><p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Open local menu</a></p></section><section class="card"><h2>Need help connecting?</h2><p>The local Help page covers sign-in and menu navigation.</p><p><a href="/indexInternal.es?action=internalHelp">Open Help</a></p></section>
        """;

    private const string RequirementsBody = """
        <h1>Requirements</h1><section class="card"><h2>What you need</h2><ul id="launcherRequirements"><li>A DarkOrbit Unity client that is already installed. The emulator does not provide one.</li><li>The local emulator, started from the AppHost.</li><li>The project’s Redirect capture mode, which sends the client’s requests to this emulator.</li><li>A local sign-in, which creates the session the local menus use.</li></ul></section><p>The emulator wrote these requirements. They are not the official system requirements.</p>
        """;

    private const string TroubleshootingBody = """
        <h1>Troubleshooting</h1><section class="card"><h2>Common problems</h2><dl id="launcherTroubleshooting"><dt>A menu says your session expired</dt><dd>Sign in again from the start page. Local sessions end at logout or when the cache expires.</dd><dt>The client asks you to choose a company</dt><dd>Choose one company. The choice is saved and the local menu opens afterwards.</dd><dt>The client reaches the official servers</dt><dd>Restart the client in Redirect mode so its requests reach this emulator.</dd></dl></section><p><a href="/indexInternal.es?action=internalSupport">Open Support</a> · <a href="/indexInternal.es?action=internalHelp">Open Help</a></p>
        """;

    // The signed-out page; a live session only changes the sign-in card.
    public static readonly string Page = Render(Sections[0], null);

    // True when any action value names this page, in any casing, so tampered links reach
    // Resolve and get a 400 instead of the wildcard sign-in capture (which mints a dosid).
    public static bool Targets(IQueryCollection query) =>
        query.Any(pair => string.Equals(pair.Key, "action", StringComparison.OrdinalIgnoreCase)
            && pair.Value.Any(value => string.Equals(value, Action, StringComparison.OrdinalIgnoreCase)));

    // Validates the query first; the session is only read for a request that would return 200,
    // so malformed links never cost a cache lookup. Missing, unknown or expired dosids read as signed out.
    public static async Task<Result> ResolveAsync(IQueryCollection query, string? dosid,
        IGameAuthSessionStore sessions, CancellationToken ct)
    {
        var result = Resolve(query);
        if (result.Status != StatusCodes.Status200OK || string.IsNullOrWhiteSpace(dosid) || dosid.Length > 128)
            return result;
        var session = await sessions.TryGetByDosidAsync(dosid, ct);
        return session is null ? result : Resolve(query, session);
    }

    public static Result Resolve(IQueryCollection query, GameAuthSession? session = null)
    {
        foreach (var (key, values) in query)
        {
            if (key != "action" && !OptionalKeys.Contains(key, StringComparer.Ordinal))
                return Error(StatusCodes.Status400BadRequest, "Unknown parameter. Open this page from the local menu.");
            if (values.Count != 1)
                return Error(StatusCodes.Status400BadRequest, "Each parameter must appear once.");
            var value = values.ToString();
            if (value.Length > MaxValueLength || value.Any(char.IsControl))
                return Error(StatusCodes.Status400BadRequest, $"Parameter values must be at most {MaxValueLength} printable characters.");
        }
        if (query["action"].ToString() != Action)
            return Error(StatusCodes.Status400BadRequest, "Invalid download action.");
        if (!query.TryGetValue("section", out var sectionValue))
            return new(StatusCodes.Status200OK, session is null ? Page : Render(Sections[0], session));

        var sectionId = sectionValue.ToString();
        if (sectionId.Length == 0)
            return Error(StatusCodes.Status400BadRequest, "Section must not be empty.");
        // Exact, case-sensitive ids keep one canonical URL per section.
        var section = Sections.FirstOrDefault(entry => entry.Id == sectionId);
        if (section is null)
            return Error(StatusCodes.Status404NotFound, "Unknown section. Choose one from the Client and launcher page.");
        return new(StatusCodes.Status200OK, section == Sections[0] && session is null ? Page : Render(section, session));
    }

    public static string SectionHref(string sectionId) =>
        sectionId == Sections[0].Id ? $"/index.es?action={Action}" : $"/index.es?action={Action}&section={sectionId}";

    private static string Render(Section current, GameAuthSession? session)
    {
        var html = new StringBuilder(Head)
            .Append(current == Sections[0] ? "Client and launcher" : $"{current.Title} - Client and launcher")
            .Append(Style)
            .Append("<main id=\"localLauncherDownload\" data-section=\"").Append(current.Id).Append("\">")
            .Append("<nav id=\"launcherSections\" aria-label=\"Client and launcher sections\">");
        foreach (var section in Sections)
        {
            html.Append("<a href=\"").Append(HtmlEncoder.Default.Encode(SectionHref(section.Id))).Append('"')
                .Append(section == current ? " aria-current=\"page\"" : string.Empty)
                .Append('>').Append(section.Title).Append("</a>");
        }
        html.Append("</nav>").Append(current.Id switch
        {
            "requirements" => RequirementsBody,
            "connection" => ConnectionBody(),
            "troubleshooting" => TroubleshootingBody,
            _ => OverviewBody,
        });
        return html.Append(SessionCard(session))
            .Append("<p><a href=\"/\">Back to sign-in</a></p></main></body></html>").ToString();
    }

    // Emulator-defined sign-in summary. A stored company that is not in the catalogue counts as
    // no company, matching the menus, which send such sessions back to company choice.
    private static string SessionCard(GameAuthSession? session)
    {
        if (session is null)
        {
            return "<aside id=\"launcherSession\" data-state=\"signed-out\"><h2>Your local sign-in</h2>" +
                "<p>You are not signed in to this emulator. Sign in before opening the local menus.</p>" +
                "<p><a href=\"/\">Sign in</a></p></aside>";
        }

        var pilot = new StringBuilder("Signed in as <strong>").Append(HtmlEncoder.Default.Encode(session.Username))
            .Append("</strong> (game ID ").Append(session.GameUserId.ToString(CultureInfo.InvariantCulture)).Append(')');
        var expires = session.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var company = LocalCompanySelection.DisplayName(session.Company);
        if (company is null)
        {
            return "<aside id=\"launcherSession\" data-state=\"no-company\"><h2>Your local sign-in</h2><p>" + pilot +
                ". Choose a company before opening the local menu.</p><p>Session valid until " + expires + " UTC.</p>" +
                "<p><a href=\"/indexInternal.es?action=internalCompanyChoose\">Choose a company</a></p></aside>";
        }

        return "<aside id=\"launcherSession\" data-state=\"ready\"><h2>Your local sign-in</h2><p>" + pilot +
            ", company <strong>" + HtmlEncoder.Default.Encode(company) + "</strong>.</p><p>Session valid until " + expires + " UTC.</p>" +
            "<p><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Open local menu</a></p></aside>";
    }

    // Uses the catalogue behind /xml/maps.php so the listed addresses cannot drift from it.
    private static string ConnectionBody()
    {
        var html = new StringBuilder("<h1>Connection</h1><section class=\"card\"><h2>Client requests</h2><p>After sign-in, the client loads these paths from this emulator:</p><ul id=\"launcherClientPaths\">");
        foreach (var path in CapturedClientPaths)
            html.Append("<li><code>").Append(HtmlEncoder.Default.Encode(path)).Append("</code></li>");
        html.Append("</ul></section><section class=\"card\"><h2>Game servers</h2><ul id=\"launcherGameServers\">");
        foreach (var server in LocalMapsXml.Maps.GroupBy(map => map.GameServerIp, StringComparer.Ordinal))
        {
            var address = HtmlEncoder.Default.Encode(server.Key);
            html.Append("<li data-server=\"").Append(address).Append("\"><code>").Append(address)
                .Append("</code>, starter maps ")
                .Append(HtmlEncoder.Default.Encode(string.Join(", ", server.Select(map => map.Id)))).Append("</li>");
        }
        return html.Append("</ul><p>These addresses are listed for reference. This page does not connect you to a map.</p>")
            .Append("<p><a href=\"/indexInternal.es?action=internalMapOverview\">Open Map and server</a></p></section>")
            .ToString();
    }

    public static async Task WriteAsync(HttpResponse response, Result result, CancellationToken ct)
    {
        response.StatusCode = result.Status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync(
            Features.IndexInternal.InternalStart.IndexInternalStartEndpoint.AdaptHtmlForClient(
                response.HttpContext.Request, result.Html), ct);
    }

    public static Result Error(int status, string message) =>
        new(status,
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Client and launcher error</title></head>" +
            $"<body><main id=\"localLauncherDownloadError\"><h1>Client and launcher request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            $"<a href=\"/index.es?action={Action}\">Back to Client and launcher</a></main></body></html>");
}
