using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-211: battle pass status tabs on the Events page.
public class LocalEventsFilterTests
{
    // supplier claimed, engineer ready, quartermaster/artisan/tycoon in progress.
    private static LocalEquipmentState State()
    {
        var state = new LocalEquipmentState([], []) { Credits = 500, PurchasedItems = 1, CraftedItems = 1 };
        state.ClaimedBattlePassTiers.Add("supplier");
        return state;
    }

    [TestCase("action=internalEvents&filter=all")]
    [TestCase("action=internalEvents&filter=ready")]
    [TestCase("action=internalEvents&filter=in-progress")]
    [TestCase("action=internalEvents&filter=claimed")]
    [TestCase("action=internalEvents&filter=unavailable")]
    [TestCase("action=internalEvents&filter=ready&detail=engineer")]
    [TestCase("action=internalEvents&filter=claimed&claimed=supplier")]
    public void KnownFilter_IsAccepted(string query) =>
        Assert.That(LocalEventsAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("action=internalEvents&filter=")]
    [TestCase("action=internalEvents&filter=READY")]
    [TestCase("action=internalEvents&filter=open")]
    [TestCase("action=internalEvents&filter=ready&filter=claimed")]
    [TestCase("action=internalEvents&Filter=ready")]
    [TestCase("action=internalEvents&filter=ready%0A")]
    public void UnknownOrMalformedFilter_IsRejected(string query) =>
        Assert.That(LocalEventsAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void Tabs_CountEveryStatus_AndMarkCurrent()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, filter: "in-progress");
        Assert.That(page, Does.Contain("data-filter=\"all\">All (5)"));
        Assert.That(page, Does.Contain("data-filter=\"ready\">Ready (1)"));
        Assert.That(page, Does.Contain("data-filter=\"in-progress\" aria-current=\"page\">In progress (3)"));
        Assert.That(page, Does.Contain("data-filter=\"claimed\">Claimed (1)"));
        Assert.That(page, Does.Contain("data-filter=\"unavailable\">Unavailable (0)"));
        Assert.That(page, Does.Contain("Showing 3 of 5 tiers"));
    }

    [Test]
    public void FilteredList_ShowsOnlyMatchingRows_AndKeepsFilterInLinks()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, filter: "ready");
        Assert.That(page, Does.Contain("<tr data-tier=\"engineer\" data-status=\"ready\">"));
        Assert.That(page, Does.Not.Contain("<tr data-tier=\"supplier\""));
        Assert.That(page, Does.Not.Contain("<tr data-tier=\"tycoon\""));
        Assert.That(page, Does.Contain("action=internalEvents&amp;filter=ready&amp;detail=engineer"));
        // The claim summary still covers every tier.
        Assert.That(page, Does.Contain("1 of 5 tiers claimed"));
    }

    [Test]
    public void AllFilter_MatchesTheUnfilteredPage()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.That(LocalEvents.Page(State(), now, filter: "all"), Is.EqualTo(LocalEvents.Page(State(), now)));
        Assert.That(LocalEvents.Page(State(), now), Does.Contain("data-filter=\"all\" aria-current=\"page\""));
        Assert.That(LocalEvents.Page(State(), now), Does.Not.Contain("battlePassShowing"));
    }

    [Test]
    public void EmptyFilter_ShowsEmptyState()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, filter: "unavailable");
        Assert.That(page, Does.Contain("id=\"battlePassEmpty\""));
        Assert.That(page, Does.Contain("No unavailable tiers right now."));
        Assert.That(page, Does.Contain("Showing 0 of 5 tiers"));
    }

    [Test]
    public void FilteredDetail_NavigatesWithinTheFilter()
    {
        var page = LocalEvents.Page(State(), DateTimeOffset.UtcNow, detail: "artisan", filter: "in-progress");
        Assert.That(page, Does.Contain("In progress tier 2 of 3"));
        Assert.That(page, Does.Contain("filter=in-progress&amp;detail=quartermaster\">Previous"));
        Assert.That(page, Does.Contain("filter=in-progress&amp;detail=tycoon\">Next"));
        Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalEvents&amp;filter=in-progress\">Close</a>"));
    }

    [TestCase("supplier", null, true)]
    [TestCase("supplier", "all", true)]
    [TestCase("supplier", "claimed", true)]
    [TestCase("supplier", "ready", false)]
    [TestCase("engineer", "ready", true)]
    [TestCase("tycoon", "in-progress", true)]
    [TestCase("unknown", null, false)]
    public void TierInFilter_FollowsSavedStatus(string tier, string? filter, bool expected) =>
        Assert.That(LocalEvents.TierInFilter(State(), tier, filter), Is.EqualTo(expected));

    [Test]
    public void AdminOffers_DisabledUnclaimedTierIsUnavailable()
    {
        EconomyRewardOffer[] offers =
        [
            new("battle_pass", "supplier", "Buy one Shop item", "credits", 200, "purchases", 1, true),
            new("battle_pass", "engineer", "Craft one Assembly item", "credits", 300, "crafts", 1, false)
        ];
        var state = new LocalEquipmentState([], []) { CraftedItems = 1 };
        Assert.That(LocalEvents.TierInFilter(state, "engineer", "unavailable", offers), Is.True);
        var page = LocalEvents.Page(state, DateTimeOffset.UtcNow, offers, filter: "unavailable");
        Assert.That(page, Does.Contain("<tr data-tier=\"engineer\" data-status=\"unavailable\">"));
        Assert.That(page, Does.Contain("data-filter=\"unavailable\" aria-current=\"page\">Unavailable (1)"));
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
