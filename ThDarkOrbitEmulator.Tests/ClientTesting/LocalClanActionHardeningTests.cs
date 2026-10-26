using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalClanActionHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalClanStore _clans = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _clans = new LocalClanStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalClanJoin", true)]
    [TestCase("?action=internalClanLeave", true)]
    [TestCase("?ACTION=internalClanJoin", false)]
    [TestCase("?Action=internalClanLeave", false)]
    [TestCase("?action=internalClanJoin&action=internalClanJoin", false)]
    [TestCase("?action=internalClanJoin&from=internalClan", false)]
    [TestCase("?action=internalClanJoin&ACTION=internalClanJoin", false)]
    [TestCase("", false)]
    public void ActionQuery_RequiresOneCanonicalActionKey(string query, bool expected) =>
        Assert.That(LocalClanAction.ValidActionQuery(
            new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query), Is.EqualTo(expected));

    [TestCase("ACTION", "join")]
    [TestCase("Action", "join")]
    [TestCase("ACTION", "leave")]
    [TestCase("aCTION", "leave")]
    public async Task NonCanonicalActionKey_WithValidForm_Returns400AndChangesNothing(string key, string kind)
    {
        if (kind == "leave") await JoinAsync();
        var context = Build($"?{key}={ActionName(kind)}", ValidBody(kind));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Clan action."));
        Assert.That(await Membership(), Is.EqualTo(kind == "leave" ? "mmo-guard" : null));
    }

    [Test]
    public async Task NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalClanJoin", ValidBody("join"), "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task NonCanonicalActionKey_WithGet_StillReturns405()
    {
        var context = Build("?ACTION=internalClanJoin", "");
        context.Request.Method = "GET";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
    }

    [TestCase("join")]
    [TestCase("leave")]
    public async Task BodyStreamIoFailure_Returns400AndChangesNothing(string kind)
    {
        if (kind == "leave") await JoinAsync();
        var context = Build($"?action={ActionName(kind)}", ValidBody(kind));
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        Assert.That(await Membership(), Is.EqualTo(kind == "leave" ? "mmo-guard" : null));
    }

    [TestCase("join")]
    [TestCase("leave")]
    public async Task BodyStreamInvalidOperation_Returns400AndChangesNothing(string kind)
    {
        if (kind == "leave") await JoinAsync();
        var context = Build($"?action={ActionName(kind)}", ValidBody(kind));
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        Assert.That(await Membership(), Is.EqualTo(kind == "leave" ? "mmo-guard" : null));
    }

    [Test]
    public async Task MissingSession_Returns401BeforeReadingTheForm()
    {
        var context = Build("?action=internalClanJoin", ValidBody("join"), "missing-session");
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await Body(context), Does.Contain("Sign in again"));
    }

    [Test]
    public async Task ErrorPage_ShowsStatusAndNoStore()
    {
        var context = Build("?ACTION=internalClanLeave", ValidBody("leave"));
        await Handle(context);
        Assert.That(await Body(context), Does.Contain("Clan request failed (400)"));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task CanonicalJoinThenLeave_StillApply()
    {
        await JoinAsync();
        Assert.That(await Membership(), Is.EqualTo("mmo-guard"));
        var leave = Build("?action=internalClanLeave", ValidBody("leave"));
        await Handle(leave);
        Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
        Assert.That(leave.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalClan&left=mmo-guard"));
        Assert.That(await Membership(), Is.Null);
    }

    private async Task JoinAsync()
    {
        var join = Build("?action=internalClanJoin", ValidBody("join"));
        await Handle(join);
        Assert.That(join.Response.StatusCode, Is.EqualTo(303));
    }

    private static string ActionName(string kind) =>
        kind == "join" ? "internalClanJoin" : "internalClanLeave";

    private string ValidBody(string kind) =>
        $"clan=mmo-guard&key={Uri.EscapeDataString(LocalClanAction.CreateFormKey(_session.Dosid, kind, "mmo-guard", Now))}";

    private Task<string?> Membership() => _clans.GetMembershipAsync(_session.AccountId, CancellationToken.None);

    private Task Handle(HttpContext context) =>
        LocalClanAction.HandleAsync(context, _sessions, _clans, new FixedClock(Now), CancellationToken.None);

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

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
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
