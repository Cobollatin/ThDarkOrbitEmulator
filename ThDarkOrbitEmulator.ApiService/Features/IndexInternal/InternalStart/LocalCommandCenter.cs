using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local navigation over the captured news identifiers, categories, priority,
// countdown snapshots, and background filenames. No official page was captured.
public static class LocalCommandCenter
{
    private static readonly string[] Categories = XDocument.Parse(CapturedCommandCenterCategories.Xml)
        .Root!.Elements("category").OrderBy(e => (int)e.Attribute("prio")!)
        .Select(e => (string)e.Attribute("id")!).ToArray();

    public static bool IsCategory(string? category) => category is null || Categories.Contains(category);

    public static bool IsItem(string? item) => item is null ||
        CapturedNewsCatalog.MenuEntries.Any(entry => entry.Name == item);

    public static bool IsSelection(string? category, string? item) =>
        IsCategory(category) && IsItem(item) && (category is null || item is null ||
            CapturedNewsCatalog.MenuEntries.Any(entry => entry.Name == item && entry.Category == category));

    public sealed record Result(int Status, string Html);

    private static readonly string[] AllowedKeys = ["action", "category", "item", "from", "prc", "lang", "sort", "urgency", "q"];

    // List orders: captured priority (default), shortest countdown snapshot first, or notice id.
    public static readonly IReadOnlyList<string> SortOrders = ["priority", "ending", "name"];

    // Menu-115: list filter over the countdown urgency groups (see Urgency).
    public static readonly IReadOnlyList<string> UrgencyGroups = ["ending-soon", "this-week", "running", "permanent"];

    // Menu-72: shape checks that need no session (400 only), so the endpoint can run them
    // before the session and company checks, like the other menus' detail views.
    public static Result? Validate(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            if (!AllowedKeys.Contains(key, StringComparer.Ordinal))
                return Error(400, "Unknown command center parameter.");
            if (values.Count != 1)
                return Error(400, $"Command center parameter '{key}' must appear once.");
            if (values.ToString().Length > 64)
                return Error(400, $"Command center parameter '{key}' is too long.");
            if (values.ToString().Any(char.IsControl))
                return Error(400, $"Command center parameter '{key}' contains control characters.");
        }
        if (query.ContainsKey("action") && query["action"].ToString() != "internalCommandCenter")
            return Error(400, "Invalid command center action.");
        var category = query["category"].ToString();
        var item = query["item"].ToString();
        if (category.Length > 0 && !IsToken(category))
            return Error(400, "Command center category is malformed.");
        if (item.Length > 0 && !IsToken(item))
            return Error(400, "Command center notice id is malformed.");
        var sort = query["sort"].ToString();
        if (sort.Length > 0 && !SortOrders.Contains(sort, StringComparer.Ordinal))
            return Error(400, "Command center sort order is not supported.");
        var urgency = query["urgency"].ToString();
        if (urgency.Length > 0 && !UrgencyGroups.Contains(urgency, StringComparer.Ordinal))
            return Error(400, "Command center urgency filter is not supported.");
        if (urgency.Length > 0 && query["item"].ToString().Length > 0)
            return Error(400, "The urgency filter applies to the notice list only.");
        var search = query["q"].ToString();
        if (query.ContainsKey("q") && (search.Length is < 2 or > 40 || search != search.Trim() ||
            search.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_'))))
            return Error(400, "Command center search is malformed.");
        return null;
    }

    // Validates the query before rendering: only known keys, each at most once, and
    // a category/item pair taken from the captured categories XML and news catalog.
    // With a clock the page shows the same ticking countdowns as /unityApi/news.php;
    // without one it shows the captured snapshot.
    public static Result Resolve(IQueryCollection query, DateTimeOffset? now = null)
    {
        if (Validate(query) is { } invalid) return invalid;
        var category = query["category"].ToString();
        var item = query["item"].ToString();
        if (category.Length == 0) category = null;
        if (item.Length == 0) item = null;
        if (!IsCategory(category))
            return Error(404, "Unknown command center category.");
        if (!IsItem(item))
            return Error(404, "Unknown command center notice.");
        if (!IsSelection(category, item))
            return Error(404, "The notice is not in the selected category.");
        var sort = query["sort"].ToString();
        if (sort.Length == 0) sort = "priority";
        var urgency = query["urgency"].ToString();
        var search = query["q"].ToString();
        if (item is not null && search.Length > 0 &&
            !MatchesSearch(CapturedNewsCatalog.MenuEntries.First(e => e.Name == item), search))
            return Error(404, "The notice is not in the search results.");
        return new Result(StatusCodes.Status200OK, Page(category, item, sort, now, urgency.Length == 0 ? null : urgency,
            search.Length == 0 ? null : search));
    }

    public static Result Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, "Your session is missing or expired. Sign in again.");

    // Captured notice names and category ids use ASCII letters, digits, '_' and '-'.
    private static bool IsToken(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    // Renders a captured countdown snapshot (seconds) as days, hours and minutes.
    public static string FormatCountdown(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.Days > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{span.Days}d {span.Hours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Hours}h {span.Minutes}m");
    }

    // Groups a countdown snapshot: under a day, under a week, under a year, or longer
    // (the captured permanent notices report about 24 years).
    public static string Urgency(int seconds) => seconds switch
    {
        < 86_400 => "ending-soon",
        < 7 * 86_400 => "this-week",
        < 365 * 86_400 => "running",
        _ => "permanent"
    };

    private static string UrgencyLabel(string urgency) => urgency switch
    {
        "ending-soon" => "Ends within a day",
        "this-week" => "Ends this week",
        "running" => "Running",
        _ => "Permanent"
    };

    public static CapturedNewsCatalog.Entry[] Sorted(IEnumerable<CapturedNewsCatalog.Entry> entries, string sort) => sort switch
    {
        "ending" => entries.OrderBy(e => e.Seconds).ThenBy(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray(),
        "name" => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray(),
        _ => entries.OrderBy(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray()
    };

    private static string SortSuffix(string sort) => sort == "priority" ? "" : "&amp;sort=" + sort;

    private static string SearchSuffix(string? search) => search is null ? "" : "&amp;q=" + Uri.EscapeDataString(search);

    private static string ListSuffix(string sort, string? urgency, string? search = null) =>
        SortSuffix(sort) + (urgency is null ? "" : "&amp;urgency=" + urgency) + SearchSuffix(search);

    private static bool MatchesSearch(CapturedNewsCatalog.Entry entry, string search) =>
        entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.Background.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static Result Error(int status, string message) => new(status,
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Command center error</title></head>" +
        $"<body><main id=\"commandCenterError\"><h1>Command center request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
        (status == StatusCodes.Status401Unauthorized
            ? "<a href=\"/\">Sign in</a>"
            : "<a href=\"/indexInternal.es?action=internalCommandCenter\">Back to command center</a>") +
        "</main></body></html>");

    // Menu-100: catalog entries with Seconds replaced by the news.php countdown at `now`.
    public static IReadOnlyList<CapturedNewsCatalog.Entry> EntriesAt(DateTimeOffset? now) => now is { } at
        ? CapturedNewsCatalog.MenuEntries.Select(e => e with { Seconds = CapturedNewsCatalog.RemainingSeconds(e.Seconds, at) }).ToArray()
        : CapturedNewsCatalog.MenuEntries;

    public static string Page(string? category, string? item, string sort = "priority", DateTimeOffset? now = null,
        string? urgency = null, string? search = null)
    {
        if (!SortOrders.Contains(sort, StringComparer.Ordinal))
            sort = "priority";
        if (item is not null || (urgency is not null && !UrgencyGroups.Contains(urgency, StringComparer.Ordinal)))
            urgency = null;
        var catalog = EntriesAt(now);
        var inCategory = catalog.Where(entry => (category is null || entry.Category == category)
            && (item is null || entry.Name == item)
            && (search is null || MatchesSearch(entry, search))).ToArray();
        var entries = Sorted(inCategory.Where(entry => urgency is null || Urgency(entry.Seconds) == urgency), sort);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Command center - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif;margin:0}main{max-width:1000px;margin:5vh auto;padding:24px}a{color:#8cdcff}nav a{display:inline-block;margin:0 16px 16px 0}.card{background:#101e2c;border:1px solid #4c7896;border-radius:6px;padding:20px;margin:12px 0}.meta{color:#a7bccb;font-size:14px}</style></head><body><main id="commandCenter"><h1>Command center</h1><nav id="commandCenterCategories">
            """);
        html.Append("<a href=\"/indexInternal.es?action=internalCommandCenter").Append(ListSuffix(sort, urgency, search)).Append('"')
            .Append(category is null && item is null ? " aria-current=\"page\"" : "")
            .Append(">All (").Append(catalog.Count(e => search is null || MatchesSearch(e, search))).Append(")</a>");
        foreach (var name in Categories)
            html.Append("<a href=\"/indexInternal.es?action=internalCommandCenter&amp;category=")
                .Append(name).Append(ListSuffix(sort, urgency, search)).Append('"').Append(name == category ? " aria-current=\"page\"" : "").Append('>')
                .Append(HtmlEncoder.Default.Encode(name)).Append(" (")
                .Append(catalog.Count(entry => entry.Category == name && (search is null || MatchesSearch(entry, search)))).Append(")</a>");
        html.Append("</nav>");
        if (item is null)
        {
            html.Append("<form id=\"commandCenterSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalCommandCenter\">");
            if (category is not null) html.Append("<input type=\"hidden\" name=\"category\" value=\"").Append(HtmlEncoder.Default.Encode(category)).Append("\">");
            if (sort != "priority") html.Append("<input type=\"hidden\" name=\"sort\" value=\"").Append(sort).Append("\">");
            if (urgency is not null) html.Append("<input type=\"hidden\" name=\"urgency\" value=\"").Append(urgency).Append("\">");
            html.Append("<label>Search notices <input name=\"q\" required minlength=\"2\" maxlength=\"40\" value=\"")
                .Append(HtmlEncoder.Default.Encode(search ?? "")).Append("\"></label><button type=\"submit\">Search</button></form>");
            if (search is not null)
                html.Append("<p id=\"commandCenterSearchStatus\">Results for ").Append(HtmlEncoder.Default.Encode(search))
                    .Append(" · <a href=\"/indexInternal.es?action=internalCommandCenter")
                    .Append(category is null ? "" : "&amp;category=" + Uri.EscapeDataString(category))
                    .Append(ListSuffix(sort, urgency)).Append("\">Clear search</a></p>");
            html.Append("<nav id=\"commandCenterSort\">Sort by:");
            foreach (var order in SortOrders)
            {
                html.Append(" <a href=\"/indexInternal.es?action=internalCommandCenter");
                if (category is not null)
                    html.Append("&amp;category=").Append(Uri.EscapeDataString(category));
                html.Append(ListSuffix(order, urgency, search)).Append('"').Append(order == sort ? " aria-current=\"true\"" : "")
                    .Append('>').Append(order switch { "ending" => "Ending soonest", "name" => "Name", _ => "Priority" }).Append("</a>");
            }
            html.Append("</nav>");
            AppendUrgencyFilter(html, inCategory, category, sort, urgency, search);
        }
        html.Append("<p>").Append(entries.Length).Append(" notices</p>");
        if (item is null && entries.Length > 0)
        {
            var soonest = Sorted(entries, "ending")[0];
            html.Append("<p id=\"commandCenterSummary\">Ending soonest: ")
                .Append(HtmlEncoder.Default.Encode(soonest.Name)).Append(" (").Append(FormatCountdown(soonest.Seconds))
                .Append(") · Ending within a day: ").Append(entries.Count(e => Urgency(e.Seconds) == "ending-soon"))
                .Append(" · Permanent: ").Append(entries.Count(e => Urgency(e.Seconds) == "permanent")).Append("</p>");
        }
        if (entries.Length == 0)
            html.Append(urgency is null
                ? "<p id=\"commandCenterEmpty\">No notices in this category.</p>"
                : "<p id=\"commandCenterEmpty\">No notices match this urgency filter.</p>");
        foreach (var entry in entries)
        {
            var encodedName = HtmlEncoder.Default.Encode(entry.Name);
            var cardUrgency = Urgency(entry.Seconds);
            html.Append("<article class=\"card\" data-urgency=\"").Append(cardUrgency)
                .Append("\" data-seconds=\"").Append(entry.Seconds).Append("\">");
            var imagePath = CommandCenterVisualAssets.GetImagePath(entry.Background);
            if (imagePath is not null)
                html.Append("<img width=\"303\" height=\"168\" alt=\"\" src=\"")
                    .Append(HtmlEncoder.Default.Encode(imagePath)).Append("\">");
            html.Append("<h2><a href=\"/indexInternal.es?action=internalCommandCenter&amp;item=")
                .Append(Uri.EscapeDataString(entry.Name)).Append(SortSuffix(sort)).Append(SearchSuffix(search)).Append("\">").Append(encodedName).Append("</a></h2>")
                .Append("<p class=\"urgency\">").Append(UrgencyLabel(cardUrgency)).Append("</p>")
                .Append("<p class=\"meta\">Category: ").Append(HtmlEncoder.Default.Encode(entry.Category))
                .Append(" · Priority: ").Append(entry.Priority)
                .Append(now is null ? " · Countdown snapshot: " : " · Countdown: ").Append(FormatCountdown(entry.Seconds))
                .Append(" (").Append(entry.Seconds).Append(" seconds)")
                .Append(" · Unity news id: BE_").Append(encodedName).Append("</p>")
                .Append("<p>Background asset: ").Append(HtmlEncoder.Default.Encode(entry.Background)).Append("</p></article>");
        }
        if (item is not null)
            AppendDetailNavigation(html, catalog, item, sort, search);
        html.Append("<p><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to game</a></p></main></body></html>");
        return html.ToString();
    }

    // Menu-115: "Show:" links per urgency group with counts over the current category,
    // keeping category and sort; "Any" clears the filter.
    private static void AppendUrgencyFilter(StringBuilder html, IReadOnlyList<CapturedNewsCatalog.Entry> inCategory,
        string? category, string sort, string? urgency, string? search)
    {
        var categorySuffix = category is null ? "" : "&amp;category=" + Uri.EscapeDataString(category);
        html.Append("<nav id=\"commandCenterUrgency\">Show: <a href=\"/indexInternal.es?action=internalCommandCenter")
            .Append(categorySuffix).Append(SortSuffix(sort)).Append(SearchSuffix(search)).Append('"').Append(urgency is null ? " aria-current=\"true\"" : "")
            .Append(">Any (").Append(inCategory.Count).Append(")</a>");
        foreach (var group in UrgencyGroups)
            html.Append(" <a href=\"/indexInternal.es?action=internalCommandCenter").Append(categorySuffix)
                .Append(ListSuffix(sort, group, search)).Append('"').Append(group == urgency ? " aria-current=\"true\"" : "")
                .Append('>').Append(UrgencyLabel(group)).Append(" (")
                .Append(inCategory.Count(e => Urgency(e.Seconds) == group)).Append(")</a>");
        html.Append("</nav>");
    }

    // Detail view: link back to the notice's category and to its neighbours in the
    // same priority order used by the list.
    private static void AppendDetailNavigation(StringBuilder html, IReadOnlyList<CapturedNewsCatalog.Entry> catalog, string item, string sort, string? search)
    {
        var entry = catalog.First(e => e.Name == item);
        var siblings = Sorted(catalog.Where(e => e.Category == entry.Category &&
            (search is null || MatchesSearch(e, search))), sort);
        var index = Array.FindIndex(siblings, e => e.Name == item);
        html.Append("<nav id=\"commandCenterDetail\"><span id=\"commandCenterPosition\">Notice ").Append(index + 1)
            .Append(" of ").Append(siblings.Length).Append(" in ").Append(HtmlEncoder.Default.Encode(entry.Category))
            .Append("</span> <a href=\"/indexInternal.es?action=internalCommandCenter&amp;category=")
            .Append(Uri.EscapeDataString(entry.Category)).Append(SortSuffix(sort)).Append(SearchSuffix(search)).Append("\">Back to ")
            .Append(HtmlEncoder.Default.Encode(entry.Category)).Append("</a>");
        if (index > 0)
            AppendNoticeLink(html, siblings[index - 1], "Previous", sort, search);
        if (index < siblings.Length - 1)
            AppendNoticeLink(html, siblings[index + 1], "Next", sort, search);
        html.Append("</nav>");
    }

    private static void AppendNoticeLink(StringBuilder html, CapturedNewsCatalog.Entry entry, string label, string sort, string? search) =>
        html.Append("<a rel=\"").Append(label == "Previous" ? "prev" : "next")
            .Append("\" href=\"/indexInternal.es?action=internalCommandCenter&amp;item=")
            .Append(Uri.EscapeDataString(entry.Name)).Append(SortSuffix(sort)).Append(SearchSuffix(search)).Append("\">").Append(label).Append(": ")
            .Append(HtmlEncoder.Default.Encode(entry.Name)).Append("</a>");
}
