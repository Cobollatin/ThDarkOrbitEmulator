using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// The Live links response establishes destinations, but no Support page body.
// These sections are local emulator guidance and do not submit support tickets.
public static class LocalSupport
{
    public sealed record Section(string Id, string Title, string Description);

    public static IReadOnlyList<Section> Sections { get; } =
    [
        new("getting-started", "Getting started", "Choose a company, open the Hangar, and select a ship. Help has detailed guidance for each local menu."),
        new("account", "Account and settings", "The Account page shows your local pilot information and saves emulator preferences. Your company selection is kept with your account."),
        new("connection", "Connection help", "If a local menu does not load, return to the menu and sign in again. The local emulator needs its services running to answer client requests."),
        new("community", "Community and help", "Browse the local Forum and Help pages for menu guidance. This emulator does not send posts or tickets to the official service."),
        new("payment", "Payments and vouchers", "Local payment practice packages and vouchers grant only local credits. No real payment is taken and no purchase can be refunded."),
        new("security", "Account security", "The emulator never asks for your password inside a menu. Sign out from the local menu when you finish on a shared computer."),
        new("legal", "Legal and credits", "The captured client links provide an official imprint destination and no credits destination. This local emulator is an independent project."),
        new("client-links", "Client links", "These are the six destinations the client reads from /unityApi/links.php. Local routes stay on this emulator; external ones open the official site."),
    ];

    // Emulator-defined links from a section to the Help topics that explain the same menus.
    // Sections without a matching Help topic (connection, legal, client-links) have none.
    public static IReadOnlyDictionary<string, string[]> RelatedHelpTopics { get; } = new Dictionary<string, string[]>
    {
        ["getting-started"] = ["company", "hangar"],
        ["account"] = ["account"],
        ["community"] = ["clan"],
        ["payment"] = ["shop"],
        ["security"] = ["account"],
    };

    public sealed record ClientLink(string Key, string Label, string Destination);

    // Same values and order as the captured links.php body (20261021-224636-login flow 83).
    public static IReadOnlyList<ClientLink> ClientLinks()
    {
        var links = ClientStubResponses.Links(string.Empty);
        return
        [
            new("imprint", "Imprint", links.Imprint),
            new("credits", "Credits", links.Credits),
            new("forum", "Forum", links.Forum),
            new("support", "Support", links.Support),
            new("help", "Help", links.Help),
            new("accountCenter", "Account center", links.AccountCenter),
        ];
    }

    // Sessions this close to expiry get a "sign in again soon" warning on the connection checks.
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromMinutes(10);

    public static bool IsSection(string? section) => section is null || Sections.Any(item => item.Id == section);

    public static bool Matches(Section section, string search) =>
        section.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || section.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
        || section.Id.Contains(search, StringComparison.OrdinalIgnoreCase);

    // The session details are only shown on the account and security sections and never include the dosid.
    // The connection section checks the session, company, and starter map server against "now".
    public static string Page(string? section = null, GameAuthSession? session = null, DateTimeOffset? now = null, string? search = null)
    {
        var selected = section is null
            ? Sections.Where(item => search is null || Matches(item, search)).ToList()
            : Sections.Where(item => item.Id == section).ToList();
        var current = section is null ? null : Sections.FirstOrDefault(item => item.Id == section);
        var title = current is null ? "Support - DarkOrbit local emulator" : $"{current.Title} - Support - DarkOrbit local emulator";
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(HtmlEncoder.Default.Encode(title))
            .Append("""
            </title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:850px;margin:5vh auto;padding:24px}a{color:#8cdcff}.card{background:#101e2c;border:1px solid #4c7896;border-radius:6px;padding:18px;margin:14px 0}dt{font-weight:bold;margin-top:8px}</style></head><body><main id="localSupport"
            """)
            .Append(current is null ? string.Empty : $" data-section=\"{current.Id}\"")
            .Append("><h1>Support</h1><p>Local emulator guidance</p>");
        if (section is null)
        {
            html.Append("<form id=\"supportSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalSupport\"><input type=\"search\" name=\"q\" maxlength=\"40\" aria-label=\"Search support topics\" value=\"")
                .Append(HtmlEncoder.Default.Encode(search ?? string.Empty))
                .Append("\"> <button type=\"submit\">Search</button></form>");
            if (search is null)
                html.Append("<p>").Append(Sections.Count).Append(" support topics</p>");
            else
                html.Append("<p id=\"supportSearchSummary\">").Append(selected.Count).Append(" support topics match &quot;")
                    .Append(HtmlEncoder.Default.Encode(search)).Append("&quot; · <a href=\"/indexInternal.es?action=internalSupport\">Clear search</a></p>");
            if (selected.Count == 0)
                html.Append("<p id=\"supportEmpty\">No support topics match. Try another search.</p>");
        }
        foreach (var item in selected)
        {
            html.Append("<article class=\"card\"><h2><a href=\"/indexInternal.es?action=internalSupport&amp;section=")
                .Append(item.Id).Append("\">").Append(HtmlEncoder.Default.Encode(item.Title))
                .Append("</a></h2>");
            if (section is not null)
                html.Append("<p>").Append(HtmlEncoder.Default.Encode(item.Description)).Append("</p>");
            html.Append("</article>");
        }
        if (section == "getting-started" || section == "community")
            html.Append("<p><a href=\"/indexInternal.es?action=internalHelp\">Help topics</a> · <a href=\"/indexInternal.es?action=internalForum\">Forum topics</a></p>");
        if (section is not null && RelatedHelpTopics.TryGetValue(section, out var related))
            AppendRelatedTopics(html, related);
        if (session is not null && section is "account" or "security")
            AppendSessionDetails(html, session, section == "security");
        if (session is not null && section == "connection")
            AppendConnectionChecks(html, session, now ?? DateTimeOffset.UtcNow);
        if (section == "account")
            html.Append("<p><a href=\"/indexInternal.es?action=internalAccount\">Account settings</a></p>");
        if (section == "payment")
            html.Append("<p><a href=\"/indexInternal.es?action=internalPayment\">Payment</a> · <a href=\"/indexInternal.es?action=internalVouchers\">Vouchers</a></p>");
        if (section == "security")
            html.Append("<p><a href=\"/indexInternal.es?action=externalLogout\">Log out</a></p>");
        if (section == "legal")
            html.Append("<p><a href=\"https://legal.bigpoint.com/PE/imprint/es\" rel=\"noopener noreferrer\">Official Bigpoint imprint</a></p>");
        if (section == "client-links")
            AppendClientLinks(html);
        if (current is not null)
            AppendNeighbours(html, current);
        if (section is not null)
            html.Append("<p><a href=\"/indexInternal.es?action=internalSupport\">All support topics</a></p>");
        html.Append("<p><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to menu</a></p></main></body></html>");
        return html.ToString();
    }

    private static void AppendRelatedTopics(StringBuilder html, string[] related)
    {
        html.Append("<ul id=\"supportRelatedTopics\">");
        foreach (var topic in LocalForumHelp.Topics.Where(item => related.Contains(item.Id)))
            html.Append("<li data-topic=\"").Append(topic.Id).Append("\"><a href=\"/indexInternal.es?action=internalHelp&amp;topic=")
                .Append(topic.Id).Append("\">Help: ").Append(HtmlEncoder.Default.Encode(topic.Title)).Append("</a></li>");
        html.Append("</ul>");
    }

    private static void AppendClientLinks(StringBuilder html)
    {
        html.Append("<dl id=\"supportClientLinks\">");
        foreach (var link in ClientLinks())
        {
            html.Append("<dt id=\"link-").Append(link.Key).Append("\">").Append(HtmlEncoder.Default.Encode(link.Label)).Append("</dt><dd>");
            if (link.Destination.Length == 0)
                html.Append("Not provided by the captured client links");
            else
                html.Append("<a href=\"").Append(HtmlEncoder.Default.Encode(link.Destination)).Append('"')
                    .Append(link.Destination.StartsWith('/') ? string.Empty : " rel=\"noopener noreferrer\"")
                    .Append('>').Append(HtmlEncoder.Default.Encode(link.Destination)).Append("</a>");
            html.Append("</dd>");
        }
        html.Append("</dl>");
    }

    private static void AppendSessionDetails(StringBuilder html, GameAuthSession session, bool security)
    {
        html.Append("<dl id=\"supportSession\"><dt>Pilot</dt><dd>").Append(HtmlEncoder.Default.Encode(session.Username))
            .Append("</dd><dt>Pilot ID</dt><dd>").Append(session.GameUserId.ToString(CultureInfo.InvariantCulture))
            .Append("</dd><dt>Company</dt><dd>");
        var company = LocalCompanySelection.DisplayName(session.Company);
        if (company is null)
            html.Append("Not chosen yet · <a href=\"/indexInternal.es?action=internalCompanyChoose\">Choose a company</a>");
        else
            html.Append(HtmlEncoder.Default.Encode(company));
        html.Append("</dd>");
        if (security)
            html.Append("<dt>Signed in since</dt><dd>").Append(Utc(session.CreatedAt))
                .Append("</dd><dt>Session expires</dt><dd>").Append(Utc(session.ExpiresAt)).Append("</dd>");
        html.Append("</dl>");
    }

    // Each check carries data-check and data-state ("ok", "warning", or "action") for tests and styling.
    // The server address is the same starter-map entry served as /xml/maps.xml and on the Map menu.
    private static void AppendConnectionChecks(StringBuilder html, GameAuthSession session, DateTimeOffset now)
    {
        html.Append("<ul id=\"supportConnectionChecks\">");
        var remaining = session.ExpiresAt - now;
        if (remaining <= TimeSpan.Zero)
            AppendCheck(html, "session", "action", "Your session has expired. <a href=\"/\">Sign in again</a>");
        else
        {
            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            var text = $"Session active, expires in {minutes.ToString(CultureInfo.InvariantCulture)} minute{(minutes == 1 ? "" : "s")} ({Utc(session.ExpiresAt)})";
            if (remaining <= ExpiryWarning)
                AppendCheck(html, "session", "warning", text + ". Sign in again soon.");
            else
                AppendCheck(html, "session", "ok", text);
        }

        var company = CompanyCatalog.Find(session.Company);
        if (company is null)
        {
            AppendCheck(html, "company", "action", "No company chosen yet. <a href=\"/indexInternal.es?action=internalCompanyChoose\">Choose a company</a>");
            AppendCheck(html, "game-server", "action", "A starter map and game server are assigned after you choose a company.");
        }
        else
        {
            AppendCheck(html, "company", "ok", "Company: " + HtmlEncoder.Default.Encode(company.DisplayName));
            var map = LocalMapsXml.Maps.FirstOrDefault(entry => entry.Id == company.StarterMap);
            if (map is null)
                AppendCheck(html, "game-server", "action", "Your starter map is missing from the local map catalogue.");
            else
                AppendCheck(html, "game-server", "ok",
                    $"Starter map {HtmlEncoder.Default.Encode(LocalMapOverview.MapName(map.Id))} (ID {map.Id}) on local game server " +
                    $"{HtmlEncoder.Default.Encode(map.GameServerIp)} · <a href=\"/indexInternal.es?action=internalMapOverview&amp;map={map.Id}\">Map details</a>");
        }
        html.Append("</ul>");
    }

    private static void AppendCheck(StringBuilder html, string check, string state, string innerHtml) =>
        html.Append("<li data-check=\"").Append(check).Append("\" data-state=\"").Append(state).Append("\">")
            .Append(innerHtml).Append("</li>");

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static void AppendNeighbours(StringBuilder html, Section current)
    {
        var index = Sections.ToList().IndexOf(current);
        html.Append("<nav id=\"supportNeighbours\"><p>");
        if (index > 0)
            AppendSectionLink(html, Sections[index - 1], "previous", "Previous: ");
        if (index > 0 && index < Sections.Count - 1)
            html.Append(" · ");
        if (index < Sections.Count - 1)
            AppendSectionLink(html, Sections[index + 1], "next", "Next: ");
        html.Append("</p></nav>");
    }

    private static void AppendSectionLink(StringBuilder html, Section target, string rel, string prefix) =>
        html.Append("<a rel=\"").Append(rel).Append("\" href=\"/indexInternal.es?action=internalSupport&amp;section=")
            .Append(target.Id).Append("\">").Append(prefix).Append(HtmlEncoder.Default.Encode(target.Title)).Append("</a>");
}
