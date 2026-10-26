using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSkylabHardeningTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    [TestCase("&lang=e%00s")]
    [TestCase("&from=intern%0AalStart")]
    [TestCase("&prc=1%09")]
    public void PageQuery_RejectsControlCharacters(string extra)
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab" + extra));
        Assert.That(LocalSkylabAction.ValidPageQuery(query), Is.False);
    }

    [Test]
    public void PageQuery_AcceptsValueAtLengthLimit()
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab&lang=" + new string('x', 64)));
        Assert.That(LocalSkylabAction.ValidPageQuery(query), Is.True);
    }

    [TestCase("?action=internalSkylabUpgrade", true)]
    [TestCase("?action=internalSkylabCollect", true)]
    [TestCase("?ACTION=internalSkylabUpgrade", false)]
    [TestCase("?Action=internalSkylabCollect", false)]
    [TestCase("?action=internalskylabupgrade", false)]
    [TestCase("?action=INTERNALSKYLABCOLLECT", false)]
    [TestCase("?action=internalSkylab", false)]
    [TestCase("?action=", false)]
    [TestCase("", false)]
    [TestCase("?action=internalSkylabCollect&lang=es", false)]
    public void ActionQuery_RequiresOneCanonicalAction(string query, bool valid)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalSkylabAction.ValidActionQuery(parsed), Is.EqualTo(valid));
    }

    [TestCase("?ACTION=internalSkylabUpgrade")]
    [TestCase("?action=internalskylabupgrade")]
    [TestCase("")]
    public async Task NonCanonicalActionQuery_Returns400BeforeSession(string query)
    {
        var h = await HarnessAsync();
        var context = Request("missing-session", Upgrade(h, "prometium", 1));
        context.Request.QueryString = new QueryString(query);
        await Handle(h, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task TruncatedMultipartBody_Returns400WithoutSpending()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "--b\r\nContent-Disposition: form-data; name=\"collector\"\r\n\r\nprometium");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(h, context);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Contain("could not be read"));
            Assert.That(html, Does.Contain("Return to Skylab"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task BodyStreamFailure_Returns400WithoutSpending()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, Upgrade(h, "prometium", 1));
        context.Request.Body = new FailingStream();
        await Handle(h, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("could not be read"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task MissingSession_Returns401BeforeReadingTheForm()
    {
        var h = await HarnessAsync();
        var context = Request("missing-session", Upgrade(h, "prometium", 1));
        context.Request.Body = new FailingStream();
        await Handle(h, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(Body(context), Does.Contain("Sign in again"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task ErrorPage_ShowsStatusAndEncodesMessage()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await LocalSkylabAction.ErrorAsync(context.Response, 409, "<script>x</script>", CancellationToken.None);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Skylab request failed (409)"));
            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Contain("&lt;script&gt;"));
        });
    }

    [Test]
    public async Task CanonicalUpgrade_StillApplies()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, Upgrade(h, "prometium", 1));
        await Handle(h, context);
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(state.Collectors.Single(c => c.Id == "prometium").Level, Is.EqualTo(2));
        });
    }

    private static async Task AssertUnchangedAsync(Harness h)
    {
        var collector = (await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None))
            .Collectors.Single(c => c.Id == "prometium");
        Assert.Multiple(() =>
        {
            Assert.That(collector.Level, Is.EqualTo(1));
            Assert.That(collector.Stock, Is.EqualTo(500));
        });
    }

    private static string Upgrade(Harness h, string collector, int level) =>
        $"collector={collector}&level={level}&key={LocalSkylabAction.CreateFormKey(h.Session.Dosid, collector, level.ToString(), h.Clock.GetUtcNow())}";

    private static Task Handle(Harness h, HttpContext context) =>
        LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed record Harness(GameAuthSessionStore Sessions, LocalSkylabStore Store, GameAuthSession Session, TestClock Clock);

    private static async Task<Harness> HarnessAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clock = new TestClock();
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return new Harness(sessions, new LocalSkylabStore(cache, clock), session, clock);
    }

    private static DefaultHttpContext Request(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalSkylabUpgrade");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
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
