using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local read-only Logbook / History. No Live logbook request, saved flow, or IL2CPP literal was found;
// categories, entry texts, and amounts are emulator-defined. Demo entries derive from the session's game user ID
// and company; real local economy ledger rows (shop purchases, claims, crafts, admin grants) are appended after
// them as entry numbers GeneratedEntries + 2 + the row's per-account ledger sequence. The page never writes account state.
public static class LocalLogbook
{
    public const int PageSize = 8;
    public const int GeneratedEntries = 30;
    public const int LedgerLimit = 200;

    public sealed record Category(string Code, string Title);

    public sealed record Entry(string Id, long Number, string Category, DateTimeOffset At, string Text, decimal Amount,
        EconomyLedgerRow? Ledger = null);

    public sealed record Result(int Status, string Html);

    public static IReadOnlyList<Category> Categories { get; } =
    [
        new("combat", "Combat"),
        new("economy", "Economy"),
        new("missions", "Missions"),
        new("account", "Account")
    ];

    private static readonly string[] AllowedKeys = ["action", "category", "source", "asset", "page", "entry", "q", "prc", "from"];

    private static readonly (string Category, string Text, long Base)[] Templates =
    [
        ("combat", "Destroyed Streuner x{0}", 4),
        ("combat", "Destroyed Lordakia x{0}", 3),
        ("combat", "Ship repaired at home base ({0} credits)", 250),
        ("economy", "Sold Prometium x{0} to the trade station", 120),
        ("economy", "Received {0} credits from bonus box", 400),
        ("missions", "Completed mission step ({0} experience)", 1_600),
        ("missions", "Gate wave cleared ({0} honour)", 90),
        ("account", "Daily login bonus ({0} credits)", 1_000)
    ];

    // Fixed reference so generated timestamps never depend on the wall clock.
    private static readonly DateTimeOffset Epoch = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<Entry> Entries(GameAuthSession session) => Entries(session, []);

    public static IReadOnlyList<Entry> Entries(GameAuthSession session, IReadOnlyList<EconomyLedgerRow> ledger)
    {
        var seed = (int)(Math.Abs(session.GameUserId % 97) + 1);
        var entries = new List<Entry>(GeneratedEntries + 2);
        for (var n = 1; n <= GeneratedEntries; n++)
        {
            var template = Templates[(n * 5 + seed) % Templates.Length];
            var amount = template.Base * (1 + (n * seed) % 9);
            entries.Add(new Entry(EntryId(session, n), n, template.Category,
                Epoch.AddMinutes(n * 47L + seed), string.Format(CultureInfo.InvariantCulture, template.Text, amount), amount));
        }
        var company = CompanyCatalog.Find(session.Company ?? string.Empty);
        entries.Add(new Entry(EntryId(session, GeneratedEntries + 1), GeneratedEntries + 1, "account",
            Epoch.AddMinutes((GeneratedEntries + 1) * 47L + seed), $"Joined company {company?.DisplayName ?? "none"}", 0));
        entries.Add(new Entry(EntryId(session, GeneratedEntries + 2), GeneratedEntries + 2, "account",
            session.CreatedAt == default ? Epoch.AddDays(30) : session.CreatedAt, "Logged in to the local emulator", 0));
        foreach (var row in ledger)
        {
            if (row.Sequence < 1 || row.Sequence > long.MaxValue - GeneratedEntries - 2 || row.Delta == 0) continue;
            var number = GeneratedEntries + 2L + row.Sequence;
            entries.Add(new Entry(EntryId(session, number), number, LedgerCategory(row), row.OccurredAt,
                LedgerText(row), Math.Abs((decimal)row.Delta), row));
        }
        return entries.DistinctBy(entry => entry.Number).OrderByDescending(entry => entry.Number).ToArray();
    }

    public static string LedgerCategory(EconomyLedgerRow row)
    {
        if (row.Reason == "npc_destroyed") return "combat";
        var tag = $"{row.Reason} {row.MenuSource}".ToLowerInvariant();
        if (tag.Contains("admin") || tag.Contains("legacy_import") || tag.Contains("payment") || tag.Contains("voucher"))
            return "account";
        if (tag.Contains("quest") || tag.Contains("battlepass") || tag.Contains("expedition") || tag.Contains("gate")
            || tag.Contains("spacesearch") || tag.Contains("event"))
            return "missions";
        return "economy";
    }

    public static string LedgerText(EconomyLedgerRow row)
    {
        var what = row.Reason switch
        {
            "shop_purchase" => "Shop purchase",
            "recipe_craft" => "Assembly craft",
            "legacy_import" => "Imported starting balance",
            "legacy_menu_action" => "Menu action",
            "npc_destroyed" => $"Destroyed {row.MenuSource}",
            _ => row.Reason.Replace('_', ' ')
        };
        if (what.Length > 0) what = char.ToUpperInvariant(what[0]) + what[1..];
        if (what.Length > 64) what = what[..64];
        return string.Create(CultureInfo.InvariantCulture,
            $"{what} ({row.MenuSource}): {(row.Delta > 0 ? "+" : "-")}{Math.Abs((decimal)row.Delta):N0} {row.AssetId}, balance {row.BalanceAfter:N0}");
    }

    public static string EntryId(GameAuthSession session, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"{session.GameUserId}-{number}");

    public static Result Resolve(IQueryCollection query, GameAuthSession? session, DateTimeOffset now) =>
        Resolve(query, session, now, []);

    public static Result Resolve(IQueryCollection query, GameAuthSession? session, DateTimeOffset now,
        IReadOnlyList<EconomyLedgerRow> ledger)
    {
        if (session is null || session.ExpiresAt != default && session.ExpiresAt <= now)
            return Error(401, "Your session is missing or expired. Log in again.");
        if (!Contracts.LocalCompanySelection.IsValid(session.Company))
            return Error(403, "Choose a company before opening the logbook.");
        if (Validate(query) is { } invalid) return invalid;

        var categoryCode = query["category"].ToString();
        var category = categoryCode.Length > 0 && categoryCode != "all" ? categoryCode : null;
        if (category is not null && !Categories.Any(item => item.Code == category))
            return Error(404, "Unknown logbook category.");
        var sourceCode = query["source"].ToString();
        var source = sourceCode.Length > 0 && sourceCode != "all" ? sourceCode : null;
        if (source is not null && source is not ("local" or "demo"))
            return Error(404, "Unknown logbook source.");
        var search = query["q"].ToString();
        var asset = query["asset"].ToString();
        TryPositivePage(query["page"].ToString(), out var page);

        var all = Entries(session, ledger);
        if (asset.Length > 0 && !all.Any(entry => entry.Ledger?.AssetId == asset))
            return Error(404, "Unknown local ledger asset.");
        var searched = all.Where(entry => MatchesSearch(entry, search)).ToArray();
        var filtered = searched.Where(entry => (category is null || entry.Category == category)
            && (source is null || (entry.Ledger is not null) == (source == "local"))
            && (asset.Length == 0 || entry.Ledger?.AssetId == asset)).ToArray();
        var pages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        if (query.ContainsKey("entry"))
        {
            TryEntryId(query["entry"].ToString(), out var owner, out var number);
            if (owner != session.GameUserId.ToString(CultureInfo.InvariantCulture))
                return Error(403, "That logbook entry belongs to another account.");
            var entry = all.FirstOrDefault(item => item.Number == number);
            if (entry is null)
                return Error(404, "Logbook entry does not exist.");
            var index = Array.FindIndex(filtered, item => item.Number == number);
            if (index < 0 || (query.ContainsKey("page") && index / PageSize + 1 != page))
                return Error(404, "Logbook entry is not on this page.");
            var returnPage = query.ContainsKey("page") ? page : index / PageSize + 1;
            return new Result(200, DetailPage(session, entry, category, source, asset, search, returnPage,
                index > 0 ? filtered[index - 1] : null,
                index + 1 < filtered.Length ? filtered[index + 1] : null,
                index));
        }

        if (page > pages)
            return Error(404, "Logbook page does not exist.");
        return new Result(200, ListPage(session, category, source, asset, search, page, pages, filtered, searched, all));
    }

    // Menu-73: checks that need no session (keys, action, empty or long values, page and entry syntax),
    // so the endpoint can answer 400 before 401 and the company redirect. Unknown selections stay 404 in Resolve.
    public static Result? Validate(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            if (!AllowedKeys.Contains(key, StringComparer.Ordinal))
                return Error(400, "Unknown logbook parameter.");
            if (values.Count != 1)
                return Error(400, "Logbook parameter must appear once.");
            if (values[0] is not { Length: <= 64 } value || value.Any(char.IsControl))
                return Error(400, "Invalid logbook parameter value.");
        }
        if (query["action"].ToString() != "internalLogbook")
            return Error(400, "Invalid logbook action.");

        var categoryCode = query["category"].ToString();
        if (query.ContainsKey("category") && categoryCode.Length == 0)
            return Error(400, "Logbook category is required.");
        if (categoryCode.Length > 16)
            return Error(400, "Logbook category is too long.");
        if (query.ContainsKey("source") && query["source"].ToString().Length == 0)
            return Error(400, "Logbook source is required.");
        if (query.ContainsKey("asset"))
        {
            var asset = query["asset"].ToString();
            if (asset.Length is < 1 or > 64 || query["source"].ToString() == "demo")
                return Error(400, "Invalid logbook asset filter.");
        }
        if (query.ContainsKey("q"))
        {
            var search = query["q"].ToString();
            if (search.Length is < 2 or > 40 || search[0] == ' ' || search[^1] == ' '
                || search.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '-' or '_')))
                return Error(400, "Invalid logbook search.");
        }

        if (query.ContainsKey("page") && query["page"].ToString().Length == 0)
            return Error(400, "Logbook page is required.");
        if (!TryPositivePage(query["page"].ToString(), out _))
            return Error(400, "Logbook page must be a positive number.");

        if (query.ContainsKey("entry") && !TryEntryId(query["entry"].ToString(), out _, out _))
            return Error(400, "Logbook entry ID is malformed.");
        return null;
    }

    // Menu-74: selections that are refused without reading the ledger (unknown category, another account's
    // entry), so the endpoint answers 404/403 before the Postgres ledger read. Resolve repeats the same checks.
    public static Result? Precheck(IQueryCollection query, GameAuthSession session)
    {
        var categoryCode = query["category"].ToString();
        if (categoryCode.Length > 0 && categoryCode != "all" && !Categories.Any(item => item.Code == categoryCode))
            return Error(404, "Unknown logbook category.");
        var sourceCode = query["source"].ToString();
        if (sourceCode.Length > 0 && sourceCode is not ("all" or "local" or "demo"))
            return Error(404, "Unknown logbook source.");
        if (query.ContainsKey("entry") && TryEntryId(query["entry"].ToString(), out var owner, out _)
            && owner != session.GameUserId.ToString(CultureInfo.InvariantCulture))
            return Error(403, "That logbook entry belongs to another account.");
        return null;
    }

    // Entry IDs are `{gameUserId}-{n}`, both canonical positive integers.
    private static bool TryEntryId(string text, out string owner, out long number)
    {
        var dash = text.IndexOf('-');
        owner = dash > 0 ? text[..dash] : string.Empty;
        number = 0;
        return dash > 0 && IsCanonicalPositiveLong(owner, out _)
            && IsCanonicalPositiveLong(text[(dash + 1)..], out number);
    }

    // The absent page defaults to one; an explicit empty page is rejected above.
    private static bool TryPositivePage(string text, out int value)
    {
        value = 1;
        if (text.Length == 0) return true;
        return text.Length <= 4 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;
    }

    private static bool IsCanonicalPositiveLong(string text, out long value)
    {
        value = 0;
        return text.Length is > 0 and <= 19 && text[0] != '0' && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool MatchesSearch(Entry entry, string search) => search.Length == 0
        || entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Ledger is { } row && (row.AssetId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.MenuSource.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static string ListPage(GameAuthSession session, string? category, string? source, string asset, string search, int page, int pages, IReadOnlyList<Entry> entries,
        IReadOnlyList<Entry> all, IReadOnlyList<Entry> known)
    {
        var e = HtmlEncoder.Default;
        var assetEntries = all.Where(entry => asset.Length == 0 || entry.Ledger?.AssetId == asset).ToArray();
        var sourceEntries = assetEntries.Where(entry => source is null || (entry.Ledger is not null) == (source == "local")).ToArray();
        var categoryEntries = assetEntries.Where(entry => category is null || entry.Category == category).ToArray();
        var html = new StringBuilder(Head("Logbook"));
        html.Append("<main id=\"localLogbook\"><h1>Logbook</h1><p>History of ").Append(e.Encode(session.Username))
            .Append(" (local economy ledger rows plus emulator-defined demo entries; in-world gameplay is not recorded)</p><nav id=\"logbookCategories\"><a href=\"")
            .Append(e.Encode(Link(null, 1, source, search, asset))).Append("\" data-count=\"").Append(sourceEntries.Length).Append('"')
            .Append(category is null ? " aria-current=\"page\"" : "").Append(">All (").Append(sourceEntries.Length).Append(")</a>");
        foreach (var item in Categories)
            html.Append("<a href=\"").Append(e.Encode(Link(item.Code, 1, source, search, asset))).Append('"')
                .Append(" data-count=\"").Append(CategoryCount(sourceEntries, item.Code)).Append('"')
                .Append(item.Code == category ? " aria-current=\"page\"" : "").Append('>').Append(e.Encode(item.Title))
                .Append(" (").Append(CategoryCount(sourceEntries, item.Code)).Append(")</a>");
        html.Append("</nav><nav id=\"logbookSources\">");
        foreach (var (code, title, count) in new[] { ("all", "All sources", categoryEntries.Length),
                     ("local", "Local activity", categoryEntries.Count(entry => entry.Ledger is not null)),
                     ("demo", "Demo entries", all.Count(entry => entry.Ledger is null && (category is null || entry.Category == category))) })
            html.Append("<a href=\"").Append(e.Encode(Link(category, 1, code == "all" ? null : code, search, code == "demo" ? "" : asset)))
                .Append("\" data-source=\"").Append(code).Append("\" data-count=\"").Append(count).Append('"')
                .Append((source ?? "all") == code ? " aria-current=\"page\"" : "")
                .Append('>').Append(title).Append(" (").Append(count).Append(")</a>");
        html.Append("</nav><nav id=\"logbookAssets\"><a href=\"").Append(e.Encode(Link(category, 1, source, search)))
            .Append('"').Append(asset.Length == 0 ? " aria-current=\"page\"" : "").Append(">All assets</a>");
        foreach (var ledgerAsset in known.Where(entry => entry.Ledger is not null).Select(entry => entry.Ledger!.AssetId)
                     .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            html.Append("<a href=\"").Append(e.Encode(Link(category, 1, source == "demo" ? "local" : source, search, ledgerAsset))).Append('"')
                .Append(asset == ledgerAsset ? " aria-current=\"page\"" : "").Append('>')
                .Append(e.Encode(ledgerAsset)).Append("</a>");
        html.Append("</nav><form method=\"get\" action=\"/indexInternal.es\" id=\"logbookSearch\"><input type=\"hidden\" name=\"action\" value=\"internalLogbook\"><input type=\"hidden\" name=\"category\" value=\"")
            .Append(e.Encode(category ?? "all")).Append("\"><input type=\"hidden\" name=\"source\" value=\"").Append(e.Encode(source ?? "all"))
            .Append("\">");
        if (asset.Length > 0)
            html.Append("<input type=\"hidden\" name=\"asset\" value=\"").Append(e.Encode(asset)).Append("\">");
        html.Append("<label for=\"logbookQuery\">Search entries</label> <input id=\"logbookQuery\" name=\"q\" minlength=\"2\" maxlength=\"40\" required value=\"")
            .Append(e.Encode(search)).Append("\"><button type=\"submit\">Search</button></form>");
        if (search.Length > 0)
            html.Append("<p id=\"logbookSearchCount\">").Append(entries.Count).Append(" entries match &quot;").Append(e.Encode(search))
                .Append("&quot;. <a href=\"").Append(e.Encode(Link(category, 1, source, asset: asset))).Append("\">Clear search</a></p>");
        // Keep the balance anchored to the newest saved ledger row. A text search can match an older
        // transaction; summarizing only its matches would mislabel that older balance as the latest one.
        var summaryEntries = known.Where(entry => asset.Length == 0 || entry.Ledger?.AssetId == asset).ToArray();
        html.Append(source == "demo" ? "" : LedgerSummary(summaryEntries)).Append("<h2>").Append(e.Encode(category is null ? "All" : Categories.First(c => c.Code == category).Title))
            .Append(" · page ").Append(page).Append(" of ").Append(pages).Append("</h2>")
            .Append("<table id=\"logbookTable\"><thead><tr><th>Date (UTC)</th><th>Type</th><th>Event</th></tr></thead><tbody>");
        foreach (var entry in entries.Skip((page - 1) * PageSize).Take(PageSize))
            html.Append("<tr><td>").Append(entry.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(entry.Category).Append("</td><td><a href=\"")
                .Append(e.Encode(DetailLink(entry, category, page, source, search, asset))).Append("\">")
                .Append(e.Encode(entry.Text)).Append("</a></td></tr>");
        if (entries.Count == 0)
            html.Append("<tr><td colspan=\"3\">No entries in this category yet.</td></tr>");
        html.Append("</tbody></table><p>");
        if (page > 1)
            html.Append("<a href=\"").Append(e.Encode(Link(category, page - 1, source, search, asset))).Append("\">Previous</a>");
        if (page < pages)
            html.Append("<a href=\"").Append(e.Encode(Link(category, page + 1, source, search, asset))).Append("\">Next</a>");
        html.Append(BackToMenu).Append("</p></main></body></html>");
        return html.ToString();
    }

    private static int CategoryCount(IReadOnlyList<Entry> all, string code) => all.Count(entry => entry.Category == code);

    // Menu-108: per-asset totals over the listed real ledger rows (demo entries excluded). "Latest balance" is the
    // balance after the newest listed row; Net covers only the listed (newest LedgerLimit) rows.
    public static IReadOnlyList<(string Asset, int Rows, decimal Net, long Latest)> Summary(IReadOnlyList<Entry> all) =>
        all.Where(entry => entry.Ledger is not null)
            .GroupBy(entry => entry.Ledger!.AssetId, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count(),
                group.Aggregate(0m, (sum, entry) => sum + entry.Ledger!.Delta),
                group.MaxBy(entry => entry.Number)!.Ledger!.BalanceAfter))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

    private static string LedgerSummary(IReadOnlyList<Entry> all)
    {
        var e = HtmlEncoder.Default;
        var rows = Summary(all);
        var html = new StringBuilder("<section id=\"logbookSummary\"><h2>Local ledger summary</h2>");
        if (rows.Count == 0)
            return html.Append("<p>No local economy activity recorded yet.</p></section>").ToString();
        html.Append("<table><thead><tr><th>Asset</th><th>Entries</th><th>Net change</th><th>Latest balance</th></tr></thead><tbody>");
        foreach (var (asset, count, net, latest) in rows)
            html.Append("<tr data-asset=\"").Append(e.Encode(asset)).Append("\"><td>").Append(e.Encode(asset))
                .Append("</td><td>").Append(count).Append("</td><td>")
                .Append(net.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(latest.ToString("N0", CultureInfo.InvariantCulture)).Append("</td></tr>");
        return html.Append("</tbody></table></section>").ToString();
    }

    private static string DetailPage(GameAuthSession session, Entry entry, string? category, string? source, string asset, string search, int page,
        Entry? previous, Entry? next, int index)
    {
        var e = HtmlEncoder.Default;
        return new StringBuilder(Head("Logbook entry"))
            .Append("<main id=\"localLogbookEntry\"><h1>Logbook entry ").Append(e.Encode(entry.Id)).Append("</h1><dl>")
            .Append("<dt>Pilot</dt><dd>").Append(e.Encode(session.Username)).Append("</dd>")
            .Append("<dt>Date (UTC)</dt><dd>").Append(entry.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</dd>")
            .Append("<dt>Type</dt><dd>").Append(entry.Category).Append("</dd>")
            .Append("<dt>Event</dt><dd>").Append(e.Encode(entry.Text)).Append("</dd>")
            .Append(entry.Ledger is { } row ? LedgerDetails(row) : "<dt>Source</dt><dd>Demo entry</dd>")
            .Append("</dl><p><a href=\"")
            .Append(e.Encode(Link(category, page, source, search, asset))).Append("\">Back to logbook</a>")
            .Append(previous is null ? "" : $"<a href=\"{e.Encode(DetailLink(previous, category, (index - 1) / PageSize + 1, source, search, asset))}\">Previous entry</a>")
            .Append(next is null ? "" : $"<a href=\"{e.Encode(DetailLink(next, category, (index + 1) / PageSize + 1, source, search, asset))}\">Next entry</a>")
            .Append(BackToMenu)
            .Append("</p></main></body></html>").ToString();
    }

    private static string LedgerDetails(EconomyLedgerRow row)
    {
        var e = HtmlEncoder.Default;
        return string.Create(CultureInfo.InvariantCulture,
            $"<dt>Source</dt><dd>Local ledger row {row.Id}</dd><dt>Asset</dt><dd>{e.Encode(row.AssetId)}</dd>" +
            $"<dt>Change</dt><dd>{row.Delta:+#;-#;0}</dd><dt>Balance after</dt><dd>{row.BalanceAfter}</dd>" +
            $"<dt>Menu</dt><dd>{e.Encode(row.MenuSource)}</dd><dt>Reason</dt><dd>{e.Encode(row.Reason)}</dd>");
    }

    private const string BackToMenu = "<a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to menu</a>";

    private static string Head(string title) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        $"<title>{title} - DarkOrbit local emulator</title><style>body{{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}}" +
        "main{max-width:900px;margin:5vh auto;padding:24px}a{color:#8cdcff;margin-right:10px}table{width:100%;border-collapse:collapse}" +
        "td,th{border-bottom:1px solid #4c7896;padding:6px;text-align:left}</style></head><body>";

    private static string Link(string? category, int page, string? source = null, string search = "", string asset = "") =>
        $"/indexInternal.es?action=internalLogbook&category={category ?? "all"}&page={page}"
        + (source is null ? "" : $"&source={source}")
        + (search.Length == 0 ? "" : $"&q={Uri.EscapeDataString(search)}")
        + (asset.Length == 0 ? "" : $"&asset={Uri.EscapeDataString(asset)}");

    private static string DetailLink(Entry entry, string? category, int page, string? source, string search, string asset) =>
        $"{Link(category, page, source, search, asset)}&entry={entry.Id}";

    private static Result Error(int status, string message) => new(status,
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Logbook error</title></head>" +
        $"<body><main><h1>Logbook request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
        "<a href=\"/indexInternal.es?action=internalLogbook\">Back to logbook</a></main></body></html>");
}
