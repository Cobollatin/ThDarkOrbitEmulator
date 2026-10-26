using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalTechFactoryHardeningTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    [TestCase("&lang=e%00s")]
    [TestCase("&from=intern%0AalStart")]
    [TestCase("&prc=1%09")]
    [TestCase("&lang=es%7F")]
    public void PageQuery_RejectsControlCharacters(string extra)
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalTechFactory" + extra));
        Assert.That(LocalTechFactoryAction.ValidPageQuery(query), Is.False);
    }

    [Test]
    public void PageQuery_AcceptsShellParametersAndValueAtLengthLimit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalTechFactoryAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalTechFactory&from=internalStart&prc=100&lang=es"))), Is.True);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalTechFactory&lang=" + new string('x', 64)))), Is.True);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalTechFactory&lang=" + new string('x', 65)))), Is.False);
        });
    }

    [TestCase("?action=internalTechFactoryStart", true)]
    [TestCase("?action=internalTechFactoryClaim", true)]
    [TestCase("?ACTION=internalTechFactoryStart", false)]
    [TestCase("?Action=internalTechFactoryClaim", false)]
    [TestCase("?action=internaltechfactorystart", false)]
    [TestCase("?action=INTERNALTECHFACTORYCLAIM", false)]
    [TestCase("?action=internalTechFactory", false)]
    [TestCase("?action=", false)]
    [TestCase("", false)]
    [TestCase("?action=internalTechFactoryStart&lang=es", false)]
    public void ActionQuery_RequiresOneCanonicalAction(string query, bool valid)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalTechFactoryAction.ValidActionQuery(parsed), Is.EqualTo(valid));
    }

    [TestCase("?ACTION=internalTechFactoryStart")]
    [TestCase("?Action=internalTechFactoryStart")]
    [TestCase("?action=internaltechfactorystart")]
    [TestCase("")]
    public async Task NonCanonicalActionQuery_Returns400BeforeSession(string query)
    {
        var h = await HarnessAsync();
        var context = Request("missing-session", StartForm(h, "targeting"), query);
        await Handle(h, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task UpperCaseActionKey_WithValidSession_DoesNotStartProduction()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, StartForm(h, "targeting"), "?ACTION=internalTechFactoryStart");
        await Handle(h, context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task TruncatedMultipartBody_Returns400WithoutStarting()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "--b\r\nContent-Disposition: form-data; name=\"recipe\"\r\n\r\ntargeting");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(h, context);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Contain("could not be read"));
            Assert.That(html, Does.Contain("Return to Tech Factory"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task BodyStreamFailure_Returns400WithoutStarting()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, StartForm(h, "targeting"));
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
        var context = Request("missing-session", StartForm(h, "targeting"));
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
        await LocalTechFactoryAction.ErrorAsync(context.Response, 409, "<script>x</script>", CancellationToken.None);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Tech Factory request failed (409)"));
            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Contain("&lt;script&gt;"));
        });
    }

    [Test]
    public async Task CanonicalStart_StillApplies()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, StartForm(h, "targeting"));
        await Handle(h, context);
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalTechFactory&started=targeting"));
            Assert.That(state.Job?.RecipeId, Is.EqualTo("targeting"));
            Assert.That(state.Components, Is.LessThan(h.InitialComponents));
        });
    }

    private static async Task AssertUnchangedAsync(Harness h)
    {
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Job, Is.Null);
            Assert.That(state.Components, Is.EqualTo(h.InitialComponents));
        });
    }

    private static string StartForm(Harness h, string recipe) =>
        $"recipe={recipe}&key={LocalTechFactoryAction.CreateFormKey(h.Session.Dosid, "start", recipe, h.Clock.GetUtcNow())}";

    private static Task Handle(Harness h, HttpContext context) =>
        LocalTechFactoryAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed record Harness(GameAuthSessionStore Sessions, LocalTechFactoryStore Store, GameAuthSession Session,
        TestClock Clock, int InitialComponents);

    private static async Task<Harness> HarnessAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var clock = new TestClock();
        var store = new LocalTechFactoryStore(cache, clock);
        var initial = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(initial.Job, Is.Null);
        return new Harness(sessions, store, session, clock, initial.Components);
    }

    private static DefaultHttpContext Request(string dosid, string body, string query = "?action=internalTechFactoryStart")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
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
        public override DateTimeOffset GetUtcNow() => Start;
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
