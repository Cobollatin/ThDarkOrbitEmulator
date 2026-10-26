using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only contract. No post-company Tech Factory request was captured.
public sealed record LocalTechRecipe(string Id, string Name, int ComponentCost, int Minutes);
public sealed record LocalTechJob(string RecipeId, DateTimeOffset ReadyAt, Guid Id = default);
public sealed record LocalTechFactoryState(int Components, Dictionary<string, int> Produced, LocalTechJob? Job)
{
    [JsonIgnore] public long BaseComponents { get; set; }
    [JsonIgnore] public bool ClaimOutputFull { get; set; }
}

public sealed class LocalTechFactoryStore(IDistributedCache cache, TimeProvider clock, EconomyStore? economy = null)
{
    public static readonly IReadOnlyList<LocalTechRecipe> Recipes =
    [
        new("targeting", "Targeting boost", 100, 2),
        new("shield", "Shield boost", 150, 3),
        new("repair", "Repair charge", 200, 4),
        new("speed", "Speed boost", 250, 5),
        new("energy", "Energy charge", 300, 6)
    ];

    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly HashSet<string> _submittedKeys = [];
    private static string Key(Guid accountId) => $"localtechfactory:{accountId:N}";

    public async Task<IReadOnlyList<LocalTechRecipe>> GetRecipesAsync(CancellationToken ct)
    {
        if (economy is null) return Recipes;
        return (await economy.RecipesAsync(ct)).Where(recipe => recipe.Enabled
            && recipe.Id.StartsWith("tech_", StringComparison.Ordinal)
            && recipe.Inputs.ValueKind == JsonValueKind.Array && recipe.Inputs.GetArrayLength() == 1
            && recipe.Outputs.ValueKind == JsonValueKind.Array && recipe.Outputs.GetArrayLength() == 1
            && recipe.Inputs[0].GetProperty("kind").GetString() == "resource"
            && recipe.Inputs[0].GetProperty("id").GetString() == "tech_components"
            && recipe.Inputs[0].GetProperty("quantity").TryGetInt32(out var cost) && cost > 0
            && recipe.Outputs[0].GetProperty("kind").GetString() == "resource")
            .Select(recipe =>
            {
                return new LocalTechRecipe(recipe.Id, recipe.Name,
                    recipe.Inputs[0].GetProperty("quantity").GetInt32(),
                    checked((int)Math.Min(int.MaxValue, ((long)recipe.CraftSeconds + 59) / 60)));
            }).ToArray();
    }

    public async Task<LocalTechFactoryState> GetAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        var state = json is null
            ? new LocalTechFactoryState(1000, new Dictionary<string, int>(), null)
            : JsonSerializer.Deserialize<LocalTechFactoryState>(json)!;
        if (economy is not null)
        {
            await economy.ImportResourceAsync(accountId, "tech_components", state.Components, "tech_factory", ct);
            if (state.Job is not null) await economy.ImportTechJobAsync(accountId, state.Job, ct);
            var balances = (await economy.BalancesAsync(accountId, ct)).ToDictionary(balance => balance.AssetId, balance => balance.Amount);
            var produced = new Dictionary<string, int>();
            foreach (var recipe in (await economy.RecipesAsync(ct)).Where(recipe => recipe.Id.StartsWith("tech_", StringComparison.Ordinal)
                && recipe.Outputs.ValueKind == JsonValueKind.Array && recipe.Outputs.GetArrayLength() == 1))
            {
                var assetId = recipe.Outputs[0].GetProperty("id").GetString()!;
                produced[recipe.Id] = (int)Math.Min(int.MaxValue, balances.GetValueOrDefault(assetId));
            }
            state = state with { Components = checked((int)Math.Min(int.MaxValue, balances["tech_components"])), Produced = produced,
                Job = await economy.TechJobAsync(accountId, ct) };
            if (state.Job is not null)
                state.ClaimOutputFull = !await economy.CanClaimTechJobAsync(accountId, ct);
        }
        else if (state.Job is { } job)
            state.ClaimOutputFull = state.Produced.GetValueOrDefault(job.RecipeId) == int.MaxValue;
        state.BaseComponents = state.Components;
        return state;
    }

    public async Task<bool> StartAsync(Guid accountId, string recipeId, CancellationToken ct)
    {
        if (economy is not null)
            return await StartFormAsync(accountId, recipeId.StartsWith("tech_", StringComparison.Ordinal) ? recipeId : $"tech_{recipeId}",
                $"tech:direct:{Guid.NewGuid():N}", ct) == TechFactoryChange.Applied;
        var recipe = (await GetRecipesAsync(ct)).SingleOrDefault(r => r.Id == recipeId);
        if (accountId == Guid.Empty || recipe is null) return false;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            if (state.Job is not null || state.Components < recipe.ComponentCost) return false;
            await SaveAsync(accountId, state with
            {
                Components = state.Components - recipe.ComponentCost,
                Job = new LocalTechJob(recipe.Id, clock.GetUtcNow().AddMinutes(recipe.Minutes), Guid.NewGuid())
            }, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<TechFactoryChange> StartFormAsync(Guid accountId, string recipeId, string key, CancellationToken ct)
    {
        if (economy is null)
        {
            var submitted = $"{accountId:N}:{key}";
            lock (_submittedKeys) if (!_submittedKeys.Add(submitted)) return TechFactoryChange.Conflict;
            if (await StartAsync(accountId, recipeId, ct)) return TechFactoryChange.Applied;
            lock (_submittedKeys) _submittedKeys.Remove(submitted);
            return TechFactoryChange.Conflict;
        }
        await GetAsync(accountId, ct);
        return await economy.ChangeTechFactoryAsync(accountId, recipeId, Guid.Empty, key, clock.GetUtcNow(), ct);
    }

    public async Task<TechFactoryChange> ClaimFormAsync(Guid accountId, Guid jobId, string key, CancellationToken ct)
    {
        if (economy is null)
        {
            var submitted = $"{accountId:N}:{key}";
            lock (_submittedKeys) if (!_submittedKeys.Add(submitted)) return TechFactoryChange.Conflict;
            var state = await GetAsync(accountId, ct);
            if (state.Job?.Id == jobId && await ClaimAsync(accountId, ct)) return TechFactoryChange.Applied;
            lock (_submittedKeys) _submittedKeys.Remove(submitted);
            return TechFactoryChange.Conflict;
        }
        return await economy.ChangeTechFactoryAsync(accountId, null, jobId, key, clock.GetUtcNow(), ct);
    }

    public async Task<bool> ClaimAsync(Guid accountId, CancellationToken ct)
    {
        if (economy is not null)
        {
            var job = await economy.TechJobAsync(accountId, ct);
            return job is not null && await ClaimFormAsync(accountId, job.Id,
                $"tech:direct:claim:{job.Id:N}", ct) == TechFactoryChange.Applied;
        }
        if (accountId == Guid.Empty) return false;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            if (state.Job is not { } job || job.ReadyAt > clock.GetUtcNow()) return false;
            if (state.ClaimOutputFull) return false;
            if (economy is not null)
            {
                var recipeId = job.RecipeId.StartsWith("tech_", StringComparison.Ordinal)
                    ? job.RecipeId : $"tech_{job.RecipeId}";
                var recipe = (await economy.RecipesAsync(ct)).SingleOrDefault(r => r.Id == recipeId);
                if (recipe is null || recipe.Outputs.GetArrayLength() != 1) return false;
                var output = recipe.Outputs[0];
                if (output.GetProperty("kind").GetString() != "resource"
                    || !await economy.AdjustAsync(accountId, output.GetProperty("id").GetString()!,
                        output.GetProperty("quantity").GetInt64(), "tech_factory_claim", "tech_factory",
                        $"tech:{job.RecipeId}:{job.ReadyAt.UtcTicks}", ct)) return false;
            }
            state.Produced[job.RecipeId] = state.Produced.GetValueOrDefault(job.RecipeId) + 1;
            await SaveAsync(accountId, state with { Job = null }, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    private async Task SaveAsync(Guid accountId, LocalTechFactoryState state, CancellationToken ct)
    {
        if (economy is not null)
            await economy.ReconcileResourcesAsync(accountId,
                new Dictionary<string, long> { ["tech_components"] = state.Components - state.BaseComponents },
                "tech_factory", ct);
        await cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(state),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
    }
}

public static class LocalTechFactory
{
    public static bool HasRecipe(IReadOnlyList<LocalTechRecipe> catalog, string id) =>
        catalog.Any(recipe => recipe.Id == id);

    public static IReadOnlyList<LocalTechRecipe> VisibleRecipes(IReadOnlyList<LocalTechRecipe> catalog,
        int components, string? filter) => filter switch
        {
            "affordable" => catalog.Where(recipe => recipe.ComponentCost <= components).ToArray(),
            "shortfall" => catalog.Where(recipe => recipe.ComponentCost > components).ToArray(),
            _ => catalog
        };

    public static string Page(GameAuthSession session, LocalTechFactoryState state, DateTimeOffset now,
        IReadOnlyList<LocalTechRecipe>? catalog = null, string? detail = null, string? filter = null,
        string? started = null, string? claimed = null, string? search = null)
    {
        catalog ??= LocalTechFactoryStore.Recipes;
        var notice = Notice(state, now, catalog, started, claimed);
        // Menu-205: the search narrows the tabs and list; the summary and notices still use every recipe.
        var searched = search is null ? catalog : catalog.Where(recipe => Matches(recipe, search)).ToArray();
        var visible = VisibleRecipes(searched, state.Components, filter);
        var rows = string.Join("", visible.Select(recipe =>
        {
            var shortfall = recipe.ComponentCost - state.Components;
            var status = state.Job is not null ? "Slot busy" : shortfall > 0 ? $"Need {shortfall} more" : "Available";
            return $"<tr data-recipe=\"{HtmlEncoder.Default.Encode(recipe.Id)}\"><td><a href=\"{DetailLink(recipe.Id, filter)}\">{HtmlEncoder.Default.Encode(recipe.Name)}</a></td><td>{recipe.ComponentCost}</td><td>{recipe.Minutes} min</td>" +
                $"<td>{state.Produced.GetValueOrDefault(recipe.Id)}</td><td class=\"status\">{status}</td><td><form method=\"post\" action=\"/indexInternal.es?action=internalTechFactoryStart\">" +
                $"<input type=\"hidden\" name=\"recipe\" value=\"{HtmlEncoder.Default.Encode(recipe.Id)}\"><input type=\"hidden\" name=\"key\" value=\"{LocalTechFactoryAction.CreateFormKey(session.Dosid, "start", recipe.Id, now)}\"><button type=\"submit\"" +
                (state.Job is not null || shortfall > 0 ? " disabled" : "") +
                ">Produce</button></form></td></tr>";
        }));
        var job = state.Job is null ? "Production slot empty" :
            $"Producing {HtmlEncoder.Default.Encode(catalog.SingleOrDefault(r => r.Id == state.Job.RecipeId)?.Name ?? state.Job.RecipeId)}; " +
            (state.Job.ReadyAt <= now
                ? state.ClaimOutputFull ? "ready, but output resource wallet is full" : "ready to claim"
                : $"ready at {state.Job.ReadyAt:yyyy-MM-dd HH:mm} UTC ({Remaining(state.Job.ReadyAt - now)} left)");
        var claim = state.Job is not null && state.Job.ReadyAt <= now && !state.ClaimOutputFull
            ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalTechFactoryClaim\"><input type=\"hidden\" name=\"job\" value=\"{state.Job.Id:N}\"><input type=\"hidden\" name=\"key\" value=\"{LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", state.Job.Id.ToString("N"), now)}\"><button type=\"submit\">Claim</button></form>"
            : state.Job is not null && state.Job.ReadyAt <= now && state.ClaimOutputFull
                ? "<p id=\"techFactoryClaimBlocked\" role=\"alert\">The output resource wallet is full. Spend resources before claiming this job.</p>"
                : "";
        var empty = catalog.Count == 0 ? "<p id=\"noRecipes\">No production recipes are enabled.</p>"
            : visible.Count == 0 ? $"<p id=\"noRecipes\">No recipes match this {(search is null ? "filter" : "search")}.</p>" : "";
        var affordable = catalog.Count(recipe => recipe.ComponentCost <= state.Components);
        var searchedAffordable = searched.Count(recipe => recipe.ComponentCost <= state.Components);
        var query = search is null ? "" : $"&amp;q={HtmlEncoder.Default.Encode(Uri.EscapeDataString(search))}";
        var summary = state.Job is not null
            ? $"Production slot busy. {catalog.Count} recipe(s) listed; claim the current job before starting another."
            : $"{affordable} of {catalog.Count} recipe(s) affordable with {state.Components} components.";
        var tabs = string.Join(" ", new[] { (Id: "all", Name: "All", Count: searched.Count),
            (Id: "affordable", Name: "Affordable", Count: searchedAffordable),
            (Id: "shortfall", Name: "Shortfall", Count: searched.Count - searchedAffordable) }.Select(tab =>
            $"<a href=\"/indexInternal.es?action=internalTechFactory&amp;filter={tab.Id}{query}\"" +
            ((filter ?? "all") == tab.Id ? " aria-current=\"page\"" : "") +
            $">{tab.Name} ({tab.Count})</a>"));
        var searchForm = catalog.Count == 0 ? "" :
            "<form id=\"techFactorySearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalTechFactory\">" +
            (filter is null ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
            $"<input type=\"search\" name=\"q\" maxlength=\"{LocalTechFactoryAction.MaxSearchLength}\" aria-label=\"Search recipes\" value=\"{HtmlEncoder.Default.Encode(search ?? string.Empty)}\"> " +
            "<button type=\"submit\">Search</button></form>" +
            (search is null ? "" :
                $"<p id=\"techFactorySearchSummary\">{visible.Count} of {catalog.Count} recipes match &quot;{HtmlEncoder.Default.Encode(search)}&quot; · " +
                "<a id=\"techFactorySearchClear\" href=\"/indexInternal.es?action=internalTechFactory" +
                (filter is null ? "" : $"&amp;filter={filter}") + "\">Clear search</a></p>");
        var panel = detail is null ? "" : DetailPanel(session, state, now, visible, detail, filter);
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Tech Factory - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="techFactory"><h1>Tech Factory</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · Components: {{state.Components}}</p><p>One production slot is available. Components are spent when production starts; the output enters your resource wallet when claimed.</p>{{notice}}<p id="productionSlot">{{job}}</p>{{claim}}<p id="techFactorySummary">{{summary}}</p><nav id="techFactoryFilters">{{tabs}}</nav>{{searchForm}}{{panel}}{{empty}}<table><thead><tr><th>Recipe</th><th>Components</th><th>Time</th><th>Owned resource</th><th>Status</th><th>Production</th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }

    // Menu-205: a search matches the recipe id or name, ignoring case.
    private static bool Matches(LocalTechRecipe recipe, string search) =>
        recipe.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || recipe.Name.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string DetailLink(string id, string? filter) =>
        $"/indexInternal.es?action=internalTechFactory&amp;detail={HtmlEncoder.Default.Encode(Uri.EscapeDataString(id))}" +
        (filter is null ? "" : $"&amp;filter={filter}");

    private static string DetailPanel(GameAuthSession session, LocalTechFactoryState state, DateTimeOffset now,
        IReadOnlyList<LocalTechRecipe> catalog, string id, string? filter)
    {
        var index = -1;
        for (var i = 0; i < catalog.Count; i++) if (catalog[i].Id == id) { index = i; break; }
        if (index < 0) return "";
        var recipe = catalog[index];
        var name = HtmlEncoder.Default.Encode(recipe.Name);
        var shortfall = Math.Max(0L, (long)recipe.ComponentCost - state.Components);
        var status = state.Job is not null
            ? state.Job.RecipeId == id
                ? state.Job.ReadyAt <= now
                    ? state.ClaimOutputFull ? "This job is ready, but the output resource wallet is full." : "This job is ready to claim."
                    : $"This job is running; {Remaining(state.Job.ReadyAt - now)} left."
                : "The production slot is busy with another recipe."
            : shortfall > 0 ? $"Need {shortfall} more components to start." : "Ready to start.";
        var start = state.Job is null && shortfall == 0
            ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalTechFactoryStart\"><input type=\"hidden\" name=\"recipe\" value=\"{HtmlEncoder.Default.Encode(recipe.Id)}\"><input type=\"hidden\" name=\"key\" value=\"{LocalTechFactoryAction.CreateFormKey(session.Dosid, "start", recipe.Id, now)}\"><button type=\"submit\">Produce {name}</button></form>"
            : "";
        var previous = index > 0 ? $"<a id=\"techDetailPrevious\" href=\"{DetailLink(catalog[index - 1].Id, filter)}\">Previous</a> " : "";
        var next = index + 1 < catalog.Count ? $"<a id=\"techDetailNext\" href=\"{DetailLink(catalog[index + 1].Id, filter)}\">Next</a> " : "";
        return $"<section id=\"techFactoryDetail\" data-recipe=\"{HtmlEncoder.Default.Encode(recipe.Id)}\"><h2>{name}</h2>" +
            $"<p>Recipe {index + 1} of {catalog.Count}</p><ul><li>Cost: {recipe.ComponentCost} components</li>" +
            $"<li>Production time: {recipe.Minutes} min</li><li>Owned resource: {state.Produced.GetValueOrDefault(id)}</li>" +
            (shortfall == 0 ? $"<li>Components after start: {(long)state.Components - recipe.ComponentCost}</li>" : "") + "</ul>" +
            $"<p id=\"techDetailStatus\">{status}</p>{start}<p>{previous}{next}<a id=\"techDetailClose\" href=\"/indexInternal.es?action=internalTechFactory" +
            (filter is null ? "" : $"&amp;filter={filter}") + "\">Close</a></p></section>";
    }

    // Menu-123: confirm only what the saved state proves; an unknown recipe or unmatched job shows nothing.
    public static string Notice(LocalTechFactoryState state, DateTimeOffset now,
        IReadOnlyList<LocalTechRecipe> catalog, string? started, string? claimed)
    {
        if (started is not null && state.Job is { } job && job.RecipeId == started
            && catalog.SingleOrDefault(recipe => recipe.Id == started) is { } startedRecipe)
            return $"<p id=\"techFactoryStarted\" role=\"status\" data-recipe=\"{HtmlEncoder.Default.Encode(started)}\">" +
                $"Started {HtmlEncoder.Default.Encode(startedRecipe.Name)} for {startedRecipe.ComponentCost} components. " +
                (job.ReadyAt <= now
                    ? state.ClaimOutputFull ? "Ready, but the output resource wallet is full." : "Ready to claim."
                    : $"Ready at {job.ReadyAt:yyyy-MM-dd HH:mm} UTC ({Remaining(job.ReadyAt - now)} left).") +
                $" Components left: {Math.Max(0, state.Components)}.</p>";
        if (claimed is not null && state.Produced.GetValueOrDefault(claimed) > 0
            && catalog.SingleOrDefault(recipe => recipe.Id == claimed) is { } claimedRecipe)
            return $"<p id=\"techFactoryClaimed\" role=\"status\" data-recipe=\"{HtmlEncoder.Default.Encode(claimed)}\">" +
                $"Claimed {HtmlEncoder.Default.Encode(claimedRecipe.Name)}. Owned resource: {state.Produced[claimed]}. " +
                $"Components: {Math.Max(0, state.Components)}." + (state.Job is null ? " The production slot is free." : "") + "</p>";
        return "";
    }

    // Rounds up so a job never shows "0 min" while still pending.
    internal static string Remaining(TimeSpan left)
    {
        var minutes = (long)Math.Ceiling(left.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }
}
