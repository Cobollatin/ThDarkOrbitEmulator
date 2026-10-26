using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsDetailTests
{
    [TestCase("?action=internalEvents&detail=supplier", true)]
    [TestCase("?action=internalEvents&detail=", false)]
    [TestCase("?action=internalEvents&detail=supplier&detail=engineer", false)]
    [TestCase("?action=internalEvents&DETAIL=supplier", false)]
    [TestCase("?action=internalEvents&detail=%3Cscript%3E", false)]
    [TestCase("?action=internalEvents&detail=SUPPLIER", true)]
    public void DetailQuery_ValidatesShapeAndCardinality(string query, bool valid)
    {
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(valid));
    }

    [Test]
    public void Detail_ShowsProgressAndNavigation_WithoutClaimingEarly()
    {
        var state = new LocalEquipmentState([], []) { Credits = 10000, PurchasedItems = 2 };
        var page = LocalEvents.Page(state, DateTimeOffset.UtcNow, detail: "quartermaster");
        Assert.That(page, Does.Contain("id=\"battlePassDetail\""));
        Assert.That(page, Does.Contain("Tier 3 of 5"));
        Assert.That(page, Does.Contain("Progress: 2/3 · Remaining: 1"));
        Assert.That(page, Does.Contain("400 local credits"));
        Assert.That(page, Does.Contain("detail=engineer\">Previous"));
        Assert.That(page, Does.Contain("detail=artisan\">Next"));
        Assert.That(page, Does.Not.Contain("name=\"tier\" value=\"quartermaster\""));
    }

    [Test]
    public void Detail_ClaimFormFollowsProgressAndClaimState()
    {
        var state = new LocalEquipmentState([], []) { PurchasedItems = 1 };
        var ready = LocalEvents.Page(state, DateTimeOffset.UtcNow, detail: "supplier");
        Assert.That(ready, Does.Contain("Ready to claim"));
        Assert.That(ready, Does.Contain("name=\"tier\" value=\"supplier\""));
        state.ClaimedBattlePassTiers.Add("supplier");
        var claimed = LocalEvents.Page(state, DateTimeOffset.UtcNow, detail: "supplier");
        Assert.That(claimed, Does.Contain("Tier 1 of 5 · Claimed"));
        Assert.That(claimed, Does.Not.Contain("name=\"tier\" value=\"supplier\""));
    }

    [Test]
    public void AdminOfferDetails_UseCatalogValues_AndDisabledOfferHasNoForm()
    {
        var offers = new[]
        {
            new EconomyRewardOffer("battle_pass", "bronze", "Bronze &amp; more", "uridium", 50, "purchases", 2, true),
            new EconomyRewardOffer("battle_pass", "silver", "Silver", "credits", 75, "crafts", 1, false)
        };
        var state = new LocalEquipmentState([], []) { PurchasedItems = 2, CraftedItems = 1 };
        Assert.That(LocalEvents.HasTier("bronze", offers), Is.True);
        Assert.That(LocalEvents.HasTier("BRONZE", offers), Is.False);
        var ready = LocalEvents.Page(state, DateTimeOffset.UtcNow, offers, "bronze");
        Assert.That(ready, Does.Contain("50 uridium"));
        Assert.That(ready, Does.Contain("Bronze &amp;amp; more"));
        Assert.That(ready, Does.Contain("name=\"tier\" value=\"bronze\""));
        var disabled = LocalEvents.Page(state, DateTimeOffset.UtcNow, offers, "silver");
        Assert.That(disabled, Does.Contain("Tier 2 of 2 · Unavailable"));
        Assert.That(disabled, Does.Not.Contain("name=\"tier\" value=\"silver\""));
    }
}
