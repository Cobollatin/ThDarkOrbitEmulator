using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionValidationTests
{
    [Test]
    public async Task NonPost_Returns405WithAllowHeader()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, "?action=internalAuctionBid", "item=drone_flax&amount=200", "GET");
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [TestCase("?action=internalAuctionBid&amount=1")]
    [TestCase("?action=internalAuctionBid&action=internalAuctionBid")]
    [TestCase("?action=internalAuctionBid&item=laser_lf3")]
    public async Task ExtraOrRepeatedQuery_Returns400(string query)
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, query, "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await CreditsAsync(store, sessions, dosid), Is.EqualTo(10000));
    }

    [Test]
    public async Task ExpiredSession_Returns401ErrorPage()
    {
        var (sessions, store, _) = await SetupAsync();
        var context = Request("not-a-session", "?action=internalAuctionBid", "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(Body(context), Does.Contain("Auction request failed (401)"));
    }

    [Test]
    public async Task NoCompany_Returns403WithoutDebit()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var context = Request(session.Dosid, "?action=internalAuctionClaim", "item=drone_flax");
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(403));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).AuctionLots, Is.Empty);
    }

    [TestCase("item=drone_flax")]
    [TestCase("amount=200")]
    [TestCase("item=drone_flax&amount=200&quantity=5")]
    [TestCase("item=drone_flax&item=laser_lf3&amount=700")]
    [TestCase("item=drone_flax&amount=200&amount=300")]
    [TestCase("item=&amount=200")]
    [TestCase("item=DRONE_FLAX&amount=200")]
    [TestCase("item=%3Cscript%3E&amount=200")]
    [TestCase("item=drone_flax&amount=-200")]
    [TestCase("item=drone_flax&amount=0")]
    [TestCase("item=drone_flax&amount=0200")]
    [TestCase("item=drone_flax&amount=2e3")]
    [TestCase("item=drone_flax&amount=200.5")]
    [TestCase("item=drone_flax&amount=%20200")]
    [TestCase("item=drone_flax&amount=1000000000")]
    [TestCase("item=drone_flax&amount=99999999999999999999999")]
    [TestCase("item=drone_flax&amount=%EF%BC%92%EF%BC%90%EF%BC%90")]
    public async Task MalformedBidForm_Returns400WithoutDebit(string body)
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, "?action=internalAuctionBid", body);
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(context), Does.Not.Contain("<script>"));
        Assert.That(await CreditsAsync(store, sessions, dosid), Is.EqualTo(10000));
    }

    [TestCase("item=drone_flax&amount=200")]
    [TestCase("")]
    public async Task MalformedClaimForm_Returns400(string body)
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, "?action=internalAuctionClaim", body);
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task NulInForm_Returns400()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, "?action=internalAuctionBid", "item=drone%00flax&amount=200");
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var context = Request(dosid, "?action=internalAuctionBid", "{\"item\":\"drone_flax\"}");
        context.Request.ContentType = "application/json";
        await LocalAuctionAction.HandleAsync(context, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task UnknownLot_Returns404()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var bid = Request(dosid, "?action=internalAuctionBid", "item=laser_lf4&amount=700");
        await LocalAuctionAction.HandleAsync(bid, sessions, store, TimeProvider.System, CancellationToken.None);
        var claim = Request(dosid, "?action=internalAuctionClaim", "item=laser_lf4");
        await LocalAuctionAction.HandleAsync(claim, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(bid.Response.StatusCode, Is.EqualTo(404));
        Assert.That(claim.Response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task BidConflicts_Return409AndKeepCredits()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var clock = new TestClock();
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=199"), Is.EqualTo(409));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=10001"), Is.EqualTo(409));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=200"), Is.EqualTo(303));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=219"), Is.EqualTo(409));
        Assert.That(await CreditsAsync(store, sessions, dosid), Is.EqualTo(9800));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=500"), Is.EqualTo(409));
        Assert.That(await CreditsAsync(store, sessions, dosid), Is.EqualTo(9800));
    }

    [Test]
    public async Task ClaimConflicts_Return409AndGrantOnce()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var clock = new TestClock();
        var before = await QuantityAsync(store, sessions, dosid, "drone_flax");
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionClaim", "item=drone_flax"), Is.EqualTo(409));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=200"), Is.EqualTo(303));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionClaim", "item=drone_flax"), Is.EqualTo(409));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionClaim", "item=drone_flax"), Is.EqualTo(303));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionClaim", "item=drone_flax"), Is.EqualTo(409));
        Assert.That(await PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=drone_flax&amount=300"), Is.EqualTo(409));
        Assert.That(await QuantityAsync(store, sessions, dosid, "drone_flax"), Is.EqualTo(before + 1));
    }

    [Test]
    public async Task ConcurrentBidsAndClaims_ApplyOnce()
    {
        var (sessions, store, dosid) = await SetupAsync();
        var clock = new TestClock();
        var bids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            PostAsync(sessions, store, clock, dosid, "internalAuctionBid", "item=laser_lf3&amount=700")));
        Assert.That(bids.Count(status => status == 303), Is.EqualTo(1));
        Assert.That(bids.Count(status => status == 409), Is.EqualTo(15));
        Assert.That(await CreditsAsync(store, sessions, dosid), Is.EqualTo(9300));
        var before = await QuantityAsync(store, sessions, dosid, "laser_lf3");
        clock.Advance(TimeSpan.FromMinutes(5));
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            PostAsync(sessions, store, clock, dosid, "internalAuctionClaim", "item=laser_lf3")));
        Assert.That(claims.Count(status => status == 303), Is.EqualTo(1));
        Assert.That(await QuantityAsync(store, sessions, dosid, "laser_lf3"), Is.EqualTo(before + 1));
    }

    [Test]
    public async Task StoreResults_DistinguishFailures()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await store.BidResultAsync(Guid.Empty, "drone_flax", 200, now, CancellationToken.None), Is.EqualTo(LocalAuctionResult.Rejected));
            Assert.That(await store.BidResultAsync(account, "drone_flax", -5, now, CancellationToken.None), Is.EqualTo(LocalAuctionResult.Rejected));
            Assert.That(await store.BidResultAsync(account, "missing", 200, now, CancellationToken.None), Is.EqualTo(LocalAuctionResult.UnknownLot));
            Assert.That(await store.BidResultAsync(account, "drone_flax", (long)int.MaxValue + 1, now, CancellationToken.None), Is.EqualTo(LocalAuctionResult.InsufficientCredits));
            Assert.That(await store.ClaimAuctionResultAsync(account, "drone_flax", now, CancellationToken.None), Is.EqualTo(LocalAuctionResult.NoBid));
        });
        // Rejected bids must not open a lot or start its timer.
        Assert.That((await store.GetAsync(account, CancellationToken.None)).AuctionLots, Is.Empty);
    }

    [TestCase("laser_lf3", true)]
    [TestCase("lot-2", true)]
    [TestCase("", false)]
    [TestCase("Laser", false)]
    [TestCase("a b", false)]
    public void LotIdShape(string value, bool expected) =>
        Assert.That(LocalAuction.IsLotIdShape(value), Is.EqualTo(expected));

    [Test]
    public void LotIdShape_RejectsOverlongValue() =>
        Assert.That(LocalAuction.IsLotIdShape(new string('a', 65)), Is.False);

    private static async Task<int> PostAsync(GameAuthSessionStore sessions, LocalHangarEquipmentStore store,
        TimeProvider clock, string dosid, string action, string body)
    {
        var context = Request(dosid, $"?action={action}", AuctionTestForms.WithKey(dosid, action, body, clock.GetUtcNow()));
        await LocalAuctionAction.HandleAsync(context, sessions, store, clock, CancellationToken.None);
        return context.Response.StatusCode;
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Store, string Dosid)> SetupAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session.Dosid);
    }

    private static async Task<int> CreditsAsync(LocalHangarEquipmentStore store, GameAuthSessionStore sessions, string dosid) =>
        (await store.GetAsync((await sessions.TryGetByDosidAsync(dosid, CancellationToken.None))!.AccountId, CancellationToken.None)).Credits;

    private static async Task<int> QuantityAsync(LocalHangarEquipmentStore store, GameAuthSessionStore sessions, string dosid, string item) =>
        (await store.GetAsync((await sessions.TryGetByDosidAsync(dosid, CancellationToken.None))!.AccountId, CancellationToken.None))
            .Inventory.Single(entry => entry.Id == item).Quantity;

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static DefaultHttpContext Request(string? dosid, string query, string body, string method = "POST")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(AuctionTestForms.WithKey(dosid, query, body));
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
