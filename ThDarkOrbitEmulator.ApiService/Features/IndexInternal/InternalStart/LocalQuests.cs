using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only goals backed by local Shop and Assembly actions.
public sealed record LocalQuest(string Id, string Name, int Target, int RewardCredits,
    Func<LocalEquipmentState, int> Progress);

public enum QuestClaimResult { Applied, NotFound, Unavailable, Incomplete, AlreadyClaimed }

public static class LocalQuests
{
    public const int MaxIdLength = 64;

    // Names, targets, and rewards are emulator-defined; no Live quest data was captured.
    // Keep in sync with the Postgres reward_offers seed (migrations 011 and 018).
    public static readonly IReadOnlyList<LocalQuest> Catalog =
    [
        new("first_purchase", "First supply order", 1, 250, state => state.PurchasedItems),
        new("field_supplier", "Field supplier", 3, 500, state => state.PurchasedItems),
        new("first_craft", "First assembly", 1, 400, state => state.CraftedItems),
        new("bulk_buyer", "Bulk buyer", 10, 1000, state => state.PurchasedItems),
        new("master_engineer", "Master engineer", 5, 1500, state => state.CraftedItems)
    ];

    // Quest ids are lowercase ASCII words joined by underscores in both catalogs.
    public static bool IsWellFormedId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private sealed record Row(string Id, string Name, long Progress, long Target, string Reward, bool Enabled, bool Claimed,
        long RewardCredits)
    {
        public string Status => !Enabled ? "unavailable" : Claimed ? "claimed" : Progress >= Target ? "ready" : "in-progress";

        // Whole percent toward the target; a zero target counts as complete.
        public long Percent => Target <= 0 ? 100 : Progress * 100 / Target;
    }

    // Emulator-defined list filters; no official quest list or progress API was captured.
    public static readonly IReadOnlyList<string> Filters = ["all", "ready", "in-progress", "claimed", "unavailable"];

    public static bool IsKnownFilter(string? filter) => filter is not null && Filters.Contains(filter, StringComparer.Ordinal);

    private static List<Row> BuildRows(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers) => offers is null
        ? Catalog.Select(quest => new Row(quest.Id, quest.Name, Math.Max(0, Math.Min(quest.Target, quest.Progress(state))),
            quest.Target, $"{quest.RewardCredits} local credits", true, state.ClaimedQuestIds.Contains(quest.Id), quest.RewardCredits)).ToList()
        : offers.Select(offer => new Row(offer.Id, offer.Name,
            Math.Max(0, Math.Min(offer.Target, offer.ProgressKind == "purchases" ? state.PurchasedItems : offer.ProgressKind == "crafts" ? state.CraftedItems : offer.ProgressKind == "npc_kills" ? offer.Id == "first_streuner" ? state.StreunerKills : state.NpcKills : 0)),
            offer.Target, offer.AssetId == "credits" ? $"{offer.Amount} local credits" : $"{offer.Amount} {offer.AssetId}",
            offer.Enabled, state.ClaimedQuestIds.Contains(offer.Id), offer.AssetId == "credits" ? Math.Max(0, offer.Amount) : 0)).ToList();

    public static bool HasDetail(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers, string filter, string detail,
        string? search = null) =>
        BuildRows(state, offers).Any(row => row.Id == detail && (filter == "all" || row.Status == filter) && Matches(row, search));

    // Menu-209: emulator-defined search over quest ID, name and reward text, case-insensitive.
    private static bool Matches(Row row, string? search) => search is null
        || row.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Reward.Contains(search, StringComparison.OrdinalIgnoreCase);

    public static string Page(GameAuthSession session, LocalEquipmentState state,
        IReadOnlyList<EconomyRewardOffer>? offers = null, string filter = "all", string? detail = null, string? claimed = null,
        string? search = null)
    {
        if (!IsKnownFilter(filter)) filter = "all";
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var list = BuildRows(state, offers);
        var ready = list.Count(row => row.Status == "ready");
        var matching = list.Where(row => Matches(row, search)).ToList();
        var shown = matching.Where(row => filter == "all" || row.Status == filter).ToList();
        var searchQuery = search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}";
        string ListUrl(string? id = null) => "/indexInternal.es?action=internalQuests" +
            (filter == "all" ? "" : $"&amp;filter={filter}") + searchQuery +
            (id is null ? "" : $"&amp;detail={Uri.EscapeDataString(id)}");
        var rows = string.Join("", shown.Select(row =>
        {
            var action = !row.Enabled ? "Unavailable" : row.Claimed ? "Claimed" : row.Progress < row.Target ? $"In progress ({row.Target - row.Progress} more needed)" :
                $"<form method=\"post\" action=\"/indexInternal.es?action=internalQuestClaim\"><input type=\"hidden\" name=\"quest\" value=\"{HtmlEncoder.Default.Encode(row.Id)}\">" +
                $"<input type=\"hidden\" name=\"key\" value=\"{HtmlEncoder.Default.Encode(LocalQuestAction.CreateFormKey(session.Dosid, row.Id))}\"><button type=\"submit\">Claim</button></form>";
            return $"<tr data-quest=\"{HtmlEncoder.Default.Encode(row.Id)}\" data-status=\"{row.Status}\"><td><a href=\"{ListUrl(row.Id)}\">{HtmlEncoder.Default.Encode(row.Name)}</a></td><td>{row.Progress} / {row.Target}</td>" +
                $"<td><progress max=\"100\" value=\"{row.Percent}\">{row.Percent}%</progress> {row.Percent}%</td>" +
                $"<td>{HtmlEncoder.Default.Encode(row.Reward)}</td><td>{action}</td></tr>";
        }));
        var selectedIndex = detail is null ? -1 : shown.FindIndex(row => row.Id == detail);
        var detailPanel = selectedIndex < 0 ? "" : BuildDetail(shown, selectedIndex, ListUrl,
            session);
        var tabs = string.Join(" · ", Filters.Select(name =>
        {
            var count = name == "all" ? matching.Count : matching.Count(row => row.Status == name);
            var label = $"{char.ToUpperInvariant(name[0])}{name[1..].Replace('-', ' ')} ({count})";
            var href = (name == "all" ? "/indexInternal.es?action=internalQuests" : $"/indexInternal.es?action=internalQuests&amp;filter={name}") + searchQuery;
            return name == filter
                ? $"<a href=\"{href}\" data-filter=\"{name}\" aria-current=\"page\"><strong>{label}</strong></a>"
                : $"<a href=\"{href}\" data-filter=\"{name}\">{label}</a>";
        }));
        var empty = shown.Count == 0 ? search is null
            ? $"<p id=\"questEmpty\">No {(filter == "all" ? "" : filter.Replace('-', ' ') + " ")}quests.</p>"
            : $"<p id=\"questEmpty\">No {(filter == "all" ? "" : filter.Replace('-', ' ') + " ")}quests match this search.</p>" : "";
        var searchForm = "<form id=\"questSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalQuests\">" +
            (filter == "all" ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
            $"<label>Find quest <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>";
        var searchSummary = search is null ? "" :
            $"<p id=\"questSearchSummary\">{shown.Count} of {list.Count(row => filter == "all" || row.Status == filter)} quests match. " +
            $"<a href=\"/indexInternal.es?action=internalQuests{(filter == "all" ? "" : $"&amp;filter={filter}")}\">Clear search</a></p>";
        var claimedCredits = list.Where(row => row.Status == "claimed").Sum(row => row.RewardCredits);
        var readyCredits = list.Where(row => row.Status == "ready").Sum(row => row.RewardCredits);
        var achievements = string.Join("", list.Select(row =>
            $"<li>{HtmlEncoder.Default.Encode(row.Name)}: {(row.Claimed ? "Unlocked" : "Locked")}</li>"));
        var notice = $"{list.Count(row => row.Claimed)} of {list.Count} rewards claimed; {ready} ready to claim. " +
            "Progress counts Shop purchases and Assembly crafts. Each reward can be claimed once per account; claim forms expire after one hour.";
        var claimedNotice = BuildClaimedNotice(list, claimed);
        var title = filter == "all" ? "Quests - DarkOrbit" : $"Quests ({filter}) - DarkOrbit";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>{{title}}</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="quests" data-filter="{{filter}}"><h1>Quests</h1><p>Pilot: {{HtmlEncoder.Default.Encode(session.Username)}} · Local credits: {{state.Credits}}</p>{{claimedNotice}}<p id="quest-notice">{{HtmlEncoder.Default.Encode(notice)}}</p><p id="questTotals">Purchases: {{Math.Max(0, state.PurchasedItems)}} · Crafts: {{Math.Max(0, state.CraftedItems)}} · Credits claimed: {{claimedCredits:N0}} · Credits ready: {{readyCredits:N0}}</p><nav id="questFilters">{{tabs}}</nav>{{searchForm}}{{searchSummary}}{{detailPanel}}<table><thead><tr><th>Quest</th><th>Progress</th><th>Completion</th><th>Reward</th><th>Status</th></tr></thead><tbody>{{rows}}</tbody></table>{{empty}}<h2>Achievements</h2><ul>{{achievements}}</ul><p><a href="/indexInternal.es?action=internalShop">Shop</a> · <a href="/indexInternal.es?action=internalAssembly">Assembly</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to menu</a></p></main></body></html>
            """;
    }

    // Menu-119: confirmation after a claim redirect. Unknown or unclaimed ids show nothing, so an
    // edited link cannot fake a reward; the reward text comes from the catalog, never the query.
    private static string BuildClaimedNotice(IReadOnlyList<Row> rows, string? claimed)
    {
        if (claimed is null || rows.FirstOrDefault(row => row.Id == claimed) is not { Claimed: true } row) return "";
        return $"<p id=\"questClaimed\" role=\"status\" data-quest=\"{HtmlEncoder.Default.Encode(row.Id)}\">" +
            $"Reward claimed: {HtmlEncoder.Default.Encode(row.Name)} · {HtmlEncoder.Default.Encode(row.Reward)} added to your account.</p>";
    }

    private static string BuildDetail(IReadOnlyList<Row> rows, int index, Func<string?, string> url, GameAuthSession session)
    {
        var row = rows[index];
        var claim = row.Status == "ready"
            ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalQuestClaim\"><input type=\"hidden\" name=\"quest\" value=\"{HtmlEncoder.Default.Encode(row.Id)}\"><input type=\"hidden\" name=\"key\" value=\"{LocalQuestAction.CreateFormKey(session.Dosid, row.Id)}\"><button type=\"submit\">Claim</button></form>"
            : "";
        var previous = index > 0 ? $"<a id=\"previousQuest\" href=\"{url(rows[index - 1].Id)}\">Previous</a>" : "";
        var next = index + 1 < rows.Count ? $"<a id=\"nextQuest\" href=\"{url(rows[index + 1].Id)}\">Next</a>" : "";
        return $"<section id=\"questDetail\" data-quest=\"{HtmlEncoder.Default.Encode(row.Id)}\"><h2>{HtmlEncoder.Default.Encode(row.Name)}</h2>" +
            $"<p>Quest {index + 1} of {rows.Count} · {row.Status}</p><p>Progress: {row.Progress} / {row.Target} ({row.Percent}%)</p>" +
            $"<p>Remaining: {Math.Max(0, row.Target - row.Progress)} · Reward: {HtmlEncoder.Default.Encode(row.Reward)}</p>{claim}" +
            $"<nav>{previous} {next} <a id=\"closeQuest\" href=\"{url(null)}\">Close</a></nav></section>";
    }
}
