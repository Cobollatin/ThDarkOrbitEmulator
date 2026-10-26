using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-213: Space Search planet search (`q`) combined with filters and result-scoped detail navigation.
public class LocalSpaceSearchFindTests
{
    private static LocalEquipmentState State(int credits = 10000, params string[] searched) =>
        new([new LocalEquipmentItem("laser_lf3", "LF-3 laser", "laser", 4)], []) { Credits = credits, SearchedPlanetIds = [.. searched] };

    private static string[] RowIds(string page) =>
        Regex.Matches(page, "<tr data-planet=\"([a-z]+)\"").Select(match => match.Groups[1].Value).ToArray();

    private static string DetailSection(string page)
    {
        var start = page.IndexOf("<section id=\"planetDetail\"", StringComparison.Ordinal);
        return start < 0 ? "" : page[start..(page.IndexOf("</section>", start, StringComparison.Ordinal) + 10)];
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;

    [TestCase("laser", new[] { "mars", "jupiter" })]
    [TestCase("LASER", new[] { "mars", "jupiter" })]
    [TestCase("shield", new[] { "venus", "saturn" })]
    [TestCase("sat", new[] { "saturn" })]
    [TestCase("sg3n-a03", new[] { "saturn" })]
    [TestCase("Flax drone", new[] { "earth" })]
    [TestCase("pluto", new string[0])]
    public void Search_MatchesIdNameOrReward(string search, string[] expected) =>
        Assert.That(RowIds(LocalSpaceSearch.Page(State(), search: search)), Is.EqualTo(expected));

    [Test]
    public void Search_UsesOwnedInventoryName_LikeTheRows()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem("drone_flax", "Flax mk2", "drone", 1)], []);
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(LocalSpaceSearch.Page(state, search: "mk2")), Is.EqualTo(new[] { "earth" }));
            Assert.That(LocalSpaceSearch.Matches(state, LocalSpaceSearch.Find("earth")!, null), Is.True);
        });
    }

    [Test]
    public void OwnedRewardName_IsSearchableButHtmlEncodedInEveryProjection()
    {
        const string name = "<img src=x onerror=alert(1)>";
        var state = new LocalEquipmentState([new LocalEquipmentItem("laser_lf3", name, "laser", 4)], [])
        { SearchedPlanetIds = ["mars"] };

        var page = LocalSpaceSearch.Page(state, planetId: "mars", exploredPlanetId: "mars", search: "onerror");

        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.EqualTo(new[] { "mars" }));
            Assert.That(page, Does.Contain("id=\"spaceSearchExplored\""));
            Assert.That(page, Does.Contain("id=\"planetDetail\""));
            Assert.That(Regex.Matches(page, "&lt;img src=x onerror=alert\\(1\\)&gt;").Count, Is.EqualTo(3));
            Assert.That(page, Does.Not.Contain(name));
            Assert.That(page, Does.Not.Contain("<img"));
        });
    }

    [Test]
    public void Search_BlankOrPadded_IsNormalized()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(LocalSpaceSearch.Page(State(), search: "   ")), Has.Length.EqualTo(5));
            Assert.That(LocalSpaceSearch.Page(State(), search: "   "), Does.Not.Contain("id=\"planetSearchResult\""));
            Assert.That(RowIds(LocalSpaceSearch.Page(State(), search: "  laser ")), Is.EqualTo(new[] { "mars", "jupiter" }));
        });
    }

    [Test]
    public void Search_CombinesWithFilter_AndTabsCountMatches()
    {
        var page = LocalSpaceSearch.Page(State(10000, "mars"), "available", search: "laser");
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.EqualTo(new[] { "jupiter" }));
            Assert.That(page, Does.Contain(">All (2)</a>").And.Contain(">Available (1)</a>").And.Contain(">Searched (1)</a>"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;filter=searched&amp;q=laser\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;q=laser\" data-filter=\"all\""));
            Assert.That(page, Does.Contain("filter=available&amp;q=laser&amp;planet=jupiter\""));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"filter\" value=\"available\">"));
        });
    }

    [Test]
    public void Search_ShowsCount_ClearLink_AndEmptyState()
    {
        var page = LocalSpaceSearch.Page(State(), "searched", search: "Flax drone");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("1 of 5 planets match \"Flax drone\""));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalSpaceSearch&amp;filter=searched\">Clear search</a>"));
            Assert.That(page, Does.Contain("No planets match this search."));
            Assert.That(page, Does.Contain("value=\"Flax drone\""));
            Assert.That(page, Does.Contain("q=Flax%20drone"));
        });
    }

    [Test]
    public void Detail_NavigatesWithinSearchedAndFilteredResult()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(), "all", "mars", search: "laser"));
        var filtered = DetailSection(LocalSpaceSearch.Page(State(10000, "venus", "jupiter"), "available", "earth"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Planet 1 of 5"));
            Assert.That(detail, Does.Not.Contain("rel=\"prev\""));
            Assert.That(detail, Does.Contain("q=laser&amp;planet=jupiter\" rel=\"next\">Next: Jupiter</a>"));
            Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;q=laser\">Close</a>"));
            Assert.That(filtered, Does.Contain("filter=available&amp;planet=mars\" rel=\"prev\">Previous: Mars</a>"));
            Assert.That(filtered, Does.Contain("filter=available&amp;planet=saturn\" rel=\"next\">Next: Saturn</a>"));
        });
    }

    [Test]
    public void Detail_OutsideStatusFilter_WalksSearchResult()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(), "searched", "mars", search: "laser"));
        Assert.That(detail, Does.Contain("filter=searched&amp;q=laser&amp;planet=jupiter\" rel=\"next\">Next: Jupiter</a>"));
    }

    [Test]
    public void Detail_OutsideTextSearch_RemainsVisibleWithoutInventedNeighbors()
    {
        var page = LocalSpaceSearch.Page(State(), "available", "earth", search: "laser");
        var detail = DetailSection(page);
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.EqualTo(new[] { "mars", "jupiter" }));
            Assert.That(detail, Does.Contain("data-planet=\"earth\"").And.Contain("Flax drone"));
            Assert.That(detail, Does.Not.Contain("rel=\"prev\"").And.Not.Contain("rel=\"next\""));
            Assert.That(detail, Does.Contain("filter=available&amp;q=laser\">Close</a>"));
            Assert.That(page, Does.Contain(">All (2)</a>").And.Contain(">Available (2)</a>").And.Contain(">Searched (0)</a>"));
        });
    }

    [Test]
    public void Search_DoesNotChangeBudgetOrState()
    {
        var state = State(250, "mars");
        var page = LocalSpaceSearch.Page(state, search: "shield");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Remaining searches: 4 · Cost to finish: 400 local credits"));
            Assert.That(state.Credits, Is.EqualTo(250));
            Assert.That(state.SearchedPlanetIds, Is.EqualTo(new[] { "mars" }));
        });
    }

    [TestCase("action=internalSpaceSearch&q=laser", true)]
    [TestCase("action=internalSpaceSearch&q=%20laser%20", true)]
    [TestCase("action=internalSpaceSearch&q=", true)]
    [TestCase("action=internalSpaceSearch&q=%20%20", true)]
    [TestCase("action=internalSpaceSearch&q=sg3n-a03&filter=available&planet=saturn", true)]
    [TestCase("action=internalSpaceSearch&q=x", false)]
    [TestCase("action=internalSpaceSearch&q=%3Cb%3E", false)]
    [TestCase("action=internalSpaceSearch&q=mars%09", false)]
    [TestCase("action=internalSpaceSearch&q=laser&q=shield", false)]
    [TestCase("action=internalSpaceSearch&Q=laser", false)]
    [TestCase("action=internalSpaceSearch&q=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void SearchQuery_IsValidated(string query, bool valid) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.EqualTo(valid));

    [TestCase("action=internalSpaceSearch", null)]
    [TestCase("action=internalSpaceSearch&q=%20%20", null)]
    [TestCase("action=internalSpaceSearch&q=%20Mars%20", "Mars")]
    public void Search_IsTrimmed(string query, string? expected) =>
        Assert.That(LocalSpaceSearchAction.Search(Query(query)), Is.EqualTo(expected));
}
