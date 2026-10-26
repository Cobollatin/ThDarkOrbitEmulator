using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-56: Space Search filters, remaining-cost summary, and the per-planet detail view.
public class LocalSpaceSearchViewTests
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

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [Test]
    public void DefaultPage_ListsEveryPlanet_InCatalogOrder_WithoutDetail()
    {
        var page = LocalSpaceSearch.Page(State());
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.EqualTo(new[] { "mars", "venus", "earth", "jupiter", "saturn" }));
            Assert.That(page, Does.Not.Contain("id=\"planetDetail\""));
            Assert.That(page, Does.Contain("data-filter=\"all\" aria-current=\"page\""));
        });
    }

    [TestCase("all", new[] { "mars", "venus", "earth", "jupiter", "saturn" })]
    [TestCase("", new[] { "mars", "venus", "earth", "jupiter", "saturn" })]
    [TestCase("available", new[] { "mars", "earth", "saturn" })]
    [TestCase("searched", new[] { "venus", "jupiter" })]
    public void Filter_SelectsRows(string filter, string[] expected) =>
        Assert.That(RowIds(LocalSpaceSearch.Page(State(10000, "venus", "jupiter"), filter)), Is.EqualTo(expected));

    [Test]
    public void FilterTabs_ShowCounts_AndMarkCurrent()
    {
        var page = LocalSpaceSearch.Page(State(10000, "mars", "earth"), "searched");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain(">All (5)</a>"));
            Assert.That(page, Does.Contain(">Available (3)</a>"));
            Assert.That(page, Does.Contain(">Searched (2)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"searched\" aria-current=\"page\""));
            Assert.That(page, Does.Not.Contain("data-filter=\"all\" aria-current"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;filter=available\""));
        });
    }

    [Test]
    public void EmptyFilterResult_ShowsNotice()
    {
        Assert.That(LocalSpaceSearch.Page(State(), "searched"), Does.Contain("No planets match this filter."));
        var all = LocalSpaceSearch.Catalog.Select(planet => planet.Id).ToArray();
        Assert.That(LocalSpaceSearch.Page(State(10000, all), "available"), Does.Contain("No planets match this filter."));
    }

    [Test]
    public void Rows_CarryStatus_AndLinkToDetail_KeepingFilter()
    {
        var page = LocalSpaceSearch.Page(State(50, "mars"), "available");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-planet=\"venus\" data-status=\"locked\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;filter=available&amp;planet=venus\""));
            Assert.That(LocalSpaceSearch.Page(State(10000, "mars")), Does.Contain("<tr data-planet=\"mars\" data-status=\"searched\"")
                .And.Contain("<tr data-planet=\"venus\" data-status=\"available\""));
        });
    }

    [Test]
    public void Budget_CoversRemaining_WhenBalanceIsEnough()
    {
        var page = LocalSpaceSearch.Page(State(500));
        Assert.That(page, Does.Contain("Remaining searches: 5 · Cost to finish: 500 local credits · Your balance covers every remaining search."));
    }

    [Test]
    public void Budget_ShowsShortfall_WhenBalanceIsLow()
    {
        var page = LocalSpaceSearch.Page(State(250, "mars"));
        Assert.That(page, Does.Contain("Remaining searches: 4 · Cost to finish: 400 local credits · You need 150 more local credits"));
    }

    [Test]
    public void Budget_ReportsCompletion()
    {
        var all = LocalSpaceSearch.Catalog.Select(planet => planet.Id).ToArray();
        var page = LocalSpaceSearch.Page(State(0, all));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Every planet has been searched."));
            Assert.That(page, Does.Not.Contain("<button"));
        });
    }

    [Test]
    public void Detail_AvailablePlanet_ShowsOutcome_AndSearchForm()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(1000), "all", "mars"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("data-planet=\"mars\" data-status=\"available\""));
            Assert.That(detail, Does.Contain("Planet 1 of 5 · Reward: LF-3 laser · Owned: 4 · Cost: 100 local credits"));
            Assert.That(detail, Does.Contain("Balance after search: 900 local credits · Owned after search: 5"));
            Assert.That(detail, Does.Contain("name=\"planet\" value=\"mars\""));
            Assert.That(detail, Does.Contain("action=\"/indexInternal.es?action=internalSpaceSearchExplore\""));
        });
    }

    [Test]
    public void Detail_UnownedReward_UsesCatalogName_AndZeroOwned()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(), "all", "earth"));
        Assert.That(detail, Does.Contain("Reward: Flax drone · Owned: 0").And.Contain("Owned after search: 1"));
    }

    [Test]
    public void Detail_SearchedPlanet_HasNoForm()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(10000, "venus"), "all", "venus"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("data-status=\"searched\""));
            Assert.That(detail, Does.Contain("cannot be searched again"));
            Assert.That(detail, Does.Not.Contain("<form"));
        });
    }

    [Test]
    public void Detail_LockedPlanet_ShowsShortfall_AndNoForm()
    {
        var detail = DetailSection(LocalSpaceSearch.Page(State(30), "all", "jupiter"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("data-status=\"locked\""));
            Assert.That(detail, Does.Contain("You need 70 more local credits."));
            Assert.That(detail, Does.Not.Contain("<form"));
        });
    }

    [Test]
    public void Detail_FullStack_HasNoForm()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem("laser_lf3", "LF-3 laser", "laser", int.MaxValue)], []);
        var detail = DetailSection(LocalSpaceSearch.Page(state, "all", "mars"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("cannot hold more of this item"));
            Assert.That(detail, Does.Not.Contain("<form"));
        });
    }

    [Test]
    public void Detail_NavigatesCatalog_WithPreviousNextAndClose()
    {
        var first = DetailSection(LocalSpaceSearch.Page(State(), "all", "mars"));
        var middle = DetailSection(LocalSpaceSearch.Page(State(), "available", "earth"));
        var last = DetailSection(LocalSpaceSearch.Page(State(), "all", "saturn"));
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Not.Contain("rel=\"prev\""));
            Assert.That(first, Does.Contain("planet=venus\" rel=\"next\">Next: Venus</a>"));
            Assert.That(middle, Does.Contain("filter=available&amp;planet=venus\" rel=\"prev\">Previous: Venus</a>"));
            Assert.That(middle, Does.Contain("filter=available&amp;planet=jupiter\" rel=\"next\">Next: Jupiter</a>"));
            Assert.That(middle, Does.Contain("href=\"/indexInternal.es?action=internalSpaceSearch&amp;filter=available\">Close</a>"));
            Assert.That(last, Does.Contain("Planet 5 of 5"));
            Assert.That(last, Does.Not.Contain("rel=\"next\""));
        });
    }

    [Test]
    public void Detail_HighlightsSelectedRow_AndWorksOutsideFilter()
    {
        var page = LocalSpaceSearch.Page(State(), "searched", "mars");
        Assert.Multiple(() =>
        {
            Assert.That(RowIds(page), Is.Empty);
            Assert.That(DetailSection(page), Does.Contain("data-planet=\"mars\""));
            Assert.That(LocalSpaceSearch.Page(State(), "all", "venus"), Does.Contain("<tr data-planet=\"venus\" data-status=\"available\" class=\"current\""));
        });
    }

    [Test]
    public void Page_DoesNotMutateState()
    {
        var state = State(500, "mars");
        LocalSpaceSearch.Page(state, "available", "venus");
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(500));
            Assert.That(state.SearchedPlanetIds, Is.EqualTo(new[] { "mars" }));
        });
    }

    [TestCase("action=internalSpaceSearch&filter=all", true)]
    [TestCase("action=internalSpaceSearch&filter=available", true)]
    [TestCase("action=internalSpaceSearch&filter=searched", true)]
    [TestCase("action=internalSpaceSearch&filter=", true)]
    [TestCase("action=internalSpaceSearch&planet=mars", true)]
    [TestCase("action=internalSpaceSearch&planet=pluto", true)]
    [TestCase("action=internalSpaceSearch&filter=searched&planet=saturn&lang=en", true)]
    [TestCase("action=internalSpaceSearch&filter=Searched", false)]
    [TestCase("action=internalSpaceSearch&filter=locked", false)]
    [TestCase("action=internalSpaceSearch&filter=all&filter=searched", false)]
    [TestCase("action=internalSpaceSearch&planet=", false)]
    [TestCase("action=internalSpaceSearch&planet=MARS", false)]
    [TestCase("action=internalSpaceSearch&planet=mars1", false)]
    [TestCase("action=internalSpaceSearch&planet=mars&planet=venus", false)]
    [TestCase("action=internalSpaceSearch&planet=%3Cscript%3E", false)]
    public void PageQuery_ValidatesFilterAndPlanet(string query, bool expected) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_RejectsOverlongPlanet()
    {
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query("action=internalSpaceSearch&planet=" + new string('a', 32))), Is.True);
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query("action=internalSpaceSearch&planet=" + new string('a', 33))), Is.False);
    }

    [Test]
    public void UnknownPlanet_IsNotInCatalog_SoEndpointReturns404() =>
        Assert.That(LocalSpaceSearch.Find("pluto"), Is.Null);
}
