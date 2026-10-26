using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMessagingActionQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private const string Pilot = "1001";
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalMessagingStore _messaging = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _messaging = new LocalMessagingStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalFriendAdd", true)]
    [TestCase("?action=internalMessageSend", true)]
    [TestCase("?ACTION=internalFriendAdd", false)]
    [TestCase("?Action=internalMessageDelete", false)]
    [TestCase("?action=internalFriendAdd&action=internalFriendAdd", false)]
    [TestCase("?action=internalFriendAdd&from=internalMessages", false)]
    [TestCase("?action=internalFriendAdd&ACTION=internalFriendAdd", false)]
    [TestCase("", false)]
    public void ActionQuery_RequiresOneCanonicalActionKey(string query, bool expected) =>
        Assert.That(LocalMessagingAction.ValidActionQuery(
            new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query), Is.EqualTo(expected));

    [TestCase("ACTION", "internalFriendAdd")]
    [TestCase("Action", "internalFriendAdd")]
    [TestCase("ACTION", "internalFriendRemove")]
    [TestCase("aCTION", "internalFriendRemove")]
    public async Task NonCanonicalActionKey_WithValidForm_Returns400AndChangesNothing(string key, string action)
    {
        if (action == "internalFriendRemove") await AddFriendAsync();
        var before = await State();
        var context = Build($"?{key}={action}", FriendBody(action, before.Revision));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Messages action."));
        var after = await State();
        Assert.That(after.Friends, Is.EqualTo(before.Friends));
        Assert.That(after.Revision, Is.EqualTo(before.Revision));
    }

    [Test]
    public async Task NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalFriendAdd", FriendBody("internalFriendAdd", 0), "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task NonCanonicalActionKey_WithGet_StillReturns405()
    {
        var context = Build("?ACTION=internalMessageSend", "");
        context.Request.Method = "GET";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
    }

    [TestCase("internalFriendAdd")]
    [TestCase("internalFriendRemove")]
    public Task BodyStreamIoFailure_Returns400AndChangesNothing(string action) =>
        AssertUnreadableBody(action, new IOException("Client disconnected."));

    [TestCase("internalFriendAdd")]
    [TestCase("internalFriendRemove")]
    public Task BodyStreamInvalidOperation_Returns400AndChangesNothing(string action) =>
        AssertUnreadableBody(action, new InvalidOperationException("Body already consumed."));

    [Test]
    public async Task MissingSession_Returns401BeforeReadingTheForm()
    {
        var context = Build("?action=internalFriendAdd", FriendBody("internalFriendAdd", 0), "missing-session");
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await Body(context), Does.Contain("Sign in again"));
    }

    [Test]
    public async Task ErrorPage_ShowsStatusAndNoStore()
    {
        var context = Build("?ACTION=internalMessageDelete", "message=1&rev=0&key=x");
        await Handle(context);
        Assert.That(await Body(context), Does.Contain("Messages request failed (400)"));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task CanonicalAddThenRemove_StillApply()
    {
        await AddFriendAsync();
        Assert.That((await State()).Friends, Does.Contain(int.Parse(Pilot)));
        var remove = Build("?action=internalFriendRemove", FriendBody("internalFriendRemove", (await State()).Revision));
        await Handle(remove);
        Assert.That(remove.Response.StatusCode, Is.EqualTo(303));
        Assert.That(remove.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalMessages&removed=" + Pilot));
        Assert.That((await State()).Friends, Does.Not.Contain(int.Parse(Pilot)));
    }

    private async Task AssertUnreadableBody(string action, Exception failure)
    {
        if (action == "internalFriendRemove") await AddFriendAsync();
        var before = await State();
        var context = Build($"?action={action}", FriendBody(action, before.Revision));
        context.Request.Body = new FailingStream(failure);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        var after = await State();
        Assert.That(after.Friends, Is.EqualTo(before.Friends));
        Assert.That(after.Revision, Is.EqualTo(before.Revision));
    }

    private async Task AddFriendAsync()
    {
        var add = Build("?action=internalFriendAdd", FriendBody("internalFriendAdd", (await State()).Revision));
        await Handle(add);
        Assert.That(add.Response.StatusCode, Is.EqualTo(303));
    }

    private string FriendBody(string action, int revision) =>
        $"pilot={Pilot}&rev={revision}&key={Uri.EscapeDataString(LocalMessagingAction.CreateFormKey(_session.Dosid, action, Pilot, revision, Now))}";

    private Task<LocalMessagingState> State() => _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None);

    private Task Handle(HttpContext context) =>
        LocalMessagingAction.HandleAsync(context, _sessions, _messaging, new FixedClock(Now), CancellationToken.None);

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
