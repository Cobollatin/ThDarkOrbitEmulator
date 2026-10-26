using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalQuestDetailTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "quest-session" };

    private static LocalEquipmentState State() => new([], [])
        { PurchasedItems = 10, CraftedItems = 1, ClaimedQuestIds = ["first_purchase"] };

    [Test]
    public void DetailShowsProgressRewardAndFilterRelativeNavigation()
    {
        var state = State();
        var page = LocalQuests.Page(Session, state, filter: "ready", detail: "first_craft");
        Assert.Multiple(() =>
        {
            Assert.That(LocalQuests.HasDetail(state, null, "ready", "first_craft"), Is.True);
            Assert.That(LocalQuests.HasDetail(state, null, "ready", "first_purchase"), Is.False);
            Assert.That(page, Does.Contain("id=\"questDetail\" data-quest=\"first_craft\""));
            Assert.That(page, Does.Contain("Quest 2 of 3 · ready"));
            Assert.That(page, Does.Contain("Progress: 1 / 1 (100%)"));
            Assert.That(page, Does.Contain("Remaining: 0 · Reward: 400 local credits"));
            Assert.That(page, Does.Contain("id=\"previousQuest\" href=\"/indexInternal.es?action=internalQuests&amp;filter=ready&amp;detail=field_supplier\""));
            Assert.That(page, Does.Contain("id=\"nextQuest\" href=\"/indexInternal.es?action=internalQuests&amp;filter=ready&amp;detail=bulk_buyer\""));
            Assert.That(page, Does.Contain("id=\"closeQuest\" href=\"/indexInternal.es?action=internalQuests&amp;filter=ready\""));
            Assert.That(page, Does.Contain("name=\"quest\" value=\"first_craft\""));
        });
    }

    [Test]
    public void ClaimedAndDisabledDetailsCannotBeClaimed()
    {
        var claimed = LocalQuests.Page(Session, State(), filter: "claimed", detail: "first_purchase");
        Assert.That(claimed, Does.Contain("id=\"questDetail\" data-quest=\"first_purchase\""));
        Assert.That(claimed, Does.Not.Contain("name=\"key\""));

        var offers = new[] { new EconomyRewardOffer("quest", "disabled", "Disabled", "uridium", 50, "purchases", 1, false) };
        var disabled = LocalQuests.Page(Session, State(), offers, "unavailable", "disabled");
        Assert.That(disabled, Does.Contain("Quest 1 of 1 · unavailable"));
        Assert.That(disabled, Does.Not.Contain("name=\"key\""));
    }

    [TestCase("action=internalQuests&detail=first_craft", true)]
    [TestCase("action=internalQuests&filter=ready&detail=first_craft", true)]
    [TestCase("action=internalQuests&detail=", false)]
    [TestCase("action=internalQuests&Detail=first_craft", false)]
    [TestCase("action=internalQuests&detail=first_craft&detail=bulk_buyer", false)]
    [TestCase("action=internalQuests&detail=first_craft&unknown=1", false)]
    public void DetailQueryRequiresOneCanonicalValue(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalQuestAction.ValidPageQuery(parsed), Is.EqualTo(expected));
    }

    [Test]
    public void OfferNameAndRewardAreHtmlEncoded()
    {
        var offers = new[] { new EconomyRewardOffer("quest", "safe", "<b>bonus</b>", "<img>", 1, "purchases", 1, true) };
        var page = LocalQuests.Page(Session, State(), offers, detail: "safe");
        Assert.That(page, Does.Contain("&lt;b&gt;bonus&lt;/b&gt;"));
        Assert.That(page, Does.Contain("1 &lt;img&gt;"));
        Assert.That(page, Does.Not.Contain("<img>"));
    }
}
