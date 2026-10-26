using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalGalaxyGateHardeningTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    [TestCase("&lang=e%00s")]
    [TestCase("&from=intern%0AalStart")]
    [TestCase("&prc=1%09")]
    [TestCase("&lang=es%7F")]
    public void PageQuery_RejectsControlCharacters(string extra)
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalGalaxyGates" + extra));
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(query), Is.False);
    }

    [Test]
    public void PageQuery_AcceptsValueAtLengthLimitAndRejectsLonger()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalGalaxyGates&lang=" + new string('x', 64)))), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalGalaxyGates&lang=" + new string('x', 65)))), Is.False);
        });
    }

    [TestCase("?action=internalGalaxyGates&Gate=alpha")]
    [TestCase("?action=internalGalaxyGates&gate=alpha&gate=beta")]
    [TestCase("?action=internalGalaxyGates&other=1")]
    [TestCase("?action=internalGalaxyGates&lang=es&lang=en")]
    [TestCase("?lang=es")]
    public void PageQuery_RejectsUnknownRepeatedOrMissingParameters(string query) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.False);

    [TestCase("?action=internalGalaxyGateSpin", true)]
    [TestCase("?action=internalGalaxyGateActivate", true)]
    [TestCase("?ACTION=internalGalaxyGateSpin", false)]
    [TestCase("?Action=internalGalaxyGateActivate", false)]
    [TestCase("?action=internalgalaxygatespin", false)]
    [TestCase("?action=INTERNALGALAXYGATEACTIVATE", false)]
    [TestCase("?action=internalGalaxyGates", false)]
    [TestCase("?action=", false)]
    [TestCase("", false)]
    [TestCase("?action=internalGalaxyGateSpin&lang=es", false)]
    public void ActionQuery_RequiresOneCanonicalAction(string query, bool valid)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalGalaxyGateAction.ValidActionQuery(parsed), Is.EqualTo(valid));
    }

    [TestCase("?ACTION=internalGalaxyGateSpin")]
    [TestCase("?Action=internalGalaxyGateSpin")]
    [TestCase("?action=internalgalaxygatespin")]
    [TestCase("")]
    public async Task NonCanonicalActionQuery_Returns400BeforeSession(string query)
    {
        var h = await HarnessAsync();
        var context = Request("missing-session", Spin(h, "alpha", 1));
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
    public async Task UpperCaseActionKey_WithValidSession_DoesNotSpend()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, Spin(h, "alpha", 1));
        context.Request.QueryString = new QueryString("?ACTION=internalGalaxyGateSpin");
        await Handle(h, context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task TruncatedMultipartBody_Returns400WithoutSpending()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "--b\r\nContent-Disposition: form-data; name=\"gate\"\r\n\r\nalpha");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(h, context);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Contain("could not be read"));
            Assert.That(html, Does.Contain("Return to Galaxy Gates"));
        });
        await AssertUnchangedAsync(h);
    }

    [Test]
    public async Task BodyStreamFailure_Returns400WithoutSpending()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, Spin(h, "alpha", 1));
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
        var context = Request("missing-session", Spin(h, "alpha", 1));
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
        await LocalGalaxyGateAction.ErrorAsync(context.Response, 409, "<script>x</script>", CancellationToken.None);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Galaxy Gates request failed (409)"));
            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Contain("&lt;script&gt;"));
        });
    }

    [Test]
    public async Task CanonicalSpin_StillApplies()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, Spin(h, "alpha", 2));
        await Handle(h, context);
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha"));
            Assert.That(state.Energy, Is.EqualTo(h.InitialEnergy - 2));
        });
    }

    private static async Task AssertUnchangedAsync(Harness h)
    {
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        var alpha = state.Gates.Single(g => g.Id == "alpha");
        Assert.Multiple(() =>
        {
            Assert.That(state.Energy, Is.EqualTo(h.InitialEnergy));
            Assert.That(alpha.PartsOwned, Is.EqualTo(h.InitialAlphaParts));
            Assert.That(alpha.Activated, Is.False);
        });
    }

    private static string Spin(Harness h, string gate, int amount) =>
        $"gate={gate}&amount={amount}&key={LocalGalaxyGateAction.CreateFormKey(h.Session.Dosid, gate, false, h.Clock.GetUtcNow())}";

    private static Task Handle(Harness h, HttpContext context) =>
        LocalGalaxyGateAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed record Harness(GameAuthSessionStore Sessions, LocalGalaxyGateStore Store, GameAuthSession Session,
        TestClock Clock, int InitialEnergy, int InitialAlphaParts);

    private static async Task<Harness> HarnessAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var store = new LocalGalaxyGateStore(cache);
        var initial = await store.GetAsync(session.AccountId, CancellationToken.None);
        return new Harness(sessions, store, session, new TestClock(), initial.Energy,
            initial.Gates.Single(g => g.Id == "alpha").PartsOwned);
    }

    private static DefaultHttpContext Request(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalGalaxyGateSpin");
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
