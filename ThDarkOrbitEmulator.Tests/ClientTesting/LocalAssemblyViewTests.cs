using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-55: Assembly status filters, "can craft" counts, and the per-recipe detail view.
public class LocalAssemblyViewTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "view-dosid" };

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    // lf3 is ready twice over, flax lacks a drone_iris, old is disabled, free has no inputs.
    private static readonly EconomyRecipe[] Recipes =
    [
        new("lf3", "LF-3 laser", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":150},{\"kind\":\"currency\",\"id\":\"credits\",\"quantity\":500}]"),
            Json("[{\"kind\":\"item\",\"id\":\"laser_lf3\",\"quantity\":1}]"), 0, true),
        new("flax", "<Flax>", Json("[{\"kind\":\"item\",\"id\":\"drone_iris\",\"quantity\":2},{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":100}]"),
            Json("[{\"kind\":\"item\",\"id\":\"drone_flax\",\"quantity\":1}]"), 0, true),
        new("old", "Retired", Json("[]"), Json("[{\"kind\":\"item\",\"id\":\"x\",\"quantity\":1}]"), 0, false),
        new("free", "Free sample", Json("[]"), Json("[{\"kind\":\"item\",\"id\":\"y\",\"quantity\":1}]"), 0, true)
    ];

    private static readonly EconomyBalance[] Balances =
        [new("components", "resource", 350), new("credits", "currency", 1200)];

    private static readonly Dictionary<string, long> Inventory = new() { ["drone_iris"] = 1 };

    private static string Page(string status = "all", string? recipe = null) =>
        LocalAssembly.PageFromDatabase(Session, Recipes, Balances, Inventory, status, recipe);

    private static string[] RowIds(string page) =>
        Regex.Matches(page, "<tr data-recipe=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("?action=internalAssembly&status=all")]
    [TestCase("?action=internalAssembly&status=ready")]
    [TestCase("?action=internalAssembly&status=missing")]
    [TestCase("?action=internalAssembly&status=unavailable")]
    [TestCase("?action=internalAssembly&detail=lf3")]
    [TestCase("?action=internalAssembly&detail=sg3n-b02_x")]
    [TestCase("?action=internalAssembly&status=missing&detail=flax&from=internalStart")]
    public void PageQuery_AcceptsViewKeys(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("?action=internalAssembly&status=")]
    [TestCase("?action=internalAssembly&status=Ready")]
    [TestCase("?action=internalAssembly&status=ready%20")]
    [TestCase("?action=internalAssembly&status=done")]
    [TestCase("?action=internalAssembly&status=ready&status=missing")]
    [TestCase("?action=internalAssembly&Status=ready")]
    [TestCase("?action=internalAssembly&detail=")]
    [TestCase("?action=internalAssembly&detail=LF3")]
    [TestCase("?action=internalAssembly&detail=lf3%3Cx%3E")]
    [TestCase("?action=internalAssembly&detail=lf3&detail=flax")]
    [TestCase("?action=internalAssembly&DETAIL=lf3")]
    [TestCase("?action=internalAssembly&detail=lf3%0A")]
    public void PageQuery_RejectsBadViewKeys(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void PageQuery_RejectsRecipeIdOverSixtyFourCharacters()
    {
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&detail={new string('a', 64)}")), Is.True);
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&detail={new string('a', 65)}")), Is.False);
    }

    [TestCase("all", new[] { "lf3", "flax", "old", "free" })]
    [TestCase("ready", new[] { "lf3", "free" })]
    [TestCase("missing", new[] { "flax" })]
    [TestCase("unavailable", new[] { "old" })]
    public void StatusFilter_ShowsOnlyMatchingRecipesInCatalogOrder(string status, string[] expected) =>
        Assert.That(RowIds(Page(status)), Is.EqualTo(expected));

    [Test]
    public void StatusFilter_TabsCountEachStatusAndMarkCurrent()
    {
        var page = Page("missing");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-status=\"all\">All (4)</a>"));
            Assert.That(page, Does.Contain("data-status=\"ready\">Ready (2)</a>"));
            Assert.That(page, Does.Contain("data-status=\"missing\" aria-current=\"page\">Missing (1)</a>"));
            Assert.That(page, Does.Contain("data-status=\"unavailable\">Unavailable (1)</a>"));
            Assert.That(Regex.Matches(page, "aria-current").Count, Is.EqualTo(1));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalAssembly\" data-status=\"all\""));
        });
    }

    [Test]
    public void StatusFilter_KeepsReadyNoticeForAllRecipes()
    {
        Assert.That(Page("unavailable"), Does.Contain("2 of 4 recipes ready."));
        Assert.That(Page(), Does.Contain("2 of 4 recipes ready."));
    }

    [Test]
    public void StatusFilter_UnknownValueFallsBackToAll() =>
        Assert.That(RowIds(Page("bogus")), Is.EqualTo(RowIds(Page())));

    [Test]
    public void StatusFilter_EmptyResultShowsMessage()
    {
        var page = LocalAssembly.PageFromDatabase(Session, Recipes.Take(1).ToArray(), Balances, Inventory, "unavailable");
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.Empty);
            Assert.That(page, Does.Contain("id=\"assembly-empty\""));
            Assert.That(Page(), Does.Not.Contain("assembly-empty"));
        });
    }

    [Test]
    public void List_ShowsCanCraftCountPerRecipe()
    {
        var page = Page();
        Assert.Multiple(() =>
        {
            // components 350/150 = 2, credits 1200/500 = 2.
            Assert.That(page, Does.Match("data-recipe=\"lf3\">.*?<td>Ready</td><td>2</td>"));
            Assert.That(page, Does.Match("data-recipe=\"flax\">.*?<td>Missing: drone_iris</td><td>0</td>"));
            Assert.That(page, Does.Match("data-recipe=\"old\">.*?<td>Unavailable</td><td>0</td>"));
            Assert.That(page, Does.Match("data-recipe=\"free\">.*?<td>Ready</td><td>Unlimited</td>"));
        });
    }

    [Test]
    public void List_CanCraftUsesScarcestInput()
    {
        var page = LocalAssembly.PageFromDatabase(Session, Recipes, [new("components", "resource", 1000), new("credits", "currency", 999)], Inventory);
        Assert.That(page, Does.Match("data-recipe=\"lf3\">.*?<td>Ready</td><td>1</td>"));
    }

    [Test]
    public void List_RecipeNamesLinkToDetailAndKeepFilter()
    {
        Assert.That(Page(), Does.Contain("href=\"/indexInternal.es?action=internalAssembly&amp;detail=lf3\">LF-3 laser</a>"));
        Assert.That(Page("ready"), Does.Contain("href=\"/indexInternal.es?action=internalAssembly&amp;status=ready&amp;detail=lf3\">"));
    }

    [Test]
    public void Detail_ShowsNeedHaveShortPerInput()
    {
        var page = Page(recipe: "flax");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"assembly-recipe\" data-recipe=\"flax\""));
            Assert.That(page, Does.Contain("<h2>&lt;Flax&gt;</h2>"));
            Assert.That(page, Does.Not.Contain("<Flax>"));
            Assert.That(page, Does.Contain("<tr data-input=\"drone_iris\"><td>drone_iris</td><td>item</td><td>2</td><td>1</td><td>1</td></tr>"));
            Assert.That(page, Does.Contain("<tr data-input=\"components\"><td>components</td><td>resource</td><td>100</td><td>350</td><td>0</td></tr>"));
            Assert.That(page, Does.Contain("Status: Missing: drone_iris"));
            Assert.That(page, Does.Contain("Can craft now: 0"));
            Assert.That(page, Does.Contain("Output: 1 drone_flax"));
            Assert.That(page, Does.Contain("disabled>Craft</button>"));
            Assert.That(RowIds(page), Is.Empty);
        });
    }

    [Test]
    public void Detail_ReadyRecipeHasOneEnabledSignedForm()
    {
        var page = Page(recipe: "lf3");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Can craft now: 2"));
            Assert.That(Regex.Matches(page, "name=\"key\"").Count, Is.EqualTo(1));
            Assert.That(page, Does.Contain("<button type=\"submit\">Craft</button>"));
            Assert.That(page, Does.Contain("name=\"recipe\" value=\"lf3\""));
        });
    }

    [Test]
    public void Detail_DisabledRecipeHasNoForm()
    {
        var page = Page(recipe: "old");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Status: Unavailable"));
            Assert.That(page, Does.Contain("Can craft now: 0"));
            Assert.That(page, Does.Not.Contain("name=\"key\""));
        });
    }

    [Test]
    public void Detail_BackLinkKeepsFilter()
    {
        Assert.That(Page("missing", "flax"), Does.Contain("id=\"assembly-back\" href=\"/indexInternal.es?action=internalAssembly&amp;status=missing\""));
        Assert.That(Page(recipe: "flax"), Does.Contain("id=\"assembly-back\" href=\"/indexInternal.es?action=internalAssembly\""));
    }

    [Test]
    public void HasRecipe_OnlyKnownIdsOrNoSelection()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalAssembly.HasRecipe(Recipes, null), Is.True);
            Assert.That(LocalAssembly.HasRecipe(Recipes, "old"), Is.True);
            Assert.That(LocalAssembly.HasRecipe(Recipes, "nope"), Is.False);
            Assert.That(LocalAssembly.HasRecipe(Recipes, "LF3"), Is.False);
        });
    }
}
