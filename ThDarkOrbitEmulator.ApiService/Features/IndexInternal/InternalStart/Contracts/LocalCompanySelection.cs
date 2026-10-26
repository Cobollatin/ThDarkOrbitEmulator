using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

// Local-only progression while the official company confirmation contract is unknown.
public static class LocalCompanySelection
{
    public static bool IsValid(string? company) => CompanyCatalog.Find(company) is not null;

    public static string? Faction(string? company) => CompanyCatalog.Find(company)?.Code;

    public static string? StarterMap(string? company) => CompanyCatalog.Find(company)?.StarterMap;

    public static string? DisplayName(string? company) => CompanyCatalog.Find(company)?.DisplayName;

    // Emulator-defined preview text; the Live choice page only showed the three confirm controls.
    private static readonly IReadOnlyDictionary<string, string> Summaries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mmo"] = "Mining company based in the red sector. Pilots start on its home map.",
        ["eic"] = "Industrial company based in the blue sector. Pilots start on its home map.",
        ["vru"] = "Resource company based in the green sector. Pilots start on its home map.",
        ["tst"] = "Local test company for emulator checks. It uses its own starter map."
    };

    public static string PreviewHref(string company) => $"/indexInternal.es?action=internalCompanyChoose&amp;preview={company}";

    private static string ChoiceForms(string? preview) => string.Join("\n", CompanyCatalog.Companies.Select(company =>
        $"<form class=\"company{(company.Code == preview ? " selected" : "")}\" data-company=\"{company.Code}\"{(company.Code == preview ? " aria-current=\"true\"" : "")} method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalCompanyConfirm\"><input type=\"hidden\" name=\"company\" value=\"{company.Code}\"><h2>{company.DisplayName}</h2><p class=\"details\" id=\"details_{company.Code}\">Faction {company.NumericFactionId} &middot; starter map {company.StarterMap}{(company.Code == "tst" ? " &middot; local test company" : "")}</p><p><a class=\"preview\" id=\"preview_{company.Code}\" href=\"{PreviewHref(company.Code)}\">Details</a></p><button class=\"confirm\" id=\"confirm_{company.Code}\" type=\"submit\">Choose {company.Code.ToUpperInvariant()}</button></form>"));

    // The game server comes from the catalogue behind /xml/maps.php so both pages agree.
    private static string PreviewPanel(CompanyDefinition company)
    {
        var server = LocalMapsXml.Maps.FirstOrDefault(map => map.Id == company.StarterMap)?.GameServerIp ?? "unknown";
        var sharing = CompanyCatalog.Companies
            .Where(other => other.Code != company.Code && other.StarterMap == company.StarterMap)
            .Select(other => other.Code.ToUpperInvariant())
            .ToArray();
        var shared = sharing.Length == 0 ? "none" : string.Join(", ", sharing);
        return $"<section id=\"companyPreview\" data-company=\"{company.Code}\"><h2>{company.DisplayName}</h2><p id=\"companySummary\">{Summaries[company.Code]}</p><dl id=\"companyFacts\"><dt>Code</dt><dd>{company.Code.ToUpperInvariant()}</dd><dt>Faction</dt><dd>{company.NumericFactionId}</dd><dt>Starter map</dt><dd>{company.StarterMap}</dd><dt>Game server</dt><dd><code>{server}</code></dd><dt>Shares starter map with</dt><dd>{shared}</dd></dl><p>Choosing is permanent. Previewing does not save anything.</p><form method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalCompanyConfirm\"><input type=\"hidden\" name=\"company\" value=\"{company.Code}\"><button class=\"confirm\" id=\"previewConfirm\" type=\"submit\">Choose {company.Code.ToUpperInvariant()}</button></form><p><a id=\"previewClose\" href=\"/indexInternal.es?action=internalCompanyChoose\">Close details</a></p></section>";
    }

    public static string ChoicePage => ChoicePageFor(null);

    // `preview` must be a catalog code or null; callers validate it first.
    public static string ChoicePageFor(string? preview)
    {
        var selected = CompanyCatalog.Find(preview);
        return $$"""
        <!doctype html><html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Choose a company</title>
        <style>body{margin:0;background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:1000px;margin:8vh auto;padding:32px;text-align:center}h1{letter-spacing:.12em}a{color:#8cdcff}#companySelection{display:flex;gap:24px;justify-content:center;flex-wrap:wrap;margin-top:48px}.company{width:260px;min-height:180px;background:#101e2c;border:1px solid #4c7896;padding:20px;color:inherit}.company.selected{border-color:#8cdcff}.company h2{color:#8cdcff}#companyPreview{margin:32px auto 0;max-width:640px;background:#101e2c;border:1px solid #8cdcff;padding:20px;text-align:left}#companyFacts{display:grid;grid-template-columns:auto 1fr;gap:6px 16px}#companyFacts dd{margin:0}.confirm{padding:14px 28px;background:#1789b5;border:0;color:white;font-size:18px;cursor:pointer}</style></head>
        <body><main><h1>Choose a company</h1><p id="companyNotice">The choice is permanent for this pilot and sets the starter map and hangar location.</p><div id="companySelection">
        {{ChoiceForms(selected?.Code)}}
        </div>{{(selected is null ? "" : PreviewPanel(selected))}}</main></body></html>
        """;
    }

    // Emulator-defined grouping of the local menus; the official shell layout is uncaptured.
    public static readonly IReadOnlyList<(string Id, string Title, IReadOnlyList<(string Href, string Label)> Links)> MenuSections =
    [
        ("hangar", "Hangar and economy", [
            ("/indexInternal.es?action=internalDock", "Hangar"), ("/indexInternal.es?action=internalShop", "Shop"),
            ("/indexInternal.es?action=internalAssembly", "Assembly"), ("/indexInternal.es?action=internalSkylab", "Skylab"),
            ("/indexInternal.es?action=internalTechFactory", "Tech Factory"), ("/indexInternal.es?action=internalAuction", "Auction")]),
        ("activities", "Activities", [
            ("/indexInternal.es?action=internalGalaxyGates", "Galaxy Gates"), ("/indexInternal.es?action=internalEvents", "Events"),
            ("/indexInternal.es?action=internalSpaceSearch", "Space Search"), ("/indexInternal.es?action=internalQuests", "Quests")]),
        ("pilot", "Pilot and community", [
            ("/indexInternal.es?action=internalPilotSheet", "Pilot Sheet"), ("/indexInternal.es?action=internalClan", "Clan"),
            ("/indexInternal.es?action=internalMessages", "Messages"), ("/indexInternal.es?action=internalHallofFame", "Hall of Fame"),
            ("/indexInternal.es?action=internalLogbook", "Logbook")]),
        ("info", "News and information", [
            ("/indexInternal.es?action=internalCommandCenter", "Command center"), ("/indexInternal.es?action=internalMapOverview", "Map and server"),
            ("/indexInternal.es?action=internalForum", "Forum"), ("/indexInternal.es?action=internalHelp", "Help"),
            ("/indexInternal.es?action=internalSupport", "Support")]),
        ("account", "Account", [
            ("/indexInternal.es?action=internalAccount", "Account"), ("/indexInternal.es?action=internalPayment", "Payment"),
            ("/indexInternal.es?action=internalVouchers", "Vouchers"), ("/index.es?action=externalDownload", "Client and launcher"),
            ("/indexInternal.es?action=externalLogout", "Log out")])
    ];

    public static int MenuLinkCount => MenuSections.Sum(section => section.Links.Count);

    private static string MenuNav() => string.Concat(MenuSections.Select(section =>
        $"<section class=\"menuSection\" id=\"menu_{section.Id}\" data-count=\"{section.Links.Count}\"><h2>{section.Title}</h2>"
        + string.Concat(section.Links.Select(link => $"<a href=\"{link.Href}\">{link.Label}</a>")) + "</section>"));

    // The pilot card reads only the session and the stored company; the name is HTML-encoded.
    private static string PilotCard(CompanyDefinition company, string? pilotName, long? gameUserId)
    {
        var name = string.IsNullOrWhiteSpace(pilotName) ? "Pilot" : System.Text.Encodings.Web.HtmlEncoder.Default.Encode(pilotName);
        var id = gameUserId is > 0 ? $"<dt>Game ID</dt><dd id=\"pilotGameId\">{gameUserId}</dd>" : "";
        var mapName = $"{company.NumericFactionId}-1";
        return $"<section id=\"pilotCard\" data-company=\"{company.Code}\"><h2 id=\"pilotName\">{name}</h2><dl>{id}<dt>Company</dt><dd id=\"pilotCompany\">{company.Code.ToUpperInvariant()}</dd><dt>Home map</dt><dd id=\"pilotHomeMap\"><a href=\"/indexInternal.es?action=internalMapOverview&amp;map={company.StarterMap}\">{mapName} (map {company.StarterMap})</a></dd></dl></section>";
    }

    public static string GamePage(string company, string? pilotName = null, long? gameUserId = null)
    {
        var definition = CompanyCatalog.Find(company)!;
        return $$"""
        <!doctype html><html lang="es"><head><meta charset="utf-8"><title>DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:10vh auto}a{display:inline-block;color:#8cdcff;margin:12px}#launchGame{background:#1789b5;color:white;padding:14px 32px;text-decoration:none;font-weight:bold}#pilotCard dl{display:grid;grid-template-columns:auto 1fr;gap:4px 16px}#pilotCard dd{margin:0}#pilotCard dd a{margin:0}.menuSection h2{font-size:16px;color:#7fa6bf;margin:16px 0 0}</style></head><body><main id="game"><h1>DarkOrbit local emulator</h1><p id="companyName">{{definition.DisplayName}}</p>{{PilotCard(definition, pilotName, gameUserId)}}<a id="launchGame" href="/indexInternal.es?action=internalMapRevolution">LAUNCH</a><a href="/indexInternal.es?action=internalServers">Servers</a><nav id="mainMenu" data-count="{{MenuLinkCount}}">{{MenuNav()}}</nav></main></body></html>
        """;
    }

    // Emulator-defined welcome after the first choice. Every fact comes from the stored
    // company and the /xml/maps.php catalogue, never from the redirect URL.
    public static string ConfirmationPage(string company)
    {
        var definition = CompanyCatalog.Find(company)!;
        var server = LocalMapsXml.Maps.FirstOrDefault(map => map.Id == definition.StarterMap)?.GameServerIp ?? "unknown";
        var mapName = $"{definition.NumericFactionId}-1";
        var code = definition.Code.ToUpperInvariant();
        var testNotice = definition.Code == "tst"
            ? $"<p id=\"companyTestNotice\">TST is the local test company. Its starter map is {definition.StarterMap}.</p>"
            : "";
        return $$"""
        <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Company confirmed</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:10vh auto}a{color:#8cdcff}#companyFacts{display:grid;grid-template-columns:auto 1fr;gap:6px 16px}#companyFacts dd{margin:0}#companyNextSteps li{margin:6px 0}</style></head><body><main id="companyConfirmation" data-company="{{definition.Code}}"><h1>Company confirmed</h1><p id="companyName">{{definition.DisplayName}}</p><p id="companyWelcome">Welcome to {{code}}. Your pilot now serves this company; the choice is permanent.</p>{{testNotice}}<dl id="companyFacts"><dt>Code</dt><dd>{{code}}</dd><dt>Faction</dt><dd>{{definition.NumericFactionId}}</dd><dt>Starter map</dt><dd>{{mapName}} (map {{definition.StarterMap}})</dd><dt>Game server</dt><dd><code>{{server}}</code></dd></dl><h2>Next steps</h2><ul id="companyNextSteps"><li><a id="nextHangar" href="/indexInternal.es?action=internalDock">Check your hangar</a></li><li><a id="nextShop" href="/indexInternal.es?action=internalShop">Visit the shop</a></li><li><a id="nextMap" href="/indexInternal.es?action=internalMapOverview&amp;map={{definition.StarterMap}}">View your starter map</a></li><li><a id="nextHelp" href="/indexInternal.es?action=internalHelp&amp;topic=company">Read the company guide</a></li></ul><a id="continueToGame" href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Continue to game</a></main></body></html>
        """;
    }

    public const string SignedOutPage = "<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Session expired</title></head><body><main id=\"sessionExpired\"><h1>Session expired (401)</h1><p>Your session is missing or expired. Sign in again to open the game menu.</p><a href=\"/index.es?action=externalHome\">Sign in</a></main></body></html>";

    // The official post-selection bodies have not been captured. These local
    // pages make the remaining observed navigation actions usable in the emulator.
    public static string? MenuPage(string? action)
    {
        if (string.Equals(action, "internalPayment", StringComparison.OrdinalIgnoreCase))
            return LocalPayment.Page(new LocalEquipmentState([], []));
        return null;
    }
}
