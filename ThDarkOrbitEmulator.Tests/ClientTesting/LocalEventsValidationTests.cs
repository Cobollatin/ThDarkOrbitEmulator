using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsValidationTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("tier=")]
    [TestCase("")]
    [TestCase("tier=supplier&tier=supplier")]
    [TestCase("tier=supplier&credits=99999")]
    [TestCase("tier=..%2Fsupplier")]
    [TestCase("tier=%E2%80%AEsupplier")]
    [TestCase("tier=supplier%00")]
    [TestCase("tier=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task BattlePassClaim_RejectsTamperedForms(string body)
    {
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var before = (await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits;
        var context = await SendAsync("internalBattlePassClaim", body);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(before));
        Assert.That(state.ClaimedBattlePassTiers, Is.Empty);
    }

    [TestCase("internalExpeditionStart", "start=2")]
    [TestCase("internalExpeditionStart", "start=-100")]
    [TestCase("internalExpeditionStart", "start=1&start=1")]
    [TestCase("internalExpeditionStart", "claim=1")]
    [TestCase("internalExpeditionStart", "")]
    [TestCase("internalExpeditionClaim", "claim=999999999999")]
    [TestCase("internalExpeditionClaim", "claim=1&item=ship_goliath")]
    public async Task Expedition_RejectsTamperedForms(string action, string body)
    {
        var context = await SendAsync(action, body);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
        Assert.That(state.ExpeditionReadyAt, Is.Null);
    }

    [Test]
    public async Task UnknownTier_Returns404_AndUnreachedTier_Returns409()
    {
        foreach (var tier in new[] { "nonexistent", "SUPPLIER", "-1" })
            Assert.That((await SendAsync("internalBattlePassClaim", $"tier={tier}")).Response.StatusCode, Is.EqualTo(404));
        Assert.That((await SendAsync("internalBattlePassClaim", "tier=supplier")).Response.StatusCode, Is.EqualTo(409));
        Assert.That((await SendAsync("internalBattlePassClaim", "tier=tycoon")).Response.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task ReplayedClaim_Returns409_AndCreditsOnlyOnce()
    {
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var before = (await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits;
        Assert.That((await SendAsync("internalBattlePassClaim", "tier=supplier")).Response.StatusCode, Is.EqualTo(303));
        var replay = await SendAsync("internalBattlePassClaim", "tier=supplier");
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        Assert.That(await Body(replay), Does.Contain("already claimed"));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(before + 200));
    }

    [Test]
    public async Task Expedition_DoubleStart_EarlyClaim_AndReplay_Return409()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);
        Assert.That((await SendAsync("internalExpeditionClaim", "claim=1", clock)).Response.StatusCode, Is.EqualTo(409));
        Assert.That((await SendAsync("internalExpeditionStart", "start=1", clock)).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await SendAsync("internalExpeditionStart", "start=1", clock)).Response.StatusCode, Is.EqualTo(409));
        Assert.That((await SendAsync("internalExpeditionClaim", "claim=1", clock)).Response.StatusCode, Is.EqualTo(409));
        clock.Now = now.AddMinutes(LocalEvents.ExpeditionMinutes);
        Assert.That((await SendAsync("internalExpeditionClaim", "claim=1", clock)).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await SendAsync("internalExpeditionClaim", "claim=1", clock)).Response.StatusCode, Is.EqualTo(409));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000 - LocalEvents.ExpeditionCost));
        Assert.That(state.Inventory.Single(x => x.Id == LocalEvents.ExpeditionRewardItemId).Quantity, Is.EqualTo(5));
    }

    [Test]
    public async Task ExpeditionStart_WithoutEnoughCredits_Returns409_AndNeverGoesNegative()
    {
        var account = _session.AccountId;
        var expensive = LocalShop.Catalog.OrderByDescending(item => item.Price).First();
        while (await _store.PurchaseAsync(account, expensive.Id, 1, CancellationToken.None)) { }
        foreach (var item in LocalShop.Catalog.OrderBy(item => item.Price))
            while ((await _store.GetAsync(account, CancellationToken.None)).Credits >= LocalEvents.ExpeditionCost
                && await _store.PurchaseAsync(account, item.Id, 1, CancellationToken.None)) { }
        var before = (await _store.GetAsync(account, CancellationToken.None)).Credits;
        Assume.That(before, Is.LessThan(LocalEvents.ExpeditionCost));
        var context = await SendAsync("internalExpeditionStart", "start=1");
        Assert.That(context.Response.StatusCode, Is.EqualTo(409));
        var state = await _store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(before).And.GreaterThanOrEqualTo(0));
        Assert.That(state.ExpeditionReadyAt, Is.Null);
    }

    [Test]
    public async Task ConcurrentClaimsAndStarts_ApplyExactlyOnce()
    {
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var before = (await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits;
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => SendAsync("internalBattlePassClaim", "tier=supplier")));
        var starts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => SendAsync("internalExpeditionStart", "start=1")));
        Assert.That(claims.Count(c => c.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That(claims.Count(c => c.Response.StatusCode == 409), Is.EqualTo(15));
        Assert.That(starts.Count(c => c.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits,
            Is.EqualTo(before + 200 - LocalEvents.ExpeditionCost));
    }

    [Test]
    public async Task ExpiredSession_Returns401_AndForeignAccountUnaffected()
    {
        var foreign = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other", null, 8), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(foreign.Dosid, "eic", CancellationToken.None), Is.True);
        var expired = await SendAsync("internalExpeditionStart", "start=1", dosid: "deadbeef");
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await Body(expired), Does.Contain("missing or expired"));
        Assert.That((await SendAsync("internalExpeditionStart", "start=1", dosid: foreign.Dosid)).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).ExpeditionReadyAt, Is.Null);
    }

    [Test]
    public async Task WrongMethod_ExtraQuery_AndNonForm_AreRejected()
    {
        var get = Build("internalExpeditionStart", "start=1", _session.Dosid);
        get.Request.Method = "GET";
        await LocalEventsAction.HandleAsync(get, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(get.Response.StatusCode, Is.EqualTo(405));
        var extra = Build("internalExpeditionStart", "start=1", _session.Dosid);
        extra.Request.QueryString = new QueryString("?action=internalExpeditionStart&account=1");
        await LocalEventsAction.HandleAsync(extra, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(extra.Response.StatusCode, Is.EqualTo(400));
        var json = Build("internalExpeditionStart", "{\"start\":1}", _session.Dosid);
        json.Request.ContentType = "application/json";
        await LocalEventsAction.HandleAsync(json, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public void Page_ListsEmulatorTiers_AndExpeditionFormsCarryExpectedFields()
    {
        var state = new LocalEquipmentState([], []) { Credits = 10000 };
        var page = LocalEvents.Page(state, DateTimeOffset.UtcNow);
        Assert.That(LocalEvents.Tiers.Select(t => t.Id), Is.Unique);
        Assert.That(LocalEvents.Tiers.All(t => LocalEvents.IsTierIdShape(t.Id) && t.Target > 0 && t.RewardCredits > 0));
        foreach (var tier in LocalEvents.Tiers) Assert.That(page, Does.Contain(tier.Name));
        Assert.That(page, Does.Contain("name=\"start\" value=\"1\""));
    }

    private async Task<DefaultHttpContext> SendAsync(string action, string body, TimeProvider? clock = null, string? dosid = null)
    {
        var context = Build(action, body, dosid ?? _session.Dosid);
        await LocalEventsAction.HandleAsync(context, _sessions, _store, clock ?? TimeProvider.System, CancellationToken.None);
        return context;
    }

    private static DefaultHttpContext Build(string action, string body, string dosid)
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
        return context;
    }

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
