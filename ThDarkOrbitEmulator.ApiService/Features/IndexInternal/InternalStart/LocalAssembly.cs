using System.Globalization;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only recipes. No post-company Assembly payload was captured.
public sealed record LocalAssemblyRecipe(string Id, string Name, string ItemId, int ComponentCost, int CreditCost);

public static class LocalAssembly
{
    // Menu-55: emulator-defined status filters for the recipe list; `all` is the default.
    public static readonly IReadOnlyList<string> StatusFilters = ["all", "ready", "missing", "unavailable"];

    private sealed record InputView(string Kind, string Id, long Need, long Have);

    private sealed record RecipeView(EconomyRecipe Recipe, InputView[] Inputs, string[] Outputs)
    {
        public string[] Missing { get; } = Inputs.Where(input => input.Have < input.Need).Select(input => input.Id).ToArray();
        public bool Ready => Recipe.Enabled && Missing.Length == 0;
        public string Filter => !Recipe.Enabled ? "unavailable" : Missing.Length > 0 ? "missing" : "ready";

        // How many crafts the current balances cover; a recipe without positive inputs is not capped.
        public long? MaxCrafts => !Recipe.Enabled ? 0
            : Inputs.All(input => input.Need <= 0) ? null
            : Inputs.Where(input => input.Need > 0).Min(input => input.Have / input.Need);
    }

    public static bool HasRecipe(IReadOnlyList<EconomyRecipe> recipes, string? recipeId) =>
        recipeId is null || recipes.Any(recipe => recipe.Id == recipeId);

    public static string PageFromDatabase(GameAuthSession session, IReadOnlyList<EconomyRecipe> recipes,
        IReadOnlyList<EconomyBalance> balances, IReadOnlyDictionary<string, long> inventory,
        string status = "all", string? recipeId = null, string? craftedRecipeId = null, long craftedCount = 0,
        string? search = null)
    {
        if (!StatusFilters.Contains(status)) status = "all";
        var amounts = balances.ToDictionary(balance => balance.AssetId, balance => balance.Amount);
        var allViews = recipes.Select(recipe => View(recipe, amounts, inventory)).ToArray();
        // Menu-203: the search narrows the tabs and list; the ready notice and crafted confirmation still use every recipe.
        var views = search is null ? allViews : allViews.Where(view => Matches(view.Recipe, search)).ToArray();
        // Menu-122: confirm only a recipe this account has actually crafted; text comes from the recipe and saved progress.
        var crafted = craftedRecipeId is null || craftedCount < 1 ? null
            : allViews.FirstOrDefault(view => string.Equals(view.Recipe.Id, craftedRecipeId, StringComparison.Ordinal));
        var craftedNotice = crafted is null ? "" :
            $"<p id=\"assembly-crafted\" role=\"status\" data-recipe=\"{HtmlEncoder.Default.Encode(crafted.Recipe.Id)}\">" +
            $"Crafted {HtmlEncoder.Default.Encode(crafted.Recipe.Name)}. Received: {string.Join(", ", crafted.Outputs)}. " +
            $"Total crafted: {craftedCount.ToString("N0", CultureInfo.InvariantCulture)}. Can craft again: {MaxCraftsText(crafted)}.</p>";
        var ready = allViews.Count(view => view.Ready);
        var tabs = string.Join(" · ", StatusFilters.Select(filter =>
        {
            var count = filter == "all" ? views.Length : views.Count(view => view.Filter == filter);
            var current = filter == status ? " aria-current=\"page\"" : "";
            return $"<a href=\"{Link(filter, search: search)}\" data-status=\"{filter}\"{current}>{char.ToUpperInvariant(filter[0])}{filter[1..]} ({count})</a>";
        }));
        var notice = $"{ready} of {recipes.Count} recipes ready. Each craft form works once and expires after one hour.";
        var selected = recipeId is null ? null : allViews.FirstOrDefault(view => view.Recipe.Id == recipeId);
        string body;
        if (selected is not null)
        {
            var inputs = string.Join("", selected.Inputs.Select(input =>
                $"<tr data-input=\"{HtmlEncoder.Default.Encode(input.Id)}\"><td>{HtmlEncoder.Default.Encode(input.Id)}</td><td>{HtmlEncoder.Default.Encode(input.Kind)}</td>" +
                $"<td>{input.Need}</td><td>{input.Have}</td><td>{Math.Max(0, input.Need - input.Have)}</td></tr>"));
            body = $"<section id=\"assembly-recipe\" data-recipe=\"{HtmlEncoder.Default.Encode(selected.Recipe.Id)}\"><h2>{HtmlEncoder.Default.Encode(selected.Recipe.Name)}</h2>" +
                $"<p>Status: {Status(selected)}</p><p id=\"assembly-max\">Can craft now: {MaxCraftsText(selected)}</p>" +
                $"<table><thead><tr><th>Input</th><th>Kind</th><th>Need</th><th>Have</th><th>Short</th></tr></thead><tbody>{inputs}</tbody></table>" +
                $"<p>Output: {string.Join(", ", selected.Outputs)}</p>{Form(session, selected)}" +
                $"<p><a id=\"assembly-back\" href=\"{Link(status)}\">Back to recipes</a></p></section>";
        }
        else
        {
            var shown = views.Where(view => status == "all" || view.Filter == status).ToArray();
            var rows = string.Join("", shown.Select(view =>
                $"<tr data-recipe=\"{HtmlEncoder.Default.Encode(view.Recipe.Id)}\"><td><a href=\"{Link(status, view.Recipe.Id)}\">{HtmlEncoder.Default.Encode(view.Recipe.Name)}</a></td>" +
                $"<td>{string.Join(", ", view.Inputs.Select(input => $"{input.Need} {HtmlEncoder.Default.Encode(input.Id)}"))}</td><td>{string.Join(", ", view.Outputs)}</td>" +
                $"<td>{Status(view)}</td><td>{MaxCraftsText(view)}</td><td>{Form(session, view)}</td></tr>"));
            var empty = shown.Length == 0 ? $"<p id=\"assembly-empty\">No recipes match this {(search is null ? "filter" : "search")}.</p>" : "";
            var statusField = status == "all" ? "" : $"<input type=\"hidden\" name=\"status\" value=\"{status}\">";
            var summary = search is null ? "" :
                $"<p id=\"assembly-search-summary\">{shown.Length} of {recipes.Count} recipes match &quot;{HtmlEncoder.Default.Encode(search)}&quot; · " +
                $"<a id=\"assembly-search-clear\" href=\"{Link(status)}\">Clear search</a></p>";
            body = $"<form id=\"assembly-search\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalAssembly\">{statusField}" +
                $"<input type=\"search\" name=\"q\" maxlength=\"{LocalAssemblyAction.MaxSearchLength}\" aria-label=\"Search recipes\" value=\"{HtmlEncoder.Default.Encode(search ?? string.Empty)}\"> " +
                $"<button type=\"submit\">Search</button></form>{summary}" +
                $"<table><thead><tr><th>Recipe</th><th>Cost</th><th>Output</th><th>Status</th><th>Can craft</th><th></th></tr></thead><tbody>{rows}</tbody></table>{empty}";
        }
        return $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Assembly - DarkOrbit</title></head><body><main id=\"assembly\"><h1>Assembly</h1><p>Pilot: {HtmlEncoder.Default.Encode(session.Username)}</p>{craftedNotice}<p id=\"assembly-notice\">{HtmlEncoder.Default.Encode(notice)}</p><nav id=\"assembly-filters\">{tabs}</nav>{body}<p><a href=\"/indexInternal.es?action=internalDock\">Hangar</a> · <a href=\"/indexInternal.es?action=internalQuests\">Quests</a></p></main></body></html>";
    }

    private static RecipeView View(EconomyRecipe recipe, IReadOnlyDictionary<string, long> amounts,
        IReadOnlyDictionary<string, long> inventory) => new(recipe,
        recipe.Inputs.EnumerateArray().Select(element =>
        {
            var kind = element.GetProperty("kind").GetString()!;
            var id = element.GetProperty("id").GetString()!;
            return new InputView(kind, id, element.GetProperty("quantity").GetInt64(),
                kind == "item" ? inventory.GetValueOrDefault(id) : amounts.GetValueOrDefault(id));
        }).ToArray(),
        recipe.Outputs.EnumerateArray().Select(element =>
            $"{element.GetProperty("quantity").GetInt64()} {HtmlEncoder.Default.Encode(element.GetProperty("id").GetString() ?? string.Empty)}").ToArray());

    // Menu-203: a search matches the recipe id or name, or the id of any input or output.
    private static bool Matches(EconomyRecipe recipe, string search) =>
        recipe.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || recipe.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || recipe.Inputs.EnumerateArray().Concat(recipe.Outputs.EnumerateArray())
            .Any(element => (element.GetProperty("id").GetString() ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));

    private static string Status(RecipeView view) => !view.Recipe.Enabled ? "Unavailable"
        : view.Missing.Length > 0 ? $"Missing: {string.Join(", ", view.Missing.Select(id => HtmlEncoder.Default.Encode(id)))}" : "Ready";

    private static string MaxCraftsText(RecipeView view) =>
        view.MaxCrafts is { } max ? max.ToString(CultureInfo.InvariantCulture) : "Unlimited";

    private static string Form(GameAuthSession session, RecipeView view) => !view.Recipe.Enabled ? "" :
        $"<form method=\"post\" action=\"/indexInternal.es?action=internalAssemblyCraft\"><input type=\"hidden\" name=\"recipe\" value=\"{HtmlEncoder.Default.Encode(view.Recipe.Id)}\">" +
        $"<input type=\"hidden\" name=\"key\" value=\"{HtmlEncoder.Default.Encode(LocalAssemblyAction.CreateFormKey(session.Dosid, view.Recipe.Id))}\"><button type=\"submit\"{(view.Ready ? "" : " disabled")}>Craft</button></form>";

    // Recipe ids are validated lowercase words, so they need no URL escaping beyond HTML encoding.
    // Search text is free-form, so it is URL-escaped before HTML encoding.
    private static string Link(string status, string? recipe = null, string? search = null) =>
        "/indexInternal.es?action=internalAssembly" + (status == "all" ? "" : $"&amp;status={status}")
        + (recipe is null ? "" : $"&amp;detail={HtmlEncoder.Default.Encode(recipe)}")
        + (search is null ? "" : $"&amp;q={HtmlEncoder.Default.Encode(Uri.EscapeDataString(search))}");

    public static readonly IReadOnlyList<LocalAssemblyRecipe> Recipes =
    [
        new("lf3", "LF-3 laser", "laser_lf3", 150, 500),
        new("shield", "SG3N-B02 shield", "shield_sg3n_b02", 100, 400),
        new("flax", "Flax drone", "drone_flax", 200, 600)
    ];

    public static string Page(GameAuthSession session, LocalEquipmentState state)
    {
        var rows = string.Join("", Recipes.Select(recipe =>
            $"<tr><td>{HtmlEncoder.Default.Encode(recipe.Name)}</td><td>{recipe.ComponentCost}</td><td>{recipe.CreditCost}</td>" +
            $"<td>{state.Inventory.Single(item => item.Id == recipe.ItemId).Quantity}</td>" +
            $"<td><form method=\"post\" action=\"/indexInternal.es?action=internalAssemblyCraft\">" +
            $"<input type=\"hidden\" name=\"recipe\" value=\"{recipe.Id}\"><button type=\"submit\"" +
            (state.AssemblyComponents < recipe.ComponentCost || state.Credits < recipe.CreditCost ? " disabled" : "") +
            ">Craft</button></form></td></tr>"));
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Assembly - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="assembly"><h1>Assembly</h1><p>Pilot: {{HtmlEncoder.Default.Encode(session.Username)}}</p><p>Local components: {{state.AssemblyComponents}} · Local credits: {{state.Credits}}</p><table><thead><tr><th>Item</th><th>Components</th><th>Credits</th><th>Owned</th><th></th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalDock">Hangar</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to menu</a></p></main></body></html>
            """;
    }
}
