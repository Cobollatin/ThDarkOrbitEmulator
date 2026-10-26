using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopAuctionActionQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _equipment = null!;
    private EconomyStore _economy = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _equipment = new LocalHangarEquipmentStore(cache);
        // No connection string: every request below must be rejected before the database is touched.
        _economy = new EconomyStore(new ConfigurationBuilder().Build());
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("ACTION")]
    [TestCase("Action")]
    [TestCase("aCTION")]
    public async Task ShopBuy_NonCanonicalActionKey_Returns400(string key)
    {
        var context = Build($"?{key}=internalShopBuy", ShopBody());
        await HandleShop(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("Invalid Shop action."));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [Test]
    public async Task ShopBuy_RepeatedMixedCaseKey_Returns400()
    {
        var context = Build("?action=internalShopBuy&ACTION=internalShopBuy", ShopBody());
        await HandleShop(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ShopBuy_NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalShopBuy", ShopBody(), "missing-session");
        await HandleShop(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ShopBuy_NonCanonicalActionKey_WithGet_StillReturns405()
    {
        var context = Build("?ACTION=internalShopBuy", "");
        context.Request.Method = "GET";
        await HandleShop(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        });
    }

    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(IOException))]
    [TestCase(typeof(InvalidDataException))]
    public async Task ShopBuy_UnreadableForm_Returns400WithoutEcho(Type failure)
    {
        var context = Build("?action=internalShopBuy", "");
        context.Request.ContentLength = null;
        context.Request.Body = new FailingStream((Exception)Activator.CreateInstance(failure, "Body unavailable.")!);
        await HandleShop(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("The Shop purchase form could not be read."));
            Assert.That(Body(context), Does.Not.Contain("Body unavailable"));
        });
    }

    [Test]
    public async Task ShopBuy_MissingSession_Returns401BeforeReadingForm()
    {
        var context = Build("?action=internalShopBuy", "", "missing-session");
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await HandleShop(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public void ShopBuy_ValidActionQuery_AcceptsOnlyTheCanonicalSingleKey()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalShopPurchase.ValidActionQuery(Query("?action=internalShopBuy")), Is.True);
            Assert.That(LocalShopPurchase.ValidActionQuery(Query("?Action=internalShopBuy")), Is.False);
            Assert.That(LocalShopPurchase.ValidActionQuery(Query("")), Is.False);
            Assert.That(LocalShopPurchase.ValidActionQuery(Query("?action=a&action=b")), Is.False);
            Assert.That(LocalShopPurchase.ValidActionQuery(Query("?action=internalShopBuy&lang=en")), Is.False);
        });
    }

    [TestCase("internalAuctionBid")]
    [TestCase("internalAuctionClaim")]
    public async Task Auction_InvalidOperationOnFormRead_Returns400AndAppliesNothing(string action)
    {
        var before = System.Text.Json.JsonSerializer.Serialize(await _equipment.GetAsync(_session.AccountId, CancellationToken.None));
        var context = Build($"?action={action}", "");
        context.Request.ContentLength = null;
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await LocalAuctionAction.HandleAsync(context, _sessions, _equipment, new FixedClock(Now), CancellationToken.None);
        var after = System.Text.Json.JsonSerializer.Serialize(await _equipment.GetAsync(_session.AccountId, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("The Auction form could not be read."));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(after, Is.EqualTo(before));
        });
    }

    [TestCase("ACTION")]
    [TestCase("Action")]
    public async Task Auction_NonCanonicalActionKey_Returns400BeforeSessionLookup(string key)
    {
        var context = Build($"?{key}=internalAuctionBid", "item=lot-1&amount=100", "missing-session");
        await LocalAuctionAction.HandleAsync(context, _sessions, _equipment, new FixedClock(Now), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    private Task HandleShop(DefaultHttpContext context) =>
        LocalShopPurchase.HandleAsync(context, _sessions, _equipment, _economy, new FixedClock(Now), CancellationToken.None);

    private string ShopBody() =>
        $"item=laser_lf3&quantity=1&currency=credits&idempotencyKey={LocalShopPurchase.CreateFormKey(_session.Dosid, "laser_lf3", Now)}";

    private static IQueryCollection Query(string query) => new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query;

    private DefaultHttpContext Build(string query, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }
}
