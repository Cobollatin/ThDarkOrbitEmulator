using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only Skylab model. No post-company Skylab request or payload was captured.
public sealed record LocalSkylabCollector(string Id, string Name, int Level, int Stock);
public sealed record LocalSkylabState(DateTimeOffset LastCollected, List<LocalSkylabCollector> Collectors)
{
    [JsonIgnore] public Dictionary<string, long> BaseStock { get; set; } = [];
}

public sealed class LocalSkylabStore(IDistributedCache cache, TimeProvider clock, EconomyStore? economy = null)
{
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly HashSet<string> _cacheKeys = [];
    private static string Key(Guid accountId) => $"localskylab:{accountId:N}";

    public async Task<LocalSkylabState> GetAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        var state = json is not null ? JsonSerializer.Deserialize<LocalSkylabState>(json)! : new LocalSkylabState(clock.GetUtcNow(),
        [
            new("prometium", "Prometium", 1, 500),
            new("endurium", "Endurium", 1, 500),
            new("terbium", "Terbium", 1, 500)
        ]);
        if (economy is not null)
        {
            foreach (var collector in state.Collectors)
                await economy.ImportResourceAsync(accountId, collector.Id, collector.Stock, "skylab", ct);
            var progress = await economy.SkylabProgressAsync(accountId, state, ct);
            var balances = await economy.BalancesAsync(accountId, ct);
            state = state with
            {
                LastCollected = progress[0].LastCollected,
                Collectors = LocalSkylab.Catalog.Select(c => new LocalSkylabCollector(c.Id, c.Name,
                    progress.Single(p => p.Id == c.Id).Level,
                    (int)Math.Min(int.MaxValue, balances.Single(b => b.AssetId == c.Id).Amount))).ToList()
            };
        }
        state.BaseStock = state.Collectors.ToDictionary(c => c.Id, c => (long)c.Stock);
        if (json is null && economy is null) await SaveAsync(accountId, state, ct);
        return state;
    }

    public async Task<SkylabChange> ChangeAsync(Guid accountId, string? collectorId,
        int expectedLevel, DateTimeOffset expectedTime, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return SkylabChange.Invalid;
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangeSkylabAsync(accountId, collectorId, expectedLevel,
                expectedTime, clock.GetUtcNow(), key, ct);
        }
        await _mutationLock.WaitAsync(ct);
        try
        {
            if (!_cacheKeys.Add($"{accountId:N}:{key}")) return SkylabChange.Conflict;
            var state = await GetAsync(accountId, ct);
            if (collectorId is null && state.LastCollected != expectedTime ||
                collectorId is not null && state.Collectors.All(c => c.Id != collectorId || c.Level != expectedLevel))
                return SkylabChange.Conflict;
            return collectorId is null ? await CollectCoreAsync(accountId, ct) :
                await UpgradeCoreAsync(accountId, collectorId, ct);
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> UpgradeAsync(Guid accountId, string collectorId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return false;
        await _mutationLock.WaitAsync(ct);
        try
        {
            return await UpgradeCoreAsync(accountId, collectorId, ct) == SkylabChange.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    private async Task<SkylabChange> UpgradeCoreAsync(Guid accountId, string collectorId, CancellationToken ct)
    {
            var state = await GetAsync(accountId, ct);
            var index = state.Collectors.FindIndex(c => c.Id == collectorId);
            if (index < 0) return SkylabChange.Invalid;
            var collector = state.Collectors[index];
            var cost = 100 * collector.Level;
            if (collector.Level >= LocalSkylab.MaxLevel) return SkylabChange.MaxLevel;
            if (collector.Stock < cost) return SkylabChange.InsufficientResources;
            state.Collectors[index] = collector with { Level = collector.Level + 1, Stock = collector.Stock - cost };
            await SaveAsync(accountId, state, ct);
            return SkylabChange.Applied;
    }

    public async Task<bool> CollectAsync(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return false;
        await _mutationLock.WaitAsync(ct);
        try
        {
            return await CollectCoreAsync(accountId, ct) == SkylabChange.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    private async Task<SkylabChange> CollectCoreAsync(Guid accountId, CancellationToken ct)
    {
            var state = await GetAsync(accountId, ct);
            var now = clock.GetUtcNow();
            var elapsedMinutes = LocalSkylab.ElapsedMinutes(state.LastCollected, now);
            var minutes = Math.Min(LocalSkylab.MaxCollectMinutes, elapsedMinutes);
            if (minutes < 1) return SkylabChange.NothingToCollect;
            if (state.Collectors.Any(c => (long)c.Stock + minutes * c.Level * 10 > int.MaxValue))
                return SkylabChange.Conflict;
            var collected = state.Collectors.Select(c => c with { Stock = c.Stock + (int)minutes * c.Level * 10 }).ToList();
            await SaveAsync(accountId, state with
            {
                LastCollected = state.LastCollected.AddMinutes(elapsedMinutes),
                Collectors = collected
            }, ct);
            return SkylabChange.Applied;
    }

    private async Task SaveAsync(Guid accountId, LocalSkylabState state, CancellationToken ct)
    {
        if (economy is not null)
            await economy.ReconcileResourcesAsync(accountId, state.Collectors.ToDictionary(c => c.Id,
                c => (long)c.Stock - state.BaseStock.GetValueOrDefault(c.Id)), "skylab", ct);
        await cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(state),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
    }
}

public static class LocalSkylab
{
    public static IReadOnlyList<(string Id, string Name)> Catalog { get; } =
        [("prometium", "Prometium"), ("endurium", "Endurium"), ("terbium", "Terbium")];

    public const int MaxLevel = 10;
    public const int MaxCollectMinutes = 60;

    // Whole minutes ready to collect, capped at the emulator-defined storage window.
    public static long ElapsedMinutes(DateTimeOffset lastCollected, DateTimeOffset now) =>
        now <= lastCollected ? 0 : (long)(now - lastCollected).TotalMinutes;

    public static long CollectableMinutes(DateTimeOffset lastCollected, DateTimeOffset now) =>
        Math.Min(MaxCollectMinutes, ElapsedMinutes(lastCollected, now));

    public static bool IsCollector(string id) => Catalog.Any(c => c.Id == id);

    // Resources still needed to raise a collector from its level to MaxLevel (100 per current level per step).
    public static long CostToMax(int level) =>
        Enumerable.Range(Math.Max(1, level), Math.Max(0, MaxLevel - Math.Max(1, level))).Sum(l => 100L * l);

    // Whole production minutes needed before the stock covers the next upgrade; 0 when affordable or at maximum level.
    public static long MinutesUntilAffordable(LocalSkylabCollector c) =>
        c.Level >= MaxLevel || c.Level < 1 || c.Stock >= 100L * c.Level ? 0
            : (100L * c.Level - c.Stock + c.Level * 10L - 1) / (c.Level * 10L);

    // Menu-148: emulator-defined collector filters; every collector has exactly one non-`all` status.
    public static IReadOnlyList<(string Id, string Label)> Filters { get; } =
        [("all", "All"), ("upgradable", "Upgradable"), ("saving", "Saving"), ("maxed", "Maximum level")];

    public static bool IsFilter(string? filter) => filter is null || Filters.Any(f => f.Id == filter);

    public static string StatusOf(LocalSkylabCollector c) =>
        c.Level >= MaxLevel ? "maxed" : c.Stock >= 100L * c.Level ? "upgradable" : "saving";

    public static bool Matches(LocalSkylabCollector c, string? filter) =>
        filter is null or "all" || StatusOf(c) == filter;

    // A collector the detail panel can show under the filter; the endpoint answers 404 otherwise.
    public static bool InFilter(LocalSkylabState state, string detail, string? filter) =>
        state.Collectors.Any(c => c.Id == detail && Matches(c, filter));

    private static string FilterSuffix(string? filter) => filter is null or "all" ? "" : $"&amp;filter={filter}";

    // Menu-207: case-insensitive match on collector id or name; null matches every collector.
    public static bool MatchesSearch(LocalSkylabCollector c, string? search) =>
        search is null || c.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string SearchSuffix(string? search) => search is null ? ""
        : $"&amp;q={HtmlEncoder.Default.Encode(Uri.EscapeDataString(search))}";

    private static string SearchForm(string? filter, string? search, int matching, int total) =>
        "<form method=\"get\" action=\"/indexInternal.es\" id=\"skylabSearch\"><input type=\"hidden\" name=\"action\" value=\"internalSkylab\">" +
        (filter is null ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
        $"<label>Search collectors <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>" +
        (search is null ? "" : $"<p id=\"skylabSearchSummary\">{matching} of {total} collectors match. <a href=\"/indexInternal.es?action=internalSkylab{FilterSuffix(filter)}\">Clear search</a></p>");

    private static string DetailLink(string id, string? filter = null) =>
        $"/indexInternal.es?action=internalSkylab{FilterSuffix(filter)}&amp;detail={id}";

    public static string Page(GameAuthSession session, LocalSkylabState state, DateTimeOffset? now = null, string? detail = null,
        string? upgraded = null, bool collected = false, string? filter = null, string? search = null,
        string progressionHtml = "")
    {
        if (filter == "all") filter = null;
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        // Search only narrows the list; it is never combined with a detail panel.
        if (detail is not null) search = null;
        var current = now ?? DateTimeOffset.UtcNow;
        var minutes = CollectableMinutes(state.LastCollected, current);
        var nextUnit = minutes >= MaxCollectMinutes ? 0
            : (int)Math.Ceiling((state.LastCollected.AddMinutes(minutes + 1) - current).TotalSeconds);
        var status = minutes >= MaxCollectMinutes
            ? $"<p id=\"skylabStatus\">Storage full: {MaxCollectMinutes} minutes of production are ready. Extra time cannot be collected.</p>"
            : minutes >= 1
                ? $"<p id=\"skylabStatus\">{minutes} minute(s) of production ready. Next minute completes in {nextUnit} s.</p>"
                : $"<p id=\"skylabStatus\">Nothing to collect yet. The first minute completes in {nextUnit} s.</p>";
        var notices = string.Join("", state.Collectors.Select(c =>
            $"<li>{HtmlEncoder.Default.Encode(c.Name)}: level {c.Level}/10, " +
            $"{c.Level * 10} units/minute, {minutes * c.Level * 10} units pending; " +
            (c.Level >= 10 ? "maximum level reached" : c.Stock < c.Level * 100
                ? $"{c.Level * 100 - c.Stock} more units needed for the next upgrade"
                : "next upgrade available") + "</li>"));
        var shown = state.Collectors.Where(c => Matches(c, filter) && MatchesSearch(c, search)).ToList();
        var tabs = "<nav id=\"skylabFilters\">" + string.Join(" ", Filters.Select(f =>
            $"<a data-filter=\"{f.Id}\"{((filter ?? "all") == f.Id ? " aria-current=\"page\"" : "")} href=\"/indexInternal.es?action=internalSkylab{FilterSuffix(f.Id)}{SearchSuffix(search)}\">" +
            $"{f.Label} ({state.Collectors.Count(c => Matches(c, f.Id) && MatchesSearch(c, search))})</a>")) + "</nav>" +
            SearchForm(filter, search, shown.Count, state.Collectors.Count(c => Matches(c, filter)));
        var empty = shown.Count > 0 ? ""
            : search is not null ? "<p id=\"skylabEmpty\">No collectors match this search.</p>"
            : $"<p id=\"skylabEmpty\">No collectors match the {Filters.First(f => f.Id == filter).Label.ToLowerInvariant()} filter.</p>";
        var rows = string.Join("", shown.Select(c =>
            $"<tr id=\"skylabRow-{c.Id}\" data-status=\"{StatusOf(c)}\"><td><a href=\"{DetailLink(c.Id, filter)}\">{HtmlEncoder.Default.Encode(c.Name)}</a></td><td>{c.Level}</td><td>{c.Stock}</td><td>{minutes * c.Level * 10}</td><td>" +
            (c.Level >= 10 ? "Maximum level" : progressionHtml.Length > 0 ? "Timed upgrade below" :
                $"<form method=\"post\" action=\"/indexInternal.es?action=internalSkylabUpgrade\"><input type=\"hidden\" name=\"collector\" value=\"{c.Id}\"><input type=\"hidden\" name=\"level\" value=\"{c.Level}\"><input type=\"hidden\" name=\"key\" value=\"{LocalSkylabAction.CreateFormKey(session.Dosid, c.Id, c.Level.ToString(), current)}\"><button type=\"submit\">Upgrade ({100 * c.Level} {HtmlEncoder.Default.Encode(c.Name)})</button></form>") + "</td></tr>"));
        var totalOutput = state.Collectors.Sum(c => c.Level * 10L);
        var levelsLeft = state.Collectors.Sum(c => Math.Max(0, MaxLevel - c.Level));
        var summary = $"<p id=\"skylabSummary\">Total output: {totalOutput} units/minute. " +
            (levelsLeft == 0 ? "All collectors are at the maximum level." :
                $"{levelsLeft} upgrade(s) left, costing {state.Collectors.Sum(c => CostToMax(c.Level))} units in total.") + "</p>";
        var panel = ActionNotice(state, upgraded, collected) + (detail is null ? "" : DetailPanel(session, state, detail, minutes, current, filter, progressionHtml.Length > 0));
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Skylab - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="skylab"><h1>Skylab</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}}</p><p>Collectors produce 10 units per level each minute. Up to 60 minutes can be collected at once.</p>{{status}}{{summary}}{{panel}}<ul>{{notices}}</ul>{{tabs}}{{empty}}<table><thead><tr><th>Collector</th><th>Level</th><th>Stock</th><th>Pending</th><th>Upgrade</th></tr></thead><tbody>{{rows}}</tbody></table><form method="post" action="/indexInternal.es?action=internalSkylabCollect"><input type="hidden" name="time" value="{{state.LastCollected.Ticks}}"><input type="hidden" name="key" value="{{LocalSkylabAction.CreateFormKey(session.Dosid, "collect", state.LastCollected.Ticks.ToString(), current)}}"><button type="submit"{{(minutes < 1 ? " disabled" : "")}}>Collect production</button></form>{{progressionHtml}}<p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }

    // Menu-121: confirmation for the `upgraded={id}` / `collected=1` redirect targets, built from persisted state only.
    // An unknown collector, or one still at level 1 (never upgraded), shows nothing; `upgraded` wins over `collected`.
    public static string ActionNotice(LocalSkylabState state, string? upgraded, bool collected)
    {
        if (upgraded is not null)
        {
            var c = state.Collectors.FirstOrDefault(c => string.Equals(c.Id, upgraded, StringComparison.Ordinal));
            if (c is null || c.Level < 2) return "";
            return $"<p id=\"skylabActionNotice\" role=\"status\" data-collector=\"{c.Id}\">" +
                $"{HtmlEncoder.Default.Encode(c.Name)} collector upgraded to level {Math.Min(c.Level, MaxLevel)}/{MaxLevel}. " +
                $"Output: {c.Level * 10} units/minute. Stock: {Math.Max(0, c.Stock)}." +
                (c.Level >= MaxLevel ? " Maximum level reached." : "") + "</p>";
        }
        if (!collected) return "";
        var stocks = string.Join(", ", state.Collectors.Select(c => $"{HtmlEncoder.Default.Encode(c.Name)} {Math.Max(0, c.Stock)}"));
        return $"<p id=\"skylabActionNotice\" role=\"status\" data-collected=\"true\">Production collected. Stock: {stocks}.</p>";
    }

    private static string DetailPanel(GameAuthSession session, LocalSkylabState state, string detail, long minutes, DateTimeOffset current,
        string? filter, bool timedUpgrades)
    {
        var shown = state.Collectors.Where(c => Matches(c, filter)).ToList();
        var index = shown.FindIndex(c => c.Id == detail);
        if (index < 0) return "";
        var c = shown[index];
        var name = HtmlEncoder.Default.Encode(c.Name);
        var maxed = c.Level >= MaxLevel;
        var cost = 100L * c.Level;
        var wait = MinutesUntilAffordable(c);
        var upgrade = maxed ? "<p id=\"skylabDetailUpgrade\">Maximum level reached. No further upgrades.</p>"
            : $"<p id=\"skylabDetailUpgrade\">Next upgrade: level {c.Level + 1} for {cost} {name}, raising output to {(c.Level + 1) * 10} units/minute. " +
              (c.Stock >= cost ? "Stock covers it now." :
                  $"{cost - c.Stock} more units needed: about {wait} minute(s) of production" +
                  (wait > MaxCollectMinutes ? $", more than one {MaxCollectMinutes}-minute collection." : ".")) + "</p>" +
              (timedUpgrades ? "<p>Use the timed upgrade form below.</p>" :
              $"<form method=\"post\" action=\"/indexInternal.es?action=internalSkylabUpgrade\"><input type=\"hidden\" name=\"collector\" value=\"{c.Id}\"><input type=\"hidden\" name=\"level\" value=\"{c.Level}\"><input type=\"hidden\" name=\"key\" value=\"{LocalSkylabAction.CreateFormKey(session.Dosid, c.Id, c.Level.ToString(), current)}\"><button type=\"submit\"{(c.Stock < cost ? " disabled" : "")}>Upgrade ({cost} {name})</button></form>");
        var nav = (index > 0 ? $"<a id=\"skylabDetailPrevious\" href=\"{DetailLink(shown[index - 1].Id, filter)}\">Previous</a> " : "") +
            (index < shown.Count - 1 ? $"<a id=\"skylabDetailNext\" href=\"{DetailLink(shown[index + 1].Id, filter)}\">Next</a> " : "") +
            $"<a id=\"skylabDetailClose\" href=\"/indexInternal.es?action=internalSkylab{FilterSuffix(filter)}\">Close</a>";
        return $"<section id=\"skylabDetail\" data-collector=\"{c.Id}\"><h2>{name} collector</h2>" +
            $"<p>Collector {index + 1} of {shown.Count}</p><ul>" +
            $"<li>Level {c.Level}/{MaxLevel}</li><li>Output: {c.Level * 10} units/minute</li>" +
            $"<li>Stock: {c.Stock}</li><li>Pending: {minutes * c.Level * 10} units</li>" +
            $"<li>Full storage ({MaxCollectMinutes} minutes): {MaxCollectMinutes * c.Level * 10} units</li>" +
            $"<li>Cost to maximum level: {CostToMax(c.Level)} units</li></ul>{upgrade}<p>{nav}</p></section>";
    }
}
