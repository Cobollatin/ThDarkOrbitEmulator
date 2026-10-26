using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestCase(true, false, 0, 1, "in-progress")]
    [TestCase(true, false, 1, 1, "ready")]
    [TestCase(true, true, 1, 1, "claimed")]
    [TestCase(false, false, 1, 1, "unavailable")]
    [TestCase(false, true, 1, 1, "claimed")]
    [TestCase(false, true, 0, 3, "claimed")]
    public void TierStatus_ClaimedWinsOverDisabled(bool enabled, bool claimed, long progress, long target, string expected)
    {
        Assert.That(LocalEvents.TierStatus(enabled, claimed, progress, target), Is.EqualTo(expected));
    }

    [Test]
    public void CachePage_MarksEachTierRowWithItsStatus()
    {
        var state = new LocalEquipmentState([], []) { PurchasedItems = 3, CraftedItems = 0 };
        state.ClaimedBattlePassTiers.Add("supplier");
        var page = LocalEvents.Page(state, Now);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-tier=\"supplier\" data-status=\"claimed\">"));
            Assert.That(page, Does.Contain("<tr data-tier=\"quartermaster\" data-status=\"ready\">"));
            Assert.That(page, Does.Contain("<tr data-tier=\"engineer\" data-status=\"in-progress\">"));
            Assert.That(page, Does.Contain("<tr data-tier=\"tycoon\" data-status=\"in-progress\">"));
            Assert.That(page, Does.Contain("name=\"tier\" value=\"quartermaster\""));
            Assert.That(page, Does.Contain("1 of 5 tiers claimed"));
        });
    }

    [Test]
    public void AdminOffer_ClaimedThenDisabled_StillShowsClaimed_AndCountsInSummary()
    {
        var offers = new[]
        {
            new EconomyRewardOffer("battle_pass", "bronze", "Bronze", "credits", 50, "purchases", 1, false),
            new EconomyRewardOffer("battle_pass", "silver", "Silver", "credits", 75, "purchases", 1, false),
            new EconomyRewardOffer("battle_pass", "gold", "Gold", "uridium", 20, "crafts", 2, true)
        };
        var state = new LocalEquipmentState([], []) { PurchasedItems = 1, CraftedItems = 1 };
        state.ClaimedBattlePassTiers.Add("bronze");
        var page = LocalEvents.Page(state, Now, offers);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-tier=\"bronze\" data-status=\"claimed\">"));
            Assert.That(page, Does.Contain("<tr data-tier=\"silver\" data-status=\"unavailable\">"));
            Assert.That(page, Does.Contain("<tr data-tier=\"gold\" data-status=\"in-progress\">"));
            // Bronze (claimed) and Gold (enabled) count; Silver (disabled, never claimed) does not.
            Assert.That(page, Does.Contain("1 of 2 tiers claimed"));
            Assert.That(page, Does.Not.Contain("name=\"tier\""));
        });
        var detail = LocalEvents.Page(state, Now, offers, "bronze");
        Assert.That(detail, Does.Contain("data-tier=\"bronze\" data-status=\"claimed\""));
        Assert.That(detail, Does.Contain("Tier 1 of 3 · Claimed"));
    }

    [Test]
    public void AdminOffer_RewardAndIdAreEncodedInRowsAndMarkers()
    {
        var offers = new[] { new EconomyRewardOffer("battle_pass", "a-b_1", "<b>Bold</b>", "cr<x>", 5, "purchases", 1, true) };
        var page = LocalEvents.Page(new LocalEquipmentState([], []) { PurchasedItems = 1 }, Now, offers);
        Assert.That(page, Does.Not.Contain("<b>Bold</b>"));
        Assert.That(page, Does.Not.Contain("cr<x>"));
        Assert.That(page, Does.Contain("data-tier=\"a-b_1\" data-status=\"ready\""));
    }

    [Test]
    public void Expedition_StatusFollowsCreditsTimerAndClaim()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem(LocalEvents.ExpeditionRewardItemId,
            "SG3N-B02 shield", "Equipment", 4, "shield")], []) { Credits = LocalEvents.ExpeditionCost };
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("available"));
        Assert.That(LocalEvents.Page(state, Now), Does.Contain("<div id=\"expedition\" data-status=\"available\"><form"));

        state.Credits = LocalEvents.ExpeditionCost - 1;
        var poor = LocalEvents.Page(state, Now);
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("insufficient-credits"));
        Assert.That(poor, Does.Contain("You need 1 more local credits to launch an expedition."));
        Assert.That(poor, Does.Not.Contain("name=\"start\""));

        state.Credits = 0;
        state.ExpeditionReadyAt = Now.AddMinutes(1);
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("running"), "a running expedition ignores the balance");
        Assert.That(LocalEvents.Page(state, Now), Does.Contain("data-status=\"running\">Ready at 2026-10-24 12:01 UTC (1 min left)"));
        Assert.That(LocalEvents.ExpeditionStatus(state, Now.AddMinutes(1)), Is.EqualTo("ready"));
        Assert.That(LocalEvents.Page(state, Now.AddMinutes(1)), Does.Contain("name=\"claim\" value=\"1\""));

        state.ExpeditionClaimed = true;
        Assert.That(LocalEvents.ExpeditionStatus(state, Now.AddMinutes(1)), Is.EqualTo("claimed"));
        Assert.That(LocalEvents.Page(state, Now.AddMinutes(1)), Does.Contain("data-status=\"claimed\">Reward claimed</div>"));
    }

    [Test]
    public void Expedition_ReadyRewardAtCapacity_HidesClaimUntilSpaceIsAvailable()
    {
        var shield = new LocalEquipmentItem(LocalEvents.ExpeditionRewardItemId, "SG3N-B02 shield",
            "Equipment", int.MaxValue, "shield");
        var state = new LocalEquipmentState([shield], []) { ExpeditionReadyAt = Now };
        var full = LocalEvents.Page(state, Now);
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("reward-unavailable"));
        Assert.That(full, Does.Contain("data-status=\"reward-unavailable\""));
        Assert.That(full, Does.Contain("Claim is unavailable"));
        Assert.That(full, Does.Not.Contain("name=\"claim\""));

        state.Inventory[0] = shield with { Quantity = int.MaxValue - 1 };
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("ready"));
        Assert.That(LocalEvents.Page(state, Now), Does.Contain("name=\"claim\" value=\"1\""));

        state.Inventory.Clear();
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("reward-unavailable"));
        state.ExpeditionClaimed = true;
        Assert.That(LocalEvents.ExpeditionStatus(state, Now), Is.EqualTo("claimed"));
    }
}
