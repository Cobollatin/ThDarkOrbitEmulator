using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestCase("?action=internalEvents&claimed=supplier", true)]
    [TestCase("?action=internalEvents&claimed=unknown-tier_2", true)]
    [TestCase("?action=internalEvents&expedition=started", true)]
    [TestCase("?action=internalEvents&expedition=claimed", true)]
    [TestCase("?action=internalEvents&claimed=supplier&detail=engineer&from=internalStart", true)]
    [TestCase("?action=internalEvents&expedition=claimed&detail=supplier", true)]
    [TestCase("?action=internalEvents&Claimed=supplier", false)]
    [TestCase("?action=internalEvents&EXPEDITION=started", false)]
    [TestCase("?action=internalEvents&claimed=", false)]
    [TestCase("?action=internalEvents&claimed=a%20b", false)]
    [TestCase("?action=internalEvents&claimed=%3Cscript%3E", false)]
    [TestCase("?action=internalEvents&claimed=supplier&claimed=engineer", false)]
    [TestCase("?action=internalEvents&expedition=Started", false)]
    [TestCase("?action=internalEvents&expedition=launched", false)]
    [TestCase("?action=internalEvents&expedition=", false)]
    [TestCase("?action=internalEvents&expedition=started&expedition=claimed", false)]
    [TestCase("?action=internalEvents&claimed=supplier&expedition=started", false)]
    public void PageQuery_ValidatesNoticeKeys(string query, bool expected) =>
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected), query);

    [Test]
    public void PageQuery_ClaimedRespectsLengthLimit()
    {
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalEvents&claimed=" + new string('a', 64)))), Is.True);
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalEvents&claimed=" + new string('a', 65)))), Is.False);
    }

    [TestCase("internalBattlePassClaim", "supplier", "/indexInternal.es?action=internalEvents&claimed=supplier")]
    [TestCase("internalExpeditionStart", "1", "/indexInternal.es?action=internalEvents&expedition=started")]
    [TestCase("internalExpeditionClaim", "1", "/indexInternal.es?action=internalEvents&expedition=claimed")]
    public void SuccessLocation_IsAValidPageQuery(string action, string value, string expected)
    {
        var location = LocalEventsAction.SuccessLocation(action, value);
        Assert.That(location, Is.EqualTo(expected));
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery(location[location.IndexOf('?')..]))), Is.True);
    }

    [Test]
    public void ClaimedNotice_ShowsRewardBalanceAndNextReadyTier()
    {
        var state = new LocalEquipmentState([], []) { Credits = 10200, PurchasedItems = 1, CraftedItems = 1 };
        state.ClaimedBattlePassTiers.Add("supplier");
        var page = LocalEvents.Page(state, Now, claimedTier: "supplier");
        Assert.That(page, Does.Contain("id=\"eventsNotice\" role=\"status\" data-tier=\"supplier\""));
        Assert.That(page, Does.Contain("Claimed Buy one Shop item: +200 local credits. Balance: 10200 local credits. Tiers claimed: 1 of 5. Next ready: Craft one Assembly item."));
    }

    [Test]
    public void ClaimedNotice_SaysEveryTierClaimedWhenDone()
    {
        var state = new LocalEquipmentState([], []) { Credits = 12500, PurchasedItems = 10, CraftedItems = 3 };
        state.ClaimedBattlePassTiers.AddRange(LocalEvents.Tiers.Select(tier => tier.Id));
        var page = LocalEvents.Page(state, Now, claimedTier: "tycoon");
        Assert.That(page, Does.Contain("Tiers claimed: 5 of 5. Every tier has been claimed."));
        Assert.That(page, Does.Not.Contain("Next ready"));
    }

    [Test]
    public void ClaimedNotice_OmitsNextWhenNothingElseIsReady()
    {
        var state = new LocalEquipmentState([], []) { PurchasedItems = 1 };
        state.ClaimedBattlePassTiers.Add("supplier");
        var page = LocalEvents.Page(state, Now, claimedTier: "supplier");
        Assert.That(page, Does.Contain("Tiers claimed: 1 of 5.</p>"));
    }

    [TestCase("supplier")]
    [TestCase("unknown")]
    public void ClaimedNotice_HiddenUnlessSavedStateHasTheClaim(string tier)
    {
        var state = new LocalEquipmentState([], []) { PurchasedItems = 1 };
        Assert.That(LocalEvents.Page(state, Now, claimedTier: tier), Does.Not.Contain("eventsNotice"));
    }

    [Test]
    public void StartedNotice_ShowsReadyTimeWhileRunning()
    {
        var state = new LocalEquipmentState([], []) { Credits = 9900, ExpeditionReadyAt = Now.AddMinutes(2) };
        var page = LocalEvents.Page(state, Now, expeditionNotice: "started");
        Assert.That(page, Does.Contain("data-expedition=\"started\""));
        Assert.That(page, Does.Contain("Expedition launched for 100 local credits. Ready at 2026-10-24 12:02 UTC (2 min left). Balance: 9900 local credits."));
    }

    [Test]
    public void StartedNotice_SaysReadyOnceTimeHasPassed()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem(LocalEvents.ExpeditionRewardItemId,
            "SG3N-B02 shield", "Equipment", 4, "shield")], [])
            { Credits = 9900, ExpeditionReadyAt = Now };
        Assert.That(LocalEvents.Page(state, Now, expeditionNotice: "started"), Does.Contain("It is ready to claim now."));
    }

    [Test]
    public void StartedNotice_ExplainsBlockedRewardWithoutOfferingClaim()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem(LocalEvents.ExpeditionRewardItemId,
            "SG3N-B02 shield", "Equipment", int.MaxValue, "shield")], [])
            { Credits = 9900, ExpeditionReadyAt = Now };
        var page = LocalEvents.Page(state, Now, expeditionNotice: "started");
        Assert.That(page, Does.Contain("data-expedition=\"started\""));
        Assert.That(page, Does.Contain("reward cannot be added to your Hangar inventory"));
        Assert.That(page, Does.Not.Contain("name=\"claim\""));
    }

    [Test]
    public void StartedNotice_HiddenWhenNotLaunchedOrAlreadyClaimed()
    {
        Assert.That(LocalEvents.Page(new LocalEquipmentState([], []), Now, expeditionNotice: "started"), Does.Not.Contain("eventsNotice"));
        var claimed = new LocalEquipmentState([], []) { ExpeditionReadyAt = Now, ExpeditionClaimed = true };
        Assert.That(LocalEvents.Page(claimed, Now, expeditionNotice: "started"), Does.Not.Contain("eventsNotice"));
    }

    [Test]
    public void ClaimedExpeditionNotice_ShowsOwnedShields()
    {
        var state = new LocalEquipmentState([new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 5)], [])
        { Credits = 9900, ExpeditionReadyAt = Now, ExpeditionClaimed = true };
        var page = LocalEvents.Page(state, Now, expeditionNotice: "claimed");
        Assert.That(page, Does.Contain("Expedition reward claimed: 1 SG3N-B02 shield (owned: 5). Balance: 9900 local credits."));
        Assert.That(LocalEvents.Page(new LocalEquipmentState([], []) { ExpeditionReadyAt = Now }, Now, expeditionNotice: "claimed"),
            Does.Not.Contain("eventsNotice"));
    }

    [Test]
    public async Task FullFlow_RedirectsToNoticesBuiltFromSavedState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        await store.PurchaseAsync(session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var clock = new ManualClock(Now);

        var claim = await PostAsync(sessions, store, clock, session.Dosid, "internalBattlePassClaim", "tier=supplier");
        Assert.That(claim.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalEvents&claimed=supplier"));
        Assert.That(LocalEvents.Page(await store.GetAsync(session.AccountId, CancellationToken.None), clock.GetUtcNow(), claimedTier: "supplier"),
            Does.Contain("Claimed Buy one Shop item: +200 local credits."));

        var start = await PostAsync(sessions, store, clock, session.Dosid, "internalExpeditionStart", "start=1");
        Assert.That(start.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalEvents&expedition=started"));
        Assert.That(LocalEvents.Page(await store.GetAsync(session.AccountId, CancellationToken.None), clock.GetUtcNow(), expeditionNotice: "started"),
            Does.Contain("(2 min left)"));

        clock.Now = Now.AddMinutes(2);
        var collect = await PostAsync(sessions, store, clock, session.Dosid, "internalExpeditionClaim", "claim=1");
        Assert.That(collect.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalEvents&expedition=claimed"));
        Assert.That(LocalEvents.Page(await store.GetAsync(session.AccountId, CancellationToken.None), clock.GetUtcNow(), expeditionNotice: "claimed"),
            Does.Contain("Expedition reward claimed: 1 SG3N-B02 shield"));

        var again = await PostAsync(sessions, store, clock, session.Dosid, "internalBattlePassClaim", "tier=supplier");
        Assert.That(again.Response.StatusCode, Is.EqualTo(409));
        Assert.That(again.Response.Headers.Location.ToString(), Is.Empty);
    }

    [Test]
    public async Task RejectedExpeditionClaim_DoesNotRedirect()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var context = await PostAsync(sessions, store, new ManualClock(Now), session.Dosid, "internalExpeditionClaim", "claim=1");
        Assert.That(context.Response.StatusCode, Is.EqualTo(409));
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    private static async Task<DefaultHttpContext> PostAsync(GameAuthSessionStore sessions, LocalHangarEquipmentStore store,
        TimeProvider clock, string dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalEventsAction.HandleAsync(context, sessions, store, clock, CancellationToken.None);
        return context;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
