using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopPageHardeningTests
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

    [TestCase("action=internalShop", true)]
    [TestCase("action=internalShop&from=internalStart&prc=1&lang=en", true)]
    [TestCase("action=internalShop&category=lasers", true)]
    [TestCase("action=internalShop&item=laser_lf3", true)]
    [TestCase("action=internalShop&category=lasers&item=laser_lf3", true)]
    [TestCase("action=internalShop&Category=lasers&ITEM=laser_lf3", false)]
    [TestCase("ACTION=internalShop", false)]
    [TestCase("Action=internalShop", false)]
    [TestCase("action=internalshop", false)]
    [TestCase("action=INTERNALSHOP", false)]
    [TestCase("action=internalShop&From=internalStart", false)]
    [TestCase("action=internalShop&Q=laser", false)]
    [TestCase("action=internalShop&category=", false)]
    [TestCase("action=internalShop&item=", false)]
    [TestCase("action=internalShop&item=%20laser_lf3", false)]
    [TestCase("action=internalShop&item=laser_lf3%20", false)]
    [TestCase("action=internalShop&item=laser%0Alf3", false)]
    [TestCase("action=internalShop&category=lasers%00", false)]
    [TestCase("action=internalShop&item=laser_lf3&item=drone_flax", false)]
    [TestCase("action=internalShop&category=lasers&Category=drones", false)]
    [TestCase("action=internalShop&action=internalShop", false)]
    [TestCase("action=internalShop&from=a&from=b", false)]
    [TestCase("action=internalShop&lang=e%01n", false)]
    [TestCase("action=internalShop&quantity=1", false)]
    [TestCase("action=internalShop&currency=uridium", false)]
    [TestCase("action=internalShop&dosid=abc", false)]
    [TestCase("item=laser_lf3", false)]
    [TestCase("", false)]
    public void PageQuery_AllowsNavigationAndOneFilterEach(string query, bool expected) =>
        Assert.That(LocalShopPurchase.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsNavigationTo64AndFiltersTo100Characters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalShopPurchase.ValidPageQuery(Query("action=internalShop&from=" + new string('a', 64))), Is.True);
            Assert.That(LocalShopPurchase.ValidPageQuery(Query("action=internalShop&from=" + new string('a', 65))), Is.False);
            Assert.That(LocalShopPurchase.ValidPageQuery(Query("action=internalShop&item=" + new string('a', 100))), Is.True);
            Assert.That(LocalShopPurchase.ValidPageQuery(Query("action=internalShop&item=" + new string('a', 101))), Is.False);
            Assert.That(LocalShopPurchase.ValidPageQuery(Query("action=internalShop&category=" + new string('a', 101))), Is.False);
        });
    }

    [Test]
    public async Task ErrorPage_EncodesMessage_DisablesCaching_AndLinksBackToShop()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await LocalShopPurchase.ErrorAsync(context.Response, 401, "<b>expired</b> & gone", CancellationToken.None);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Not.Contain("<b>"));
            Assert.That(html, Does.Contain("&lt;b&gt;expired&lt;/b&gt; &amp; gone"));
            Assert.That(html, Does.Contain("Shop request failed (401)"));
            Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalShop\""));
        });
    }

    [Test]
    public async Task BuyTruncatedMultipartBody_Returns400_WithoutDebiting()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"item\"\r\n\r\nlaser_lf3");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("could not be read"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task BuyBodyStreamFailure_Returns400_WithoutDebiting()
    {
        var context = Build("item=laser_lf3&quantity=1&currency=credits&idempotencyKey=x");
        context.Request.Body = new FailingStream();
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Contain("could not be read"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task BuyWithoutSession_ReturnsHtml401_BeforeReadingTheForm()
    {
        var context = Build("item=laser_lf3", dosid: "missing-session");
        context.Request.Body = new FailingStream();
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(html, Does.Contain("Sign in again"));
            Assert.That(html, Does.Contain("Return to Shop"));
        });
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private DefaultHttpContext Build(string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalShopBuy");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    // The economy store has no database; every case here must finish before database access.
    private Task Handle(HttpContext context) =>
        LocalShopPurchase.HandleAsync(context, _sessions, _store, new EconomyStore(new ConfigurationBuilder().Build()),
            TimeProvider.System, CancellationToken.None);

    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private async Task AssertUnchangedAsync()
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
    }

    private sealed class FailingStream : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("Client disconnected.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("Client disconnected.");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Client disconnected.");
    }
}
