using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-203: Assembly recipe search (`q`) by recipe id, name, and input/output ids.
public class LocalAssemblySearchTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "search-dosid" };

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private static readonly EconomyRecipe[] Recipes =
    [
        new("lf3", "LF-3 laser", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":150},{\"kind\":\"currency\",\"id\":\"credits\",\"quantity\":500}]"),
            Json("[{\"kind\":\"item\",\"id\":\"laser_lf3\",\"quantity\":1}]"), 0, true),
        new("flax", "<Flax> drone", Json("[{\"kind\":\"item\",\"id\":\"drone_iris\",\"quantity\":2}]"),
            Json("[{\"kind\":\"item\",\"id\":\"drone_flax\",\"quantity\":1}]"), 0, true),
        new("old", "Retired shield", Json("[]"), Json("[{\"kind\":\"item\",\"id\":\"shield_old\",\"quantity\":1}]"), 0, false)
    ];

    private static readonly EconomyBalance[] Balances =
        [new("components", "resource", 350), new("credits", "currency", 1200)];

    private static string Page(string? search, string status = "all") =>
        LocalAssembly.PageFromDatabase(Session, Recipes, Balances, new Dictionary<string, long>(), status, search: search);

    private static string[] RowIds(string page) =>
        Regex.Matches(page, "<tr data-recipe=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("lf3", new[] { "lf3" })]
    [TestCase("LASER", new[] { "lf3" })]
    [TestCase("drone", new[] { "flax" })]
    [TestCase("iris", new[] { "flax" })]
    [TestCase("credits", new[] { "lf3" })]
    [TestCase("shield", new[] { "old" })]
    [TestCase("zz", new string[0])]
    public void Search_MatchesIdNameInputsAndOutputs(string search, string[] expected) =>
        Assert.That(RowIds(Page(search)), Is.EqualTo(expected));

    [Test]
    public void Search_CombinesWithStatusAndCountsTabsWithinResults()
    {
        var page = Page("l", "unavailable");
        Assert.That(RowIds(page), Is.EqualTo(new[] { "old" }));
        Assert.That(page, Does.Contain("data-status=\"all\">All (3)</a>"));
        Assert.That(page, Does.Contain("&amp;status=ready&amp;q=l\" data-status=\"ready\">Ready (1)</a>"));
        Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"status\" value=\"unavailable\">"));

        var narrowed = Page("flax");
        Assert.That(narrowed, Does.Contain("data-status=\"unavailable\">Unavailable (0)</a>"));
        // The ready notice still counts the whole catalog.
        Assert.That(narrowed, Does.Contain("1 of 3 recipes ready."));
    }

    [Test]
    public void Search_ShowsSummaryClearLinkAndEmptyState()
    {
        var page = Page("drone");
        Assert.That(page, Does.Contain("id=\"assembly-search-summary\">1 of 3 recipes match &quot;drone&quot;"));
        Assert.That(page, Does.Contain("id=\"assembly-search-clear\" href=\"/indexInternal.es?action=internalAssembly\""));
        Assert.That(page, Does.Contain("value=\"drone\""));
        Assert.That(page, Does.Not.Contain("assembly-empty"));

        var empty = Page("zz");
        Assert.That(empty, Does.Contain("<p id=\"assembly-empty\">No recipes match this search.</p>"));

        var none = Page(null);
        Assert.That(none, Does.Contain("id=\"assembly-search\""));
        Assert.That(none, Does.Not.Contain("assembly-search-summary"));
    }

    [Test]
    public void Search_IsHtmlEncodedAndUrlEscapedInLinks()
    {
        var page = Page("<x> &");
        Assert.That(page, Does.Not.Contain("<x>"));
        Assert.That(page, Does.Contain("&quot;&lt;x&gt; &amp;&quot;"));
        Assert.That(page, Does.Contain("q=%3Cx%3E%20%26"));
        Assert.That(Page("drone"), Does.Contain("&lt;Flax&gt; drone"));
    }

    [TestCase("?action=internalAssembly&q=lf")]
    [TestCase("?action=internalAssembly&q=%20lf3%20")]
    [TestCase("?action=internalAssembly&q=")]
    [TestCase("?action=internalAssembly&q=%20%20")]
    [TestCase("?action=internalAssembly&q=&detail=lf3")]
    [TestCase("?action=internalAssembly&status=ready&q=laser&from=internalStart")]
    [TestCase("?action=internalAssembly&crafted=lf3&q=laser")]
    public void PageQuery_AcceptsSearch(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("?action=internalAssembly&q=x")]
    [TestCase("?action=internalAssembly&q=%20x%20")]
    [TestCase("?action=internalAssembly&q=lf3&detail=lf3")]
    [TestCase("?action=internalAssembly&q=lf3&q=flax")]
    [TestCase("?action=internalAssembly&Q=lf3")]
    [TestCase("?action=internalAssembly&q=lf%0A3")]
    public void PageQuery_RejectsBadSearch(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void PageQuery_SearchLengthIsCheckedAfterTrimming()
    {
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&q={new string('a', 40)}")), Is.True);
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&q=%20{new string('a', 40)}%20")), Is.True);
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&q={new string('a', 41)}")), Is.False);
        Assert.That(LocalAssemblyAction.Search(Query("?action=internalAssembly&q=%20laser%20")), Is.EqualTo("laser"));
        Assert.That(LocalAssemblyAction.Search(Query("?action=internalAssembly&q=%20")), Is.Null);
    }
}
