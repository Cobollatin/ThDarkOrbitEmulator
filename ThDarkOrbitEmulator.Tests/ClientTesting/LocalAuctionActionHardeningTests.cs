using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionActionHardeningTests
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

    [TestCase("internalAuctionBid")]
    [TestCase("internalAuctionClaim")]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing(string action)
    {
        var context = Build(action, "--b\r\nContent-Disposition: form-data; name=\"item\"\r\n\r\ndrone_flax");
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

    [TestCase("internalAuctionBid")]
    [TestCase("internalAuctionClaim")]
    public async Task BodyStreamFailure_Returns400_InsteadOfThrowing(string action)
    {
        var context = Build(action, "item=drone_flax&amount=200");
        context.Request.Body = new FailingStream();
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ValidMultipartBid_StillBids()
    {
        var context = Build("internalAuctionBid",
            "--b\r\nContent-Disposition: form-data; name=\"item\"\r\n\r\ndrone_flax\r\n" +
            "--b\r\nContent-Disposition: form-data; name=\"amount\"\r\n\r\n200\r\n" +
            $"--b\r\nContent-Disposition: form-data; name=\"key\"\r\n\r\n{LocalAuctionAction.CreateFormKey(_session.Dosid, "bid", "drone_flax")}\r\n--b--\r\n");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(state.Credits, Is.EqualTo(9800));
        });
    }

    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("PATCH")]
    public async Task NonPostMethods_Return405_BeforeSessionCheck(string method)
    {
        var context = Build("internalAuctionBid", "item=drone_flax&amount=200", dosid: "not-a-session");
        context.Request.Method = method;
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        });
        await AssertUnchangedAsync();
    }

    [TestCase("?action=INTERNALAUCTIONBID")]
    [TestCase("?action=internalauctionbid")]
    [TestCase("?action=internalAuctionClaimX")]
    [TestCase("")]
    public async Task UnknownOrMiscasedAction_IsRejected_BeforeSessionCheck(string query)
    {
        var context = Build("internalAuctionBid", "item=drone_flax&amount=200", dosid: "not-a-session");
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        await AssertUnchangedAsync();
    }

    [TestCase("item=<script>alert(1)</script>&amount=200")]
    [TestCase("item=drone_flax&amount=<b>200</b>")]
    public async Task RejectedValues_AreNotEchoedIntoErrorPage(string body)
    {
        var context = Build("internalAuctionBid", body);
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Not.Contain("<script>").And.Not.Contain("<b>"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ExpiredSession_GetsHtmlErrorPage_ThatLinksBack()
    {
        var context = Build("internalAuctionClaim", "item=drone_flax", dosid: "not-a-session");
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Sign in again").And.Contain("action=internalAuction\""));
        });
    }

    [TestCase("action=internalAuction", true)]
    [TestCase("action=internalAuction&from=internalStart&prc=1&lang=en", true)]
    [TestCase("action=internalAuction&LANG=en", false)]
    [TestCase("from=internalStart", false)]
    [TestCase("action=internalAuction&action=internalAuction", false)]
    [TestCase("action=internalAuction&lang=en&lang=de", false)]
    [TestCase("action=internalAuction&item=drone_flax", false)]
    [TestCase("action=internalAuction&amount=1", false)]
    [TestCase("action=internalAuction&lang=e%0An", false)]
    public void PageQuery_AllowsOnlyShellNavigationParameters(string query, bool expected) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsValueLength()
    {
        Assert.That(LocalAuctionAction.ValidPageQuery(Query("action=internalAuction&from=" + new string('a', 64))), Is.True);
        Assert.That(LocalAuctionAction.ValidPageQuery(Query("action=internalAuction&from=" + new string('a', 65))), Is.False);
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private DefaultHttpContext Build(string action, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(AuctionTestForms.WithKey(dosid ?? _session.Dosid, action, body));
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private Task Handle(HttpContext context) =>
        LocalAuctionAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);

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
