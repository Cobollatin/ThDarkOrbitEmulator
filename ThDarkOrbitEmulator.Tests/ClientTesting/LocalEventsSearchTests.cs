using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-212: local battle-pass search through the catalog and saved status.
public class LocalEventsSearchTests
{
    private static LocalEquipmentState State()
    {
        var state = new LocalEquipmentState([], []) { Credits = 500, PurchasedItems = 1, CraftedItems = 1 };
        state.ClaimedBattlePassTiers.Add("supplier");
        return state;
    }

    [TestCase("action=internalEvents&q=Shop", true)]
    [TestCase("action=internalEvents&filter=ready&q=craft", true)]
    [TestCase("action=internalEvents&filter=ready&q=craft&detail=engineer", true)]
    [TestCase("action=internalEvents&q=%20%20", true)]
    [TestCase("action=internalEvents&q=x", false)]
    [TestCase("action=internalEvents&q=hi%3Cscript%3E", false)]
    [TestCase("action=internalEvents&q=good%0A", false)]
    [TestCase("action=internalEvents&q=Shop&q=Craft", false)]
    [TestCase("action=internalEvents&Q=Shop", false)]
    public void SearchQuery_ValidatesBeforeSession(string query, bool expected) =>
        Assert.That(LocalEventsAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void Search_CombinesWithStatusTabsAndKeepsContext()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, filter: "in-progress", search: "buy");
        Assert.That(page, Does.Contain("data-filter=\"all\">All (3)"));
        Assert.That(page, Does.Contain("data-filter=\"in-progress\" aria-current=\"page\">In progress (2)"));
        Assert.That(page, Does.Contain("<tr data-tier=\"quartermaster\" data-status=\"in-progress\">"));
        Assert.That(page, Does.Contain("<tr data-tier=\"tycoon\" data-status=\"in-progress\">"));
        Assert.That(page, Does.Not.Contain("<tr data-tier=\"supplier\""));
        Assert.That(page, Does.Contain("filter=in-progress&amp;q=buy&amp;detail=quartermaster"));
        Assert.That(page, Does.Contain("name=\"filter\" value=\"in-progress\""));
        Assert.That(page, Does.Contain("2 of 3 tiers match."));
        Assert.That(page, Does.Contain("Showing 2 of 3 tiers"));
        Assert.That(page, Does.Contain("filter=in-progress\">Clear search"));
        Assert.That(page, Does.Contain("1 of 5 tiers claimed"));
    }

    [Test]
    public void Search_MatchesRewardTextAndNavigatesWithinResult()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, detail: "engineer", search: "local credits");
        Assert.That(page, Does.Contain("Tier 2 of 5"));
        Assert.That(page, Does.Contain("q=local%20credits&amp;detail=supplier\">Previous"));
        Assert.That(page, Does.Contain("q=local%20credits&amp;detail=quartermaster\">Next"));
        Assert.That(LocalEvents.TierInFilter(State(), "supplier", "all", search: "craft"), Is.False);
    }

    [Test]
    public void EmptyResult_AndBlankSearch()
    {
        var empty = LocalEvents.Page(State(), DateTimeOffset.UtcNow, filter: "claimed", search: "craft");
        Assert.That(empty, Does.Contain("No claimed tiers match this search."));
        Assert.That(empty, Does.Contain("0 of 1 tiers match."));
        Assert.That(LocalEvents.Page(State(), DateTimeOffset.UtcNow, search: "  "),
            Is.EqualTo(LocalEvents.Page(State(), DateTimeOffset.UtcNow)));
    }

    [Test]
    public void AdminOfferSearch_UsesSavedCatalogNameAndReward()
    {
        EconomyRewardOffer[] offers = [new("battle_pass", "special", "Special reward", "credits", 900, "purchases", 2, true)];
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, offers, search: "900 credits");
        Assert.That(page, Does.Contain("data-tier=\"special\""));
        Assert.That(LocalEvents.TierInFilter(State(), "special", null, offers, "900 credits"), Is.True);
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
