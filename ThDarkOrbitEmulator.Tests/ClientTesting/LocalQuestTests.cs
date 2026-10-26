using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalQuestTests
{
    [Test]
    public void NpcKillOffers_ShowStreunerAndSectorProgress()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "session" };
        var state = new LocalEquipmentState([], []) { NpcKills = 3, StreunerKills = 1 };
        var offers = new EconomyRewardOffer[]
        {
            new("quest", "first_streuner", "First Streuner", "credits", 500, "npc_kills", 1, true),
            new("quest", "sector_patrol", "Sector patrol", "credits", 1500, "npc_kills", 5, true)
        };
        var page = LocalQuests.Page(session, state, offers);
        Assert.That(page, Does.Contain("data-quest=\"first_streuner\" data-status=\"ready\""));
        Assert.That(page, Does.Contain("data-quest=\"sector_patrol\" data-status=\"in-progress\""));
        Assert.That(page, Does.Contain("3 / 5"));
    }
    [Test]
    public async Task PurchaseAndCraftProgress_ClaimOnceIntoSharedBalance_AccountScoped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.ClaimQuestAsync(account, "first_purchase", CancellationToken.None), Is.False);
        Assert.That(await store.PurchaseAsync(account, "drone_flax", 3, CancellationToken.None), Is.True);
        Assert.That(await store.CraftAsync(account, "lf3", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(account, "first_purchase", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(account, "field_supplier", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(account, "first_craft", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(account, "first_purchase", CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.PurchasedItems, Is.EqualTo(3));
            Assert.That(state.CraftedItems, Is.EqualTo(1));
            Assert.That(state.Credits, Is.EqualTo(9150));
            Assert.That(state.ClaimedQuestIds, Is.EquivalentTo(new[] { "first_purchase", "field_supplier", "first_craft" }));
        });
        var other = await store.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.That(other.Credits, Is.EqualTo(10000));
        Assert.That(other.ClaimedQuestIds, Is.Empty);
    }

    [Test]
    public async Task ClaimAction_RequiresSessionCompanyAndCompletedQuest()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "quest=first_purchase");
        await LocalQuestAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "quest=first_purchase");
        await LocalQuestAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var incomplete = Request(session.Dosid, "quest=first_purchase");
        await LocalQuestAction.HandleAsync(incomplete, sessions, store, CancellationToken.None);
        Assert.That(incomplete.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        var valid = Request(session.Dosid, Signed(session.Dosid, "first_purchase"));
        await LocalQuestAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=first_purchase"));
        var duplicate = Request(session.Dosid, "quest=first_purchase");
        await LocalQuestAction.HandleAsync(duplicate, sessions, store, CancellationToken.None);
        Assert.That(duplicate.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public void Page_ShowsProgressAndEscapesPilot()
    {
        var page = LocalQuests.Page(new GameAuthSession { Username = "<pilot>" },
            new LocalEquipmentState([], []) { PurchasedItems = 1 });
        Assert.That(page, Does.Contain("id=\"quests\""));
        Assert.That(page, Does.Contain("action=internalQuestClaim"));
        Assert.That(page, Does.Contain("Achievements"));
        Assert.That(page, Does.Contain("&lt;pilot&gt;"));
        Assert.That(page, Does.Not.Contain("<pilot>"));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalQuests"));
    }

    [Test]
    public async Task ClaimAction_RejectsMalformedTamperedForeignAndExpiredForms()
    {
        var (sessions, store, session, now) = await CompanySessionAsync();
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        var other = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other", null, 8), null, CancellationToken.None)).Session;
        var key = Uri.EscapeDataString(LocalQuestAction.CreateFormKey(session.Dosid, "first_purchase", now));
        var raw = LocalQuestAction.CreateFormKey(session.Dosid, "first_purchase", now);
        var tampered = Uri.EscapeDataString(raw[..^1] + (raw[^1] == 'a' ? 'b' : 'a'));
        string[] badBodies =
        [
            "",
            "quest=first_purchase",
            $"key={key}",
            $"quest=first_purchase&quest=first_purchase&key={key}",
            $"quest=first_purchase&key={key}&key={key}",
            $"quest=first_purchase&key={key}&quantity=1",
            $"quest=first_purchase&key={key}&amount=-1",
            $"quest=first_purchase&key={key}&amount=99999999999999999999",
            $"quest=FIRST_PURCHASE&key={key}",
            $"quest=field_supplier&key={key}",
            $"quest={new string('a', 65)}&key={key}",
            $"quest=first_purchase&key={tampered}",
            $"quest=first_purchase&key={new string('9', 200)}",
            "quest=first_purchase&key=legacy",
            Signed(other.Dosid, "first_purchase", now),
            Signed(session.Dosid, "first_purchase", now.AddHours(1))
        ];
        foreach (var body in badBodies)
        {
            var context = Request(session.Dosid, body);
            await LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None);
            Assert.That(context.Response.StatusCode, Is.EqualTo(400), body);
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"), body);
        }
        var extraQuery = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now), "?action=internalQuestClaim&quest=x");
        await LocalQuestAction.HandleAsync(extraQuery, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(extraQuery.Response.StatusCode, Is.EqualTo(400));
        var expired = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now.AddHours(-2)));
        await LocalQuestAction.HandleAsync(expired, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(409));
        var unknown = Request(session.Dosid, Signed(session.Dosid, "no_such_quest", now));
        await LocalQuestAction.HandleAsync(unknown, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).ClaimedQuestIds, Is.Empty);
    }

    [Test]
    public async Task ClaimAction_ExpiredSessionAndWrongCompany_ReturnHtmlErrors()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var unknownSession = Request("expired-dosid", Signed("expired-dosid", "first_purchase"));
        await LocalQuestAction.HandleAsync(unknownSession, sessions, store, CancellationToken.None);
        Assert.That(unknownSession.Response.StatusCode, Is.EqualTo(401));
        Assert.That(unknownSession.Response.ContentType, Does.StartWith("text/html"));
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), Dosid = "dosid-x", Company = "not-a-company" };
        var context = Request(session.Dosid, Signed(session.Dosid, "first_purchase"));
        await LocalQuestAction.HandleAsync(context, new FixedSessionStore(session), store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(403));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
    }

    [Test]
    public async Task ClaimAction_DoubleSubmitAndConcurrentClaims_GrantOnce()
    {
        var (sessions, store, session, now) = await CompanySessionAsync();
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        var before = await store.GetAsync(session.AccountId, CancellationToken.None);
        var contexts = Enumerable.Range(0, 8)
            .Select(_ => Request(session.Dosid, Signed(session.Dosid, "first_purchase", now))).ToArray();
        await Task.WhenAll(contexts.Select(context =>
            LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None)));
        Assert.That(contexts.Count(c => c.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That(contexts.Count(c => c.Response.StatusCode == 409), Is.EqualTo(7));
        var after = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(after.Credits, Is.EqualTo(before.Credits + 250));
            Assert.That(after.ClaimedQuestIds, Is.EqualTo(new[] { "first_purchase" }));
            Assert.That(after.Inventory, Is.EqualTo(before.Inventory));
        });
        var incomplete = Request(session.Dosid, Signed(session.Dosid, "field_supplier", now));
        await LocalQuestAction.HandleAsync(incomplete, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(incomplete.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await store.ClaimQuestResultAsync(session.AccountId, "field_supplier", CancellationToken.None),
            Is.EqualTo(QuestClaimResult.Incomplete));
        Assert.That(await store.ClaimQuestResultAsync(session.AccountId, "first_purchase", CancellationToken.None),
            Is.EqualTo(QuestClaimResult.AlreadyClaimed));
        Assert.That(await store.ClaimQuestResultAsync(session.AccountId, "../quest", CancellationToken.None),
            Is.EqualTo(QuestClaimResult.NotFound));
        Assert.That(await store.ClaimQuestResultAsync(Guid.Empty, "first_purchase", CancellationToken.None),
            Is.EqualTo(QuestClaimResult.NotFound));
    }

    [Test]
    public async Task CacheExpiryResetsProgressWithClaims_AndCorruptProgressNeverGoesNegative()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalHangarEquipmentStore(cache);
        var account = Guid.NewGuid();
        Assert.That(await store.PurchaseAsync(account, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(account, "first_purchase", CancellationToken.None), Is.True);
        // Without Postgres the cache is the only store: expiry restores the starter state
        // (progress and claims together), so no reward can be claimed without new progress.
        await cache.RemoveAsync($"localhangar:equipment:{account:N}");
        Assert.That(await store.ClaimQuestResultAsync(account, "first_purchase", CancellationToken.None),
            Is.EqualTo(QuestClaimResult.Incomplete));
        var page = LocalQuests.Page(new GameAuthSession { Username = "pilot", Dosid = "d" },
            new LocalEquipmentState([], []) { PurchasedItems = -5, CraftedItems = int.MaxValue });
        Assert.That(page, Does.Contain("<td>0 / 1</td>"));
        Assert.That(page, Does.Contain("<td>5 / 5</td>"));
        Assert.That(page, Does.Not.Contain("-5"));
    }

    [Test]
    public void Page_ListsRicherCatalogNoticesAndSignedForms()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "dosid-1" };
        var page = LocalQuests.Page(session, new LocalEquipmentState([], [])
            { PurchasedItems = 10, CraftedItems = 1, ClaimedQuestIds = ["first_purchase"] });
        Assert.Multiple(() =>
        {
            Assert.That(LocalQuests.Catalog.Select(q => q.Id), Is.Unique);
            Assert.That(LocalQuests.Catalog.All(q => LocalQuests.IsWellFormedId(q.Id) && q.Target > 0 && q.RewardCredits > 0));
            Assert.That(page, Does.Contain("Bulk buyer"));
            Assert.That(page, Does.Contain("Master engineer"));
            Assert.That(page, Does.Contain("1 of 5 rewards claimed; 3 ready to claim"));
            Assert.That(page, Does.Contain("name=\"key\""));
            Assert.That(page, Does.Contain("<td>1 / 5</td>"));
        });
        var offers = new[]
        {
            new EconomyRewardOffer("quest", "first_purchase", "First supply order", "credits", 250, "purchases", 1, true),
            new EconomyRewardOffer("quest", "uri", "<b>Uri</b>", "uridium", 50, "purchases", 1, false)
        };
        var offerPage = LocalQuests.Page(session, new LocalEquipmentState([], []) { PurchasedItems = 1 }, offers);
        Assert.That(offerPage, Does.Contain("50 uridium"));
        Assert.That(offerPage, Does.Contain("Unavailable"));
        Assert.That(offerPage, Does.Contain("&lt;b&gt;Uri&lt;/b&gt;"));
        Assert.That(offerPage, Does.Contain("0 of 2 rewards claimed; 1 ready to claim"));
    }

    private static async Task<(GameAuthSessionStore, LocalHangarEquipmentStore, GameAuthSession, DateTimeOffset)> CompanySessionAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session, DateTimeOffset.UtcNow);
    }

    private static string Signed(string dosid, string quest, DateTimeOffset? issued = null) =>
        $"quest={quest}&key={Uri.EscapeDataString(LocalQuestAction.CreateFormKey(dosid, quest, issued))}";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedSessionStore(GameAuthSession session) : IGameAuthSessionStore
    {
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken) =>
            Task.FromResult<GameAuthSession?>(dosid == session.Dosid ? session : null);
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static DefaultHttpContext Request(string? dosid, string body, string query = "?action=internalQuestClaim")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
