using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-205: Tech Factory recipe search (`q`) by recipe id and name.
public class LocalTechFactorySearchTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "search-dosid" };
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<LocalTechRecipe> Recipes =
    [
        new("targeting", "Targeting boost", 100, 2),
        new("shield", "Shield boost", 150, 3),
        new("repair", "Repair charge", 200, 4),
        new("tech_x", "<Energy> & charge", 300, 6)
    ];

    private static LocalTechFactoryState State(int components = 150, LocalTechJob? job = null) =>
        new(components, new Dictionary<string, int>(), job);

    private static string Page(string? search, string? filter = null, LocalTechFactoryState? state = null,
        string? started = null) =>
        LocalTechFactory.Page(Session, state ?? State(), Now, Recipes, filter: filter, started: started, search: search);

    private static string[] RowIds(string page) =>
        Regex.Matches(page, "<tr data-recipe=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("shield", new[] { "shield" })]
    [TestCase("BOOST", new[] { "targeting", "shield" })]
    [TestCase("charge", new[] { "repair", "tech_x" })]
    [TestCase("tech_", new[] { "tech_x" })]
    [TestCase("<energy>", new[] { "tech_x" })]
    [TestCase("zz", new string[0])]
    public void Search_MatchesIdAndNameIgnoringCase(string search, string[] expected) =>
        Assert.That(RowIds(Page(search)), Is.EqualTo(expected));

    [Test]
    public void Search_CombinesWithFilterAndCountsTabsWithinResults()
    {
        var page = Page("boost", "shortfall", State(100));
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.EqualTo(new[] { "shield" }));
            Assert.That(page, Does.Contain("filter=all&amp;q=boost\">All (2)</a>"));
            Assert.That(page, Does.Contain("filter=affordable&amp;q=boost\">Affordable (1)</a>"));
            Assert.That(page, Does.Contain("filter=shortfall&amp;q=boost\" aria-current=\"page\">Shortfall (1)</a>"));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"filter\" value=\"shortfall\">"));
            // The affordability summary still counts the whole catalog.
            Assert.That(page, Does.Contain("1 of 4 recipe(s) affordable with 100 components."));
            Assert.That(page, Does.Contain("1 of 4 recipes match &quot;boost&quot;"));
            Assert.That(page, Does.Contain("id=\"techFactorySearchClear\" href=\"/indexInternal.es?action=internalTechFactory&amp;filter=shortfall\""));
        });
    }

    [Test]
    public void Search_WithoutMatches_ShowsSearchEmptyState()
    {
        var page = Page("zz");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("No recipes match this search."));
            Assert.That(page, Does.Contain("0 of 4 recipes match"));
            Assert.That(page, Does.Contain(">All (0)</a>"));
        });
    }

    [Test]
    public void NoSearch_ShowsEmptySearchBoxAndFilterEmptyState()
    {
        var page = Page(null, "shortfall", State(1000));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"techFactorySearch\""));
            Assert.That(page, Does.Contain("name=\"q\" maxlength=\"40\" aria-label=\"Search recipes\" value=\"\""));
            Assert.That(page, Does.Not.Contain("techFactorySearchSummary"));
            Assert.That(page, Does.Contain("No recipes match this filter."));
            Assert.That(page, Does.Contain("filter=all\">All (4)</a>"));
        });
    }

    [Test]
    public void EmptyCatalog_HidesSearchForm() =>
        Assert.That(LocalTechFactory.Page(Session, State(), Now, [], search: "boost"),
            Does.Not.Contain("techFactorySearch").And.Contain("No production recipes are enabled."));

    [Test]
    public void SearchText_IsEncodedInPageAndEscapedInLinks()
    {
        var page = Page("<a&\"b");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Not.Contain("<a&\"b"));
            Assert.That(page, Does.Contain("&amp;q=%3Ca%26%22b\""));
            Assert.That(page, Does.Contain("value=\"&lt;a&amp;&quot;b\""));
        });
    }

    [Test]
    public void Search_DetailLinksDropSearchAndNoticesUseWholeCatalog()
    {
        var job = new LocalTechJob("repair", Now.AddMinutes(4), Guid.NewGuid());
        var page = Page("boost", state: State(0, job), started: "repair");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalTechFactory&amp;detail=shield\""));
            Assert.That(page, Does.Contain("id=\"techFactoryStarted\""));
            Assert.That(page, Does.Contain("Producing Repair charge"));
            Assert.That(RowIds(page), Is.EqualTo(new[] { "targeting", "shield" }));
        });
    }

    [TestCase("?action=internalTechFactory&q=boost", true)]
    [TestCase("?action=internalTechFactory&q=bo", true)]
    [TestCase("?action=internalTechFactory&q=%20%20boost%20", true)]
    [TestCase("?action=internalTechFactory&q=boost&filter=affordable", true)]
    [TestCase("?action=internalTechFactory&q=boost&started=shield", true)]
    [TestCase("?action=internalTechFactory&q=", true)]
    [TestCase("?action=internalTechFactory&q=%20%20", true)]
    [TestCase("?action=internalTechFactory&q=&detail=shield", true)]
    [TestCase("?action=internalTechFactory&q=b", false)]
    [TestCase("?action=internalTechFactory&q=%20b%20", false)]
    [TestCase("?action=internalTechFactory&q=boost&detail=shield", false)]
    [TestCase("?action=internalTechFactory&q=boost&q=shield", false)]
    [TestCase("?action=internalTechFactory&Q=boost", false)]
    [TestCase("?action=internalTechFactory&q=bo%0Aost", false)]
    public void Query_ValidatesSearch(string query, bool valid) =>
        Assert.That(LocalTechFactoryAction.ValidPageQuery(Query(query)), Is.EqualTo(valid));

    [Test]
    public void Query_RejectsSearchLongerThanForty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory&q=" + new string('a', 40))), Is.True);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory&q=" + new string('a', 41))), Is.False);
        });
    }

    [TestCase("?action=internalTechFactory&q=%20boost%20", "boost")]
    [TestCase("?action=internalTechFactory&q=%20", null)]
    [TestCase("?action=internalTechFactory", null)]
    public void Search_IsTrimmedAndBlankMeansNone(string query, string? expected) =>
        Assert.That(LocalTechFactoryAction.Search(Query(query)), Is.EqualTo(expected));
}
