using System.Text;
using System.Text.Encodings.Web;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator guidance behind the captured forum link and local Unity help link.
// No official forum/help response or content was captured.
public static class LocalForumHelp
{
    // Menu is the local menu action the topic explains; every one is served by indexInternal.es.
    public sealed record Topic(string Id, string Title, string Category, string Answer, string Menu, string MenuTitle);

    public static IReadOnlyList<Topic> Topics { get; } =
    [
        new("company", "Choosing a company", "Getting started",
            "Choose MMO, EIC, or VRU on the company page. Your choice is saved to this emulator account and appears in the local hangar summary.",
            "internalCompanyChoose", "Company"),
        new("hangar", "Ships and equipment", "Hangar",
            "Open Hangar to select an owned ship. Each ship has two equipment configurations. Items bought in the local Shop appear in your inventory.",
            "internalDock", "Hangar"),
        new("shop", "Using local credits", "Shop",
            "Shop purchases spend local practice credits and add items to the Hangar. Payment practice packages and vouchers grant only local credits.",
            "internalShop", "Shop"),
        new("assembly", "Assembling equipment", "Production",
            "Assembly turns resources, credits, and parts into Hangar items. A recipe shows what is missing before you can craft it.",
            "internalAssembly", "Assembly"),
        new("skylab", "Skylab collectors", "Production",
            "Skylab collectors produce resources every minute for up to 60 minutes. Collect them before storage is full, and upgrade a collector to raise its output.",
            "internalSkylab", "Skylab"),
        new("tech-factory", "Tech Factory jobs", "Production",
            "Tech Factory runs one timed job at a time. Starting a job uses its components; claim the result after the timer ends.",
            "internalTechFactory", "Tech Factory"),
        new("gates", "Galaxy Gates", "Activities",
            "Use local energy to spin for gate parts. A completed gate can be activated here without entering the world.",
            "internalGalaxyGates", "Galaxy Gates"),
        new("events", "Events and expeditions", "Activities",
            "Local battle pass claims follow Shop and Assembly progress. An expedition can be claimed after its timer finishes.",
            "internalEvents", "Events"),
        new("quests", "Quests and rewards", "Activities",
            "Quests count your Shop purchases and Assembly crafts. Claim a finished quest once to receive its local reward.",
            "internalQuests", "Quests"),
        new("auction", "Personal auction", "Economy",
            "Each bid must be at least 10% above the current bid, and the bid amount is held from your credits. Claim the item after a lot you lead has ended.",
            "internalAuction", "Auction"),
        new("clan", "Joining a clan", "Community",
            "The clan list shows clans from your company. You can join one open clan at a time and leave it from the same page.",
            "internalClan", "Clan"),
        new("account", "Account preferences", "Account",
            "Account saves your language, sound, and notification preferences for this emulator account. Your password is never requested in a menu.",
            "internalAccount", "Account")
    ];

    // Emulator-defined board notices; no Live forum board content was captured.
    public sealed record Notice(string Id, string Title, string Body);

    public static IReadOnlyList<Notice> Notices { get; } =
    [
        new("local-only", "Local emulator board",
            "This board is read-only. Posts, replies, and private messages are not sent anywhere."),
        new("redirect-mode", "Redirect mode reminder",
            "Menus open from your installed client through Redirect mode. If a page fails to load, sign in again from the client."),
        new("practice-economy", "Practice economy",
            "Credits, items, and rewards shown in local menus are practice values stored only by this emulator.")
    ];

    public static bool IsTopic(string? topic) => topic is null || Topics.Any(item => item.Id == topic);

    // Category filter ids are derived from the topic category labels ("Getting started" -> "getting-started").
    public static string CategoryId(string category) => category.ToLowerInvariant().Replace(' ', '-');

    public static IReadOnlyList<string> Categories { get; } =
        Topics.Select(item => item.Category).Distinct().ToList();

    public static bool IsCategory(string? category) =>
        category is null || Categories.Any(item => CategoryId(item) == category);

    // A topic opened with a category filter must belong to that category.
    public static bool InCategory(string? topic, string? category) =>
        topic is null || category is null
        || Topics.Any(item => item.Id == topic && CategoryId(item.Category) == category);

    // Emulator-defined search: case-insensitive substring of a topic's title, category, answer, or menu name.
    public static bool Matches(Topic item, string search) =>
        new[] { item.Title, item.Category, item.Answer, item.MenuTitle }
            .Any(text => text.Contains(search, StringComparison.OrdinalIgnoreCase));

    public static string Page(bool forum, string? topic = null, string? category = null, string? search = null)
    {
        var title = forum ? "Forum" : "Help";
        var action = forum ? "internalForum" : "internalHelp";
        var id = forum ? "localForum" : "localHelp";
        var selected = topic is not null
            ? Topics.Where(item => item.Id == topic)
            : Topics.Where(item => (category is null || CategoryId(item.Category) == category)
                && (search is null || Matches(item, search)));
        var html = new StringBuilder($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{title}} - DarkOrbit local emulator</title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:850px;margin:5vh auto;padding:24px}a{color:#8cdcff}.card{background:#101e2c;border:1px solid #4c7896;border-radius:6px;padding:18px;margin:14px 0}.category{color:#abc0d0;font-size:14px}</style></head><body><main id="{{id}}"><h1>{{title}}</h1><p>Local emulator guidance</p><nav><a href="/indexInternal.es?action=internalForum">Forum</a> · <a href="/indexInternal.es?action=internalHelp">Help</a></nav>
            """);
        if (topic is null)
        {
            var searchSuffix = search is null ? "" : "&amp;q=" + HtmlEncoder.Default.Encode(Uri.EscapeDataString(search));
            var matchingTopics = search is null ? Topics : Topics.Where(item => Matches(item, search)).ToList();
            html.Append("<nav id=\"topicCategories\"><a href=\"/indexInternal.es?action=").Append(action)
                .Append(searchSuffix).Append('"')
                .Append(category is null ? " aria-current=\"page\"" : "").Append(">All (").Append(matchingTopics.Count).Append(")</a>");
            foreach (var name in Categories)
            {
                var categoryId = CategoryId(name);
                html.Append(" · <a href=\"/indexInternal.es?action=").Append(action).Append("&amp;category=").Append(categoryId)
                    .Append(searchSuffix)
                    .Append("\" data-category=\"").Append(categoryId).Append('"')
                    .Append(categoryId == category ? " aria-current=\"page\"" : "").Append('>')
                    .Append(HtmlEncoder.Default.Encode(name)).Append(" (")
                    .Append(matchingTopics.Count(item => item.Category == name)).Append(")</a>");
            }
            html.Append("</nav>");
            // GET search form; the current category is kept so a search stays inside the open tab.
            html.Append("<form id=\"topicSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"")
                .Append(action).Append("\">");
            if (category is not null)
                html.Append("<input type=\"hidden\" name=\"category\" value=\"").Append(category).Append("\">");
            html.Append("<input type=\"search\" name=\"q\" maxlength=\"40\" value=\"")
                .Append(HtmlEncoder.Default.Encode(search ?? "")).Append("\" aria-label=\"Search topics\"> <button type=\"submit\">Search</button></form>");
        }
        if (forum && topic is null && category is null && search is null)
        {
            html.Append("<section id=\"forumNotices\"><h2>Board notices</h2>");
            foreach (var notice in Notices)
                html.Append("<article class=\"card\" id=\"notice-").Append(notice.Id).Append("\"><h3>")
                    .Append(HtmlEncoder.Default.Encode(notice.Title)).Append("</h3><p>")
                    .Append(HtmlEncoder.Default.Encode(notice.Body)).Append("</p></article>");
            html.Append("</section>");
        }
        foreach (var item in selected)
        {
            html.Append("<article class=\"card\" data-topic=\"").Append(item.Id).Append("\"><p class=\"category\">")
                .Append(HtmlEncoder.Default.Encode(item.Category)).Append("</p><h2><a href=\"/indexInternal.es?action=")
                .Append(action).Append("&amp;topic=").Append(item.Id).Append("\">")
                .Append(HtmlEncoder.Default.Encode(item.Title)).Append("</a></h2>");
            if (topic is not null)
                html.Append("<p>").Append(HtmlEncoder.Default.Encode(item.Answer)).Append("</p>");
            html.Append("</article>");
        }
        if (topic is not null)
        {
            var index = Topics.ToList().FindIndex(item => item.Id == topic);
            var current = Topics[index];
            html.Append("<p><a id=\"openMenu\" href=\"/indexInternal.es?action=").Append(current.Menu).Append("\">Open ")
                .Append(HtmlEncoder.Default.Encode(current.MenuTitle)).Append("</a></p><nav id=\"topicNav\">");
            if (index > 0)
                html.Append("<a rel=\"prev\" href=\"/indexInternal.es?action=").Append(action).Append("&amp;topic=")
                    .Append(Topics[index - 1].Id).Append("\">Previous: ")
                    .Append(HtmlEncoder.Default.Encode(Topics[index - 1].Title)).Append("</a> ");
            if (index < Topics.Count - 1)
                html.Append("<a rel=\"next\" href=\"/indexInternal.es?action=").Append(action).Append("&amp;topic=")
                    .Append(Topics[index + 1].Id).Append("\">Next: ")
                    .Append(HtmlEncoder.Default.Encode(Topics[index + 1].Title)).Append("</a>");
            html.Append("</nav><p><a id=\"topicCategory\" href=\"/indexInternal.es?action=").Append(action).Append("&amp;category=")
                .Append(CategoryId(current.Category)).Append("\">More in ").Append(HtmlEncoder.Default.Encode(current.Category))
                .Append("</a> · <a href=\"/indexInternal.es?action=").Append(action).Append("\">All topics</a></p>");
        }
        else if (search is not null)
        {
            var count = selected.Count();
            html.Append("<p id=\"topicCount\">").Append(count).Append(count == 1 ? " topic matches" : " topics match")
                .Append(" \"").Append(HtmlEncoder.Default.Encode(search)).Append('"');
            if (category is not null)
                html.Append(" in ").Append(HtmlEncoder.Default.Encode(Categories.First(name => CategoryId(name) == category)));
            html.Append("</p>");
            if (count == 0)
                html.Append("<p id=\"noTopics\">Try a shorter word or browse all topics.</p>");
            html.Append("<p><a id=\"clearSearch\" href=\"/indexInternal.es?action=").Append(action);
            if (category is not null)
                html.Append("&amp;category=").Append(category);
            html.Append("\">Clear search</a> · <a href=\"/indexInternal.es?action=").Append(action).Append("\">All topics</a></p>");
        }
        else if (category is not null)
        {
            var count = selected.Count();
            html.Append("<p id=\"topicCount\">").Append(count).Append(count == 1 ? " topic" : " topics").Append(" in ")
                .Append(HtmlEncoder.Default.Encode(Categories.First(name => CategoryId(name) == category)))
                .Append("</p><p><a href=\"/indexInternal.es?action=").Append(action).Append("\">All topics</a></p>");
        }
        else
            html.Append("<p id=\"topicCount\">").Append(Topics.Count).Append(" topics</p>");
        html.Append("<p><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to game</a></p></main></body></html>");
        return html.ToString();
    }
}
