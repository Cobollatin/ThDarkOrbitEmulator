using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local menu model. The installed client contains GalaxyGateBuilder binary commands,
// but no post-company HTML or HTTP body was captured for this menu.
public sealed record LocalGalaxyGate(string Id, string Name, int PartsRequired, int PartsOwned, bool Activated);
public sealed record LocalGalaxyGateState(int Energy, List<LocalGalaxyGate> Gates)
{
    public List<string> UsedKeys { get; init; } = [];
}

public sealed class LocalGalaxyGateStore(IDistributedCache cache, EconomyStore? economy = null)
{
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private static string Key(Guid accountId) => $"localgalaxygates:{accountId:N}";

    public async Task<LocalGalaxyGateState> GetAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        var state = json is null ? new LocalGalaxyGateState(250, [.. LocalGalaxyGates.Catalog])
            : JsonSerializer.Deserialize<LocalGalaxyGateState>(json)!;
        if (economy is not null)
        {
            await economy.ImportResourceAsync(accountId, "galaxy_energy", state.Energy, "galaxy_gates", ct);
            if (json is not null) await economy.ImportGalaxyGateProgressAsync(accountId, state.Gates, ct);
            var progress = await economy.GalaxyGateProgressAsync(accountId, ct);
            state = state with
            {
                Energy = (int)Math.Min(int.MaxValue, (await economy.BalancesAsync(accountId, ct))
                    .Single(b => b.AssetId == "galaxy_energy").Amount),
                Gates = LocalGalaxyGates.Catalog.Select(g => progress.TryGetValue(g.Id, out var saved)
                    ? g with { PartsOwned = saved.Parts, Activated = saved.Activated } : g).ToList()
            };
        }
        return state;
    }

    public async Task<bool> SpinAsync(Guid accountId, string gateId, int amount, CancellationToken ct)
    {
        if (accountId == Guid.Empty || amount is < 1 or > 10) return false;
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangeGalaxyGateAsync(accountId, gateId, amount,
                $"gate:{Guid.NewGuid():N}", ct) == GalaxyGateChange.Applied;
        }
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var index = state.Gates.FindIndex(gate => gate.Id == gateId);
            if (index < 0 || state.Energy < amount) return false;
            var gate = state.Gates[index];
            if (gate.Activated || amount > gate.PartsRequired - gate.PartsOwned) return false;
            state.Gates[index] = gate with { PartsOwned = gate.PartsOwned + amount };
            await SaveAsync(accountId, state with { Energy = state.Energy - amount }, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ActivateAsync(Guid accountId, string gateId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return false;
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangeGalaxyGateAsync(accountId, gateId, 0,
                $"activate:{Guid.NewGuid():N}", ct) == GalaxyGateChange.Applied;
        }
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var index = state.Gates.FindIndex(gate => gate.Id == gateId);
            if (index < 0) return false;
            var gate = state.Gates[index];
            if (gate.Activated || gate.PartsOwned != gate.PartsRequired) return false;
            state.Gates[index] = gate with { Activated = true };
            await SaveAsync(accountId, state, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<GalaxyGateChange> ChangeAsync(Guid accountId, string gateId, int amount,
        string key, CancellationToken ct)
    {
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangeGalaxyGateAsync(accountId, gateId, amount, key, ct);
        }
        if (accountId == Guid.Empty || amount is < 0 or > 10) return GalaxyGateChange.Invalid;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            if (state.UsedKeys.Contains(key, StringComparer.Ordinal)) return GalaxyGateChange.Conflict;
            var index = state.Gates.FindIndex(g => g.Id == gateId);
            if (index < 0) return GalaxyGateChange.Invalid;
            var gate = state.Gates[index];
            if (gate.Activated || (amount == 0 ? gate.PartsOwned != gate.PartsRequired
                : amount > gate.PartsRequired - gate.PartsOwned)) return GalaxyGateChange.Conflict;
            if (state.Energy < amount) return GalaxyGateChange.InsufficientEnergy;
            state.Gates[index] = gate with { PartsOwned = gate.PartsOwned + amount, Activated = amount == 0 };
            state.UsedKeys.Add(key);
            await SaveAsync(accountId, state with { Energy = state.Energy - amount }, ct);
            return GalaxyGateChange.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    private async Task SaveAsync(Guid accountId, LocalGalaxyGateState state, CancellationToken ct)
    {
        await cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(state),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
    }
}

public static class LocalGalaxyGates
{
    public static IReadOnlyList<LocalGalaxyGate> Catalog { get; } =
    [
        new("alpha", "Alpha", 34, 0, false),
        new("beta", "Beta", 48, 0, false),
        new("gamma", "Gamma", 82, 0, false),
        new("delta", "Delta", 96, 0, false),
        new("epsilon", "Epsilon", 120, 0, false)
    ];

    // Emulator-defined status text; each spin costs one energy and adds one part.
    internal static string Status(LocalGalaxyGate gate, int energy) =>
        gate.Activated ? "Activated"
            : gate.PartsOwned >= gate.PartsRequired ? "Ready to activate"
            : energy < 1 ? $"No energy ({gate.PartsRequired - gate.PartsOwned} parts missing)"
            : $"{gate.PartsRequired - gate.PartsOwned} parts missing";

    public static bool HasGate(LocalGalaxyGateState state, string? gateId) =>
        gateId is null || state.Gates.Any(gate => gate.Id == gateId);

    // Menu-145: emulator-defined status filters. `incomplete` covers every gate with missing parts, with or without energy.
    public static IReadOnlyList<(string Id, string Label)> Filters { get; } =
        [("all", "All"), ("activated", "Activated"), ("ready", "Ready to activate"), ("incomplete", "Incomplete")];

    public static bool IsFilter(string? filter) => filter is null || Filters.Any(f => f.Id == filter);

    public static bool Matches(LocalGalaxyGate gate, string? filter) => filter switch
    {
        null or "all" => true,
        "activated" => gate.Activated,
        "ready" => !gate.Activated && gate.PartsOwned >= gate.PartsRequired,
        "incomplete" => !gate.Activated && gate.PartsOwned < gate.PartsRequired,
        _ => false
    };

    public static bool MatchesSearch(LocalGalaxyGate gate, string? search) =>
        search is null || gate.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || gate.Name.Contains(search, StringComparison.OrdinalIgnoreCase);

    // A detail gate must exist and, when a filter is selected, belong to it.
    public static bool HasGate(LocalGalaxyGateState state, string? gateId, string? filter) =>
        gateId is null || state.Gates.Any(gate => gate.Id == gateId && Matches(gate, filter));

    private static string FilterSuffix(string? filter) => filter is null or "all" ? "" : $"&amp;filter={filter}";

    private static string SearchSuffix(string? search) => search is null ? ""
        : $"&amp;q={HtmlEncoder.Default.Encode(Uri.EscapeDataString(search))}";

    private static string Link(string gateId, string? filter = null) =>
        $"/indexInternal.es?action=internalGalaxyGates&amp;gate={gateId}{FilterSuffix(filter)}";

    private static string FilterTabs(LocalGalaxyGateState state, string? filter, string? search) =>
        "<nav id=\"gate-filters\">" + string.Join("", Filters.Select(f =>
        {
            var count = state.Gates.Count(g => Matches(g, f.Id) && MatchesSearch(g, search));
            var current = (filter ?? "all") == f.Id ? " aria-current=\"page\"" : "";
            return $"<a href=\"/indexInternal.es?action=internalGalaxyGates{FilterSuffix(f.Id)}{SearchSuffix(search)}\" data-filter=\"{f.Id}\" data-count=\"{count}\"{current}>{f.Label} ({count})</a>";
        })) + "</nav>";

    private static string SearchForm(string? filter, string? search, int matching, int total) =>
        $"<form method=\"get\" action=\"/indexInternal.es\" id=\"gate-search\"><input type=\"hidden\" name=\"action\" value=\"internalGalaxyGates\">" +
        (filter is null or "all" ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
        $"<label>Search gates <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>" +
        (search is null ? "" : $"<p id=\"gate-search-summary\">{matching} of {total} gates match. <a href=\"/indexInternal.es?action=internalGalaxyGates{FilterSuffix(filter)}\">Clear search</a></p>");

    private static string ActionForm(GameAuthSession session, LocalGalaxyGateState state, LocalGalaxyGate gate, string? filter)
    {
        var filterField = filter is null or "all" ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">";
        return
        gate.Activated ? "<p>Gate activated and ready for the game world.</p>"
            : gate.PartsOwned == gate.PartsRequired
                ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalGalaxyGateActivate\"><input type=\"hidden\" name=\"gate\" value=\"{gate.Id}\"><input type=\"hidden\" name=\"key\" value=\"{LocalGalaxyGateAction.CreateFormKey(session.Dosid, gate.Id, true)}\">{filterField}<button type=\"submit\">Activate gate</button></form>"
                : $"<form method=\"post\" action=\"/indexInternal.es?action=internalGalaxyGateSpin\"><input type=\"hidden\" name=\"gate\" value=\"{gate.Id}\"><input type=\"hidden\" name=\"key\" value=\"{LocalGalaxyGateAction.CreateFormKey(session.Dosid, gate.Id)}\">{filterField}<label>Energy spins <input type=\"number\" name=\"amount\" min=\"1\" max=\"{Math.Max(1, Math.Min(Math.Min(LocalGalaxyGateAction.MaxSpinsPerForm, gate.PartsRequired - gate.PartsOwned), state.Energy))}\" value=\"1\"></label><button type=\"submit\"{(state.Energy < 1 ? " disabled" : "")}>Spin</button></form>";
    }

    // Emulator-defined forecast: one energy per missing part.
    internal static string EnergyForecast(LocalGalaxyGate gate, int energy)
    {
        var missing = gate.PartsRequired - gate.PartsOwned;
        if (gate.Activated || missing <= 0) return "No energy needed.";
        return energy >= missing
            ? $"Completing needs {missing} energy; {energy - missing} energy would remain."
            : $"Completing needs {missing} energy; {missing - energy} more energy required.";
    }

    private static string Detail(GameAuthSession session, LocalGalaxyGateState state, string gateId, string? filter)
    {
        var visible = state.Gates.Where(g => Matches(g, filter)).ToList();
        var index = visible.FindIndex(g => g.Id == gateId);
        var gate = visible[index];
        var percent = gate.PartsRequired == 0 ? 100 : gate.PartsOwned * 100 / gate.PartsRequired;
        var previous = index > 0 ? $"<a href=\"{Link(visible[index - 1].Id, filter)}\" rel=\"prev\">Previous gate</a>" : "";
        var next = index < visible.Count - 1 ? $"<a href=\"{Link(visible[index + 1].Id, filter)}\" rel=\"next\">Next gate</a>" : "";
        return $"<section id=\"gate-detail\" data-gate=\"{gate.Id}\"><h2>{HtmlEncoder.Default.Encode(gate.Name)} gate</h2><p>Gate {index + 1} of {visible.Count} · Id {gate.Id}</p><p>Parts: {gate.PartsOwned} / {gate.PartsRequired} ({percent}%) · Missing: {Math.Max(0, gate.PartsRequired - gate.PartsOwned)}</p><p class=\"status\">Status: {Status(gate, state.Energy)}</p><p class=\"forecast\">Energy: {state.Energy}. {EnergyForecast(gate, state.Energy)}</p>{ActionForm(session, state, gate, filter)}<p>{previous}{next}<a href=\"/indexInternal.es?action=internalGalaxyGates{FilterSuffix(filter)}\">Close</a></p></section>";
    }

    // Menu-125 confirmation after a successful spin or activation. Only saved state is shown, never query text;
    // a spun notice hides once the gate is activated and an activated notice needs a saved activation.
    public static string Notice(LocalGalaxyGateState state, string? spun, string? activated)
    {
        if (spun is not null && state.Gates.SingleOrDefault(g => g.Id == spun) is { Activated: false } spunGate)
        {
            var missing = Math.Max(0, spunGate.PartsRequired - spunGate.PartsOwned);
            var next = missing == 0 ? "All parts collected. Activate the gate now."
                : $"{missing} part(s) missing.";
            return $"<p id=\"gateSpun\" role=\"status\" data-gate=\"{spunGate.Id}\">Spin complete on the " +
                $"{HtmlEncoder.Default.Encode(spunGate.Name)} gate: {spunGate.PartsOwned} / {spunGate.PartsRequired} parts. " +
                $"{next} Energy left: {state.Energy}.</p>";
        }
        if (activated is not null && state.Gates.SingleOrDefault(g => g.Id == activated) is { Activated: true } activeGate)
            return $"<p id=\"gateActivated\" role=\"status\" data-gate=\"{activeGate.Id}\">" +
                $"{HtmlEncoder.Default.Encode(activeGate.Name)} gate activated. Activated gates: " +
                $"{state.Gates.Count(g => g.Activated)} / {state.Gates.Count}. Activation does not enter the world.</p>";
        return "";
    }

    public static string Page(GameAuthSession session, LocalGalaxyGateState state, string? gateId = null,
        string? spun = null, string? activated = null, string? filter = null, string? search = null)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var notice = Notice(state, spun, activated);
        var detail = gateId is not null && HasGate(state, gateId, filter) ? Detail(session, state, gateId, filter) : "";
        var visible = state.Gates.Where(g => Matches(g, filter) && MatchesSearch(g, search)).ToList();
        var cards = visible.Count == 0 ? $"<p id=\"gate-empty\">{(search is null ? "No gates match this filter." : "No gates match this search.")}</p>" : string.Join("", visible.Select(gate =>
            $"<article data-gate=\"{gate.Id}\"{(gate.Id == gateId ? " class=\"selected\"" : "")}><h2><a href=\"{Link(gate.Id, filter)}\">{HtmlEncoder.Default.Encode(gate.Name)}</a></h2><p>Parts: {gate.PartsOwned} / {gate.PartsRequired}</p><p class=\"status\">Status: {Status(gate, state.Energy)}</p>{(gate.Id == gateId ? "" : ActionForm(session, state, gate, filter))}</article>"));
        var activatedCount = state.Gates.Count(g => g.Activated);
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Galaxy Gates - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff;margin-right:20px}article{border:1px solid #4c7896;padding:16px;margin:16px 0}button{background:#1789b5;color:white;padding:8px;border:0}#gate-detail,.selected{border:1px solid #8cdcff;padding:16px}</style></head><body><main id="galaxy-gates"><h1>Galaxy Gates</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · Energy: {{state.Energy}} · Activated gates: {{activatedCount}} / {{state.Gates.Count}}</p><p>Each spin adds one local gate part. Complete a gate to activate it; activation does not enter the world. Completed gates cannot be spun again.</p>{{FilterTabs(state, filter, search)}}{{SearchForm(filter, search, visible.Count, state.Gates.Count(g => Matches(g, filter)))}}{{notice}}{{detail}}{{cards}}<p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
