using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-209: local Quests search and its interaction with status filters and details.
public class LocalQuestSearchTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    // claimed: first_purchase; ready: field_supplier, bulk_buyer, first_craft; in-progress: master_engineer.
    private static LocalEquipmentState MixedState() => new([], [])
        { PurchasedItems = 10, CraftedItems = 1, ClaimedQuestIds = ["first_purchase"] };

    private static IQueryCollection Query(string url) => new QueryCollection(QueryHelpers.ParseQuery(url));

    [TestCase("?action=internalQuests&q=buyer", true)]
    [TestCase("?action=internalQuests&filter=ready&q=bulk_buyer", true)]
    [TestCase("?action=internalQuests&q=", true)]
    [TestCase("?action=internalQuests&q=%20%20", true)]
    [TestCase("?action=internalQuests&q=%20First%20", true)]
    [TestCase("?action=internalQuests&q=first&detail=first_craft", true)]
    [TestCase("?action=internalQuests&q=f", false)]
    [TestCase("?action=internalQuests&q=first&q=bulk", false)]
    [TestCase("?action=internalQuests&Q=first", false)]
    [TestCase("?action=internalQuests&q=first%0A", false)]
    [TestCase("?action=internalQuests&q=%3Cscript%3E", false)]
    [TestCase("?action=internalQuests&q=f%C3%A9eld", false)]
    public void Query_ValidatesSearch(string url, bool expected) =>
        Assert.That(LocalQuestAction.ValidPageQuery(Query(url)), Is.EqualTo(expected), url);

    [Test]
    public void Query_EnforcesFortyCharacterLimit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalQuestAction.ValidPageQuery(Query("?action=internalQuests&q=" + new string('a', 40))), Is.True);
            Assert.That(LocalQuestAction.ValidPageQuery(Query("?action=internalQuests&q=" + new string('a', 41))), Is.False);
        });
    }

    [Test]
    public void Search_NormalizesBlankAndTrims()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalQuestAction.Search(Query("?action=internalQuests&q=%20First%20")), Is.EqualTo("First"));
            Assert.That(LocalQuestAction.Search(Query("?action=internalQuests&q=%20%20")), Is.Null);
            Assert.That(LocalQuestAction.Search(Query("?action=internalQuests")), Is.Null);
        });
    }

    [Test]
    public void Search_MatchesNameIdAndRewardAndCountsTabs()
    {
        var html = LocalQuests.Page(Session, MixedState(), search: "FIRST");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("data-quest=\"first_purchase\""));
            Assert.That(html, Does.Contain("data-quest=\"first_craft\""));
            Assert.That(html, Does.Not.Contain("data-quest=\"bulk_buyer\""));
            Assert.That(html, Does.Contain("All (2)"));
            Assert.That(html, Does.Contain("Ready (1)"));
            Assert.That(html, Does.Contain("Claimed (1)"));
            Assert.That(html, Does.Contain("In progress (0)"));
            Assert.That(html, Does.Contain("2 of 5 quests match."));
            Assert.That(html, Does.Contain("action=internalQuests&amp;filter=ready&amp;q=FIRST\" data-filter=\"ready\""));
            Assert.That(html, Does.Contain("value=\"FIRST\""));
        });
        Assert.That(LocalQuests.Page(Session, MixedState(), search: "bulk_buyer"), Does.Contain("<tr data-quest=\"bulk_buyer\""));
        Assert.That(LocalQuests.Page(Session, MixedState(), search: "1500 local"), Does.Contain("<tr data-quest=\"master_engineer\""));
    }

    [Test]
    public void Search_WithFilterKeepsFilterInFormAndClearLink()
    {
        var html = LocalQuests.Page(Session, MixedState(), filter: "ready", search: "first");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<tr data-quest=\"first_craft\""));
            Assert.That(html, Does.Not.Contain("<tr data-quest=\"first_purchase\""));
            Assert.That(html, Does.Contain("name=\"filter\" value=\"ready\""));
            Assert.That(html, Does.Contain("1 of 3 quests match."));
            Assert.That(html, Does.Contain("action=internalQuests&amp;filter=ready\">Clear search"));
            Assert.That(html, Does.Contain("filter=ready&amp;q=first&amp;detail=first_craft\""));
        });
    }

    [Test]
    public void Search_EmptyResultShowsSearchEmptyState()
    {
        var html = LocalQuests.Page(Session, MixedState(), search: "nothing here");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("No quests match this search."));
            Assert.That(html, Does.Contain("0 of 5 quests match."));
            Assert.That(html, Does.Not.Contain("<tr data-quest="));
            Assert.That(html, Does.Contain("value=\"nothing here\""));
        });
    }

    [Test]
    public void Search_FilterWithNoMatchesNamesTheSelectedStatus()
    {
        var html = LocalQuests.Page(Session, MixedState(), filter: "claimed", search: "bulk");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("No claimed quests match this search."));
            Assert.That(html, Does.Contain("0 of 1 quests match."));
            Assert.That(html, Does.Contain("In progress (0)"));
            Assert.That(html, Does.Contain("Ready (1)"));
            Assert.That(html, Does.Not.Contain("<tr data-quest="));
        });
    }

    [Test]
    public void Search_DetailStaysWithinResults()
    {
        var state = MixedState();
        Assert.Multiple(() =>
        {
            Assert.That(LocalQuests.HasDetail(state, null, "all", "first_craft", "first"), Is.True);
            Assert.That(LocalQuests.HasDetail(state, null, "all", "bulk_buyer", "first"), Is.False);
            Assert.That(LocalQuests.HasDetail(state, null, "all", "bulk_buyer", null), Is.True);
        });
        var html = LocalQuests.Page(Session, state, detail: "first_purchase", search: "first");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Quest 1 of 2"));
            Assert.That(html, Does.Contain("id=\"nextQuest\" href=\"/indexInternal.es?action=internalQuests&amp;q=first&amp;detail=first_craft\""));
            Assert.That(html, Does.Contain("id=\"closeQuest\" href=\"/indexInternal.es?action=internalQuests&amp;q=first\""));
        });
    }

    [Test]
    public void Search_LeavesTotalsAndAchievementsUnfiltered()
    {
        var html = LocalQuests.Page(Session, MixedState(), search: "first");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("1 of 5 rewards claimed; 3 ready to claim."));
            Assert.That(html, Does.Contain("<li>Bulk buyer: Locked</li>"));
            Assert.That(html, Does.Contain("Purchases: 10 · Crafts: 1"));
        });
    }

    [Test]
    public void Page_WithoutSearchHasFormButNoSummary()
    {
        var html = LocalQuests.Page(Session, MixedState(), search: "  ");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"questSearch\""));
            Assert.That(html, Does.Not.Contain("questSearchSummary"));
            Assert.That(html, Does.Contain("All (5)"));
            Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalQuests\" data-filter=\"all\""));
        });
    }
}
