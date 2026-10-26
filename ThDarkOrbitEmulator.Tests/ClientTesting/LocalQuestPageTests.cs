using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalQuestPageTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    // 10 purchases, 1 craft, first_purchase claimed: claimed=1, ready=3 (field_supplier, bulk_buyer, first_craft),
    // in-progress=1 (master_engineer 1/5).
    private static LocalEquipmentState MixedState() => new([], [])
        { PurchasedItems = 10, CraftedItems = 1, ClaimedQuestIds = ["first_purchase"] };

    private static QueryCollection Query(string query) =>
        new(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));

    [TestCase("all")]
    [TestCase("ready")]
    [TestCase("in-progress")]
    [TestCase("claimed")]
    [TestCase("unavailable")]
    public void IsKnownFilter_AcceptsListedFilters(string filter) =>
        Assert.That(LocalQuests.IsKnownFilter(filter), Is.True);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("READY")]
    [TestCase("in_progress")]
    [TestCase("ready ")]
    [TestCase("<script>")]
    public void IsKnownFilter_RejectsOtherValues(string? filter) =>
        Assert.That(LocalQuests.IsKnownFilter(filter), Is.False);

    [TestCase("action=internalQuests&filter=ready", true)]
    [TestCase("action=internalQuests&filter=nope", true)]
    [TestCase("action=internalQuests&filter=ready&filter=claimed", false)]
    [TestCase("action=internalQuests&filter=%0Aready", false)]
    public void PageQuery_AllowsOneFilterValue(string query, bool expected) =>
        Assert.That(LocalQuestAction.ValidPageQuery(Query(query)), Is.EqualTo(expected), query);

    [Test]
    public void Page_DefaultShowsEveryRowWithStatusMarkersAndCounts()
    {
        var page = LocalQuests.Page(Session, MixedState());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<main id=\"quests\" data-filter=\"all\">"));
            Assert.That(page, Does.Contain("<title>Quests - DarkOrbit</title>"));
            Assert.That(page, Does.Contain("data-quest=\"first_purchase\" data-status=\"claimed\""));
            Assert.That(page, Does.Contain("data-quest=\"field_supplier\" data-status=\"ready\""));
            Assert.That(page, Does.Contain("data-quest=\"master_engineer\" data-status=\"in-progress\""));
            Assert.That(page, Does.Contain(">All (5)</strong>"));
            Assert.That(page, Does.Contain(">Ready (3)</a>"));
            Assert.That(page, Does.Contain(">In progress (1)</a>"));
            Assert.That(page, Does.Contain(">Claimed (1)</a>"));
            Assert.That(page, Does.Contain(">Unavailable (0)</a>"));
            Assert.That(page, Does.Contain("1 of 5 rewards claimed; 3 ready to claim"));
            Assert.That(page, Does.Not.Contain("questEmpty"));
        });
    }

    [Test]
    public void Page_ReadyFilterShowsOnlyClaimableRowsButKeepsTotals()
    {
        var page = LocalQuests.Page(Session, MixedState(), filter: "ready");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<main id=\"quests\" data-filter=\"ready\">"));
            Assert.That(page, Does.Contain("<title>Quests (ready) - DarkOrbit</title>"));
            Assert.That(page, Does.Contain("aria-current=\"page\"><strong>Ready (3)</strong>"));
            Assert.That(page, Does.Contain("data-status=\"ready\""));
            Assert.That(page, Does.Not.Contain("data-status=\"claimed\""));
            Assert.That(page, Does.Not.Contain("data-status=\"in-progress\""));
            Assert.That(page.Split("name=\"key\"").Length - 1, Is.EqualTo(3));
            Assert.That(page, Does.Contain("1 of 5 rewards claimed; 3 ready to claim"));
            // Achievements stay complete regardless of the list filter.
            Assert.That(page, Does.Contain("<li>First supply order: Unlocked</li>"));
            Assert.That(page, Does.Contain("<li>Master engineer: Locked</li>"));
        });
    }

    [Test]
    public void Page_InProgressFilterHasNoClaimForms()
    {
        var page = LocalQuests.Page(Session, MixedState(), filter: "in-progress");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Master engineer"));
            Assert.That(page, Does.Contain("In progress (4 more needed)"));
            Assert.That(page, Does.Not.Contain("name=\"key\""));
            Assert.That(page, Does.Not.Contain("data-quest=\"bulk_buyer\""));
        });
    }

    [Test]
    public void Page_EmptyFilterShowsEmptyNotice()
    {
        var page = LocalQuests.Page(Session, MixedState(), filter: "unavailable");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<p id=\"questEmpty\">No unavailable quests.</p>"));
            Assert.That(page, Does.Contain("<tbody></tbody>"));
        });
        var none = LocalQuests.Page(Session, MixedState(), filter: "in-progress");
        Assert.That(none, Does.Not.Contain("questEmpty"));
        var inProgressEmpty = LocalQuests.Page(Session, new LocalEquipmentState([], [])
            { PurchasedItems = 100, CraftedItems = 100 }, filter: "in-progress");
        Assert.That(inProgressEmpty, Does.Contain("No in progress quests."));
    }

    [Test]
    public void Page_UnknownFilterFallsBackToAll()
    {
        var page = LocalQuests.Page(Session, MixedState(), filter: "<b>x</b>");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-filter=\"all\">"));
            Assert.That(page, Does.Not.Contain("<b>x</b>"));
            Assert.That(page, Does.Contain("data-status=\"claimed\""));
        });
    }

    [Test]
    public void Page_FilterTabsLinkEveryFilterWithEncodedAmpersand()
    {
        var page = LocalQuests.Page(Session, MixedState());
        foreach (var filter in LocalQuests.Filters.Where(name => name != "all"))
            Assert.That(page, Does.Contain($"href=\"/indexInternal.es?action=internalQuests&amp;filter={filter}\" data-filter=\"{filter}\""), filter);
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalQuests\" data-filter=\"all\""));
    }

    [Test]
    public void Page_ShowsCompletionPercentClampedAndRoundedDown()
    {
        var page = LocalQuests.Page(Session, new LocalEquipmentState([], []) { PurchasedItems = 2, CraftedItems = int.MaxValue });
        Assert.Multiple(() =>
        {
            // field_supplier 2/3 = 66%, bulk_buyer 2/10 = 20%, master_engineer clamped to 5/5 = 100%.
            Assert.That(page, Does.Contain("<progress max=\"100\" value=\"66\">66%</progress> 66%"));
            Assert.That(page, Does.Contain("<progress max=\"100\" value=\"20\">20%</progress> 20%"));
            Assert.That(page, Does.Contain("<progress max=\"100\" value=\"100\">100%</progress> 100%"));
            Assert.That(page, Does.Not.Contain("value=\"-"));
        });
        var negative = LocalQuests.Page(Session, new LocalEquipmentState([], []) { PurchasedItems = -7, CraftedItems = -1 });
        Assert.That(negative, Does.Contain("Purchases: 0 · Crafts: 0"));
        Assert.That(negative, Does.Contain("value=\"0\">0%"));
    }

    [Test]
    public void Page_TotalsSumOnlyCreditRewardsByStatus()
    {
        var page = LocalQuests.Page(Session, MixedState());
        // Claimed: first_purchase 250. Ready: field_supplier 500 + first_craft 400 + bulk_buyer 1,000.
        Assert.That(page, Does.Contain("Purchases: 10 · Crafts: 1 · Credits claimed: 250 · Credits ready: 1,900"));

        var offers = new[]
        {
            new EconomyRewardOffer("quest", "uri", "Uri", "uridium", 50, "purchases", 1, true),
            new EconomyRewardOffer("quest", "cred", "Cred", "credits", 300, "crafts", 1, true),
            new EconomyRewardOffer("quest", "off", "Off", "credits", 900, "purchases", 1, false),
            new EconomyRewardOffer("quest", "free", "Free", "credits", 75, "none", 0, true)
        };
        var offerPage = LocalQuests.Page(Session, new LocalEquipmentState([], []) { PurchasedItems = 1, CraftedItems = 1 }, offers);
        Assert.Multiple(() =>
        {
            Assert.That(offerPage, Does.Contain("Credits claimed: 0 · Credits ready: 375"));
            Assert.That(offerPage, Does.Contain("data-quest=\"off\" data-status=\"unavailable\""));
            // A zero-target "none" offer is ready at once, matching the claim rule.
            Assert.That(offerPage, Does.Contain("data-quest=\"free\" data-status=\"ready\""));
            Assert.That(offerPage, Does.Contain("<td>0 / 0</td><td><progress max=\"100\" value=\"100\">"));
        });
    }

    [Test]
    public void Page_OfferIdsAreEncodedInRowMarkers()
    {
        var offers = new[] { new EconomyRewardOffer("quest", "a\"b", "Odd", "credits", 1, "purchases", 1, true) };
        var page = LocalQuests.Page(Session, new LocalEquipmentState([], []), offers, "in-progress");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-quest=\"a&quot;b\""));
            Assert.That(page, Does.Not.Contain("data-quest=\"a\"b\""));
        });
    }
}
