using System.Text.Encodings.Web;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only event catalog. Client metadata gives field names, not values or rules.
public sealed record LocalSpaceSearchPlanet(string Id, string Name, string RewardItemId, string RewardName);
public enum LocalSpaceSearchResult { Applied, UnknownPlanet, AlreadySearched, InsufficientCredits, InventoryFull, Unavailable, Rejected }

public static class LocalSpaceSearch
{
    public const int SearchCost = 100;
    public const int MaxPlanetIdLength = 32;
    public static readonly IReadOnlyList<LocalSpaceSearchPlanet> Catalog =
    [
        new("mars", "Mars", "laser_lf3", "LF-3 laser"),
        new("venus", "Venus", "shield_sg3n_b02", "SG3N-B02 shield"),
        new("earth", "Earth", "drone_flax", "Flax drone"),
        new("jupiter", "Jupiter", "laser_lf2", "LF-2 laser"),
        new("saturn", "Saturn", "shield_sg3n_a03", "SG3N-A03 shield")
    ];

    // Emulator-defined page filters; "available" means not searched yet, whatever the balance.
    public static readonly IReadOnlyList<string> Filters = ["all", "available", "searched"];

    public static LocalSpaceSearchPlanet? Find(string planetId) =>
        Catalog.SingleOrDefault(planet => planet.Id == planetId);

    public static bool IsPlanetIdShape(string value) =>
        value.Length is >= 1 and <= MaxPlanetIdLength && value.All(c => c is >= 'a' and <= 'z');

    // An empty filter means the default list, like the shell's other optional page parameters.
    public static bool IsFilter(string value) => value.Length == 0 || Filters.Contains(value, StringComparer.Ordinal);

    // Menu-213: emulator-defined planet search, trimmed by the caller; blank text means no search.
    public static bool IsSearch(string value) => value.Length is >= 2 and <= 40
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_');

    // Matches the planet ID, its name, or the displayed reward name (owned inventory name first, like the rows).
    public static bool Matches(LocalEquipmentState state, LocalSpaceSearchPlanet planet, string? search) => search is null
        || planet.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || planet.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || RewardName(state, planet).Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string RewardName(LocalEquipmentState state, LocalSpaceSearchPlanet planet) =>
        state.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId)?.Name ?? planet.RewardName;

    public static string Status(LocalEquipmentState state, LocalSpaceSearchPlanet planet) =>
        state.SearchedPlanetIds.Contains(planet.Id) ? "searched" :
        state.Credits < SearchCost ? "locked" : "available";

    public static string Page(LocalEquipmentState state, string filter = "all", string? planetId = null, string? exploredPlanetId = null,
        string? search = null)
    {
        if (filter.Length == 0) filter = "all";
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var searchQuery = search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}";
        var matching = Catalog.Where(planet => Matches(state, planet, search)).ToArray();
        var searched = Catalog.Count(planet => state.SearchedPlanetIds.Contains(planet.Id));
        var remaining = Catalog.Count - searched;
        var remainingCost = (long)remaining * SearchCost;
        var filterQuery = (filter == "all" ? "" : $"&amp;filter={filter}") + searchQuery;
        var visible = matching.Where(planet => filter switch
        {
            "available" => !state.SearchedPlanetIds.Contains(planet.Id),
            "searched" => state.SearchedPlanetIds.Contains(planet.Id),
            _ => true
        }).ToArray();
        var tabs = string.Join(" · ", Filters.Select(id =>
        {
            var count = id switch
            {
                "available" => matching.Count(planet => !state.SearchedPlanetIds.Contains(planet.Id)),
                "searched" => matching.Count(planet => state.SearchedPlanetIds.Contains(planet.Id)),
                _ => matching.Length
            };
            var label = $"{char.ToUpperInvariant(id[0])}{id[1..]} ({count})";
            var href = (id == "all" ? "/indexInternal.es?action=internalSpaceSearch" : $"/indexInternal.es?action=internalSpaceSearch&amp;filter={id}") + searchQuery;
            return id == filter
                ? $"<a href=\"{href}\" data-filter=\"{id}\" aria-current=\"page\" class=\"current\">{label}</a>"
                : $"<a href=\"{href}\" data-filter=\"{id}\">{label}</a>";
        }));
        var rows = visible.Length == 0
            ? search is null ? "<tr><td colspan=\"4\">No planets match this filter.</td></tr>"
                : "<tr><td colspan=\"4\">No planets match this search.</td></tr>"
            : string.Join("", visible.Select(planet =>
            {
                // Postgres inventory lists owned items only, so an unowned reward has no row.
                var reward = state.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId);
                var status = Status(state, planet);
                var action = status == "searched" ? "Searched" :
                    status == "locked" ? "Not enough local credits" : SearchForm(planet);
                var link = $"<a href=\"/indexInternal.es?action=internalSpaceSearch{filterQuery}&amp;planet={planet.Id}\">{HtmlEncoder.Default.Encode(planet.Name)}</a>";
                return $"<tr data-planet=\"{planet.Id}\" data-status=\"{status}\"{(planet.Id == planetId ? " class=\"current\"" : "")}><td>{link}</td><td>{HtmlEncoder.Default.Encode(reward?.Name ?? planet.RewardName)}</td><td>{reward?.Quantity ?? 0}</td><td>{action}</td></tr>";
            }));
        var budget = remaining == 0
            ? "Every planet has been searched."
            : state.Credits >= remainingCost
                ? $"Remaining searches: {remaining} · Cost to finish: {remainingCost} local credits · Your balance covers every remaining search."
                : $"Remaining searches: {remaining} · Cost to finish: {remainingCost} local credits · You need {remainingCost - state.Credits} more local credits to search every remaining planet.";
        // Previous/Next walk the visible result; a planet opened outside the status filter walks the search result.
        var detail = planetId is not null && Find(planetId) is { } selected
            ? Detail(state, selected, filterQuery, visible.Contains(selected) ? visible : matching) : "";
        var clearFilter = filter == "all" ? "" : $"&amp;filter={filter}";
        var searchForm = "<form id=\"planetSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalSpaceSearch\">" +
            (filter == "all" ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
            $"<label>Find planet <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><input type=\"submit\" value=\"Find\"></form>" +
            (search is null ? "" : $"<p id=\"planetSearchResult\">{matching.Length} of {Catalog.Count} planets match \"{HtmlEncoder.Default.Encode(search)}\" · <a href=\"/indexInternal.es?action=internalSpaceSearch{clearFilter}\">Clear search</a></p>");
        var notice = exploredPlanetId is null ? "" : ExploredNotice(state, exploredPlanetId);
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Space Search - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}a.current,tr.current{font-weight:bold}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}#planetDetail{border:1px solid #4c7896;padding:12px;margin:16px 0}</style></head><body><main id="spaceSearch"><h1>Space Search</h1><p>Local event: one search per planet. Each search costs {{SearchCost}} local credits and grants one of the listed Hangar item.</p><p>Balance: {{state.Credits}} local credits · Searched {{searched}} of {{Catalog.Count}} planets</p><p id="searchBudget">{{budget}}</p>{{notice}}{{searchForm}}<nav id="searchFilters">{{tabs}}</nav>{{detail}}<table><thead><tr><th>Planet</th><th>Reward</th><th>Owned</th><th>Status</th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalDock">Hangar</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to menu</a></p></main></body></html>
            """;
    }

    // Built from the saved search state only, so a link alone cannot fake a result.
    public static string ExploredNotice(LocalEquipmentState state, string planetId)
    {
        if (Find(planetId) is not { } planet || !state.SearchedPlanetIds.Contains(planet.Id)) return "";
        var reward = state.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId);
        var remaining = Catalog.Count(candidate => !state.SearchedPlanetIds.Contains(candidate.Id));
        var next = remaining == 0 ? "Every planet has been searched."
            : state.Credits < SearchCost ? $"Planets left: {remaining}. You need {SearchCost - state.Credits} more local credits for the next search."
            : $"Planets left: {remaining}.";
        return $"<p id=\"spaceSearchExplored\" role=\"status\" data-planet=\"{planet.Id}\">Searched {HtmlEncoder.Default.Encode(planet.Name)}: found 1 {HtmlEncoder.Default.Encode(reward?.Name ?? planet.RewardName)} " +
            $"(owned: {reward?.Quantity ?? 0}). Cost: {SearchCost} local credits · Balance: {state.Credits} local credits. {next}</p>";
    }

    private static string SearchForm(LocalSpaceSearchPlanet planet) =>
        $"<form method=\"post\" action=\"/indexInternal.es?action=internalSpaceSearchExplore\"><input type=\"hidden\" name=\"planet\" value=\"{planet.Id}\"><button type=\"submit\">Search</button></form>";

    private static string Detail(LocalEquipmentState state, LocalSpaceSearchPlanet planet, string filterQuery,
        IReadOnlyList<LocalSpaceSearchPlanet> sequence)
    {
        var index = Catalog.ToList().IndexOf(planet);
        var at = sequence.ToList().IndexOf(planet);
        var reward = state.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId);
        var owned = reward?.Quantity ?? 0;
        var status = Status(state, planet);
        var outcome = status switch
        {
            "searched" => "<p>Status: Searched. This planet cannot be searched again.</p>",
            "locked" => $"<p>Status: Not enough local credits. You need {SearchCost - state.Credits} more local credits.</p>",
            _ when owned == int.MaxValue => "<p>Status: Your Hangar cannot hold more of this item.</p>",
            _ => $"<p>Status: Available. Balance after search: {state.Credits - SearchCost} local credits · Owned after search: {owned + 1}</p>{SearchForm(planet)}"
        };
        string Link(LocalSpaceSearchPlanet target, string rel) =>
            $"<a href=\"/indexInternal.es?action=internalSpaceSearch{filterQuery}&amp;planet={target.Id}\" rel=\"{rel}\">{(rel == "prev" ? "Previous" : "Next")}: {HtmlEncoder.Default.Encode(target.Name)}</a>";
        var nav = string.Join(" · ", new[]
        {
            at > 0 ? Link(sequence[at - 1], "prev") : null,
            at >= 0 && at < sequence.Count - 1 ? Link(sequence[at + 1], "next") : null,
            $"<a href=\"/indexInternal.es?action=internalSpaceSearch{filterQuery}\">Close</a>"
        }.Where(part => part is not null));
        return $"<section id=\"planetDetail\" data-planet=\"{planet.Id}\" data-status=\"{status}\"><h2>{HtmlEncoder.Default.Encode(planet.Name)}</h2>" +
            $"<p>Planet {index + 1} of {Catalog.Count} · Reward: {HtmlEncoder.Default.Encode(reward?.Name ?? planet.RewardName)} · Owned: {owned} · Cost: {SearchCost} local credits</p>" +
            $"{outcome}<p>{nav}</p></section>";
    }
}
