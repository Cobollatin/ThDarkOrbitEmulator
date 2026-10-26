using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsActionHardeningTests
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
        // One purchase unlocks the "supplier" battle pass tier.
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalBattlePassClaim", true)]
    [TestCase("?action=internalExpeditionStart", true)]
    [TestCase("?ACTION=internalBattlePassClaim", false)]
    [TestCase("?Action=internalBattlePassClaim", false)]
    [TestCase("?action=internalBattlePassClaim&action=internalBattlePassClaim", false)]
    [TestCase("?action=internalBattlePassClaim&from=internalEvents", false)]
    [TestCase("?action=internalBattlePassClaim&ACTION=internalBattlePassClaim", false)]
    public void ActionQuery_RequiresOneCanonicalActionKey(string query, bool expected) =>
        Assert.That(LocalEventsAction.ValidActionQuery(new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query),
            Is.EqualTo(expected));

    [TestCase("ACTION", "internalBattlePassClaim", "tier=supplier")]
    [TestCase("Action", "internalBattlePassClaim", "tier=supplier")]
    [TestCase("ACTION", "internalExpeditionStart", "start=1")]
    [TestCase("aCTION", "internalExpeditionClaim", "claim=1")]
    public async Task NonCanonicalActionKey_WithValidSession_Returns400AndChangesNothing(string key, string action, string body)
    {
        var before = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        var context = Build($"?{key}={action}", body);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Events action."));
        await AssertUnchangedAsync(before);
    }

    [Test]
    public async Task NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalBattlePassClaim", "tier=supplier", "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [TestCase("internalBattlePassClaim", "tier=supplier")]
    [TestCase("internalExpeditionStart", "start=1")]
    [TestCase("internalExpeditionClaim", "claim=1")]
    public async Task BodyStreamIoFailure_Returns400AndChangesNothing(string action, string body)
    {
        var before = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        var context = Build($"?action={action}", body);
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync(before);
    }

    [TestCase("internalBattlePassClaim", "tier=supplier")]
    [TestCase("internalExpeditionStart", "start=1")]
    public async Task BodyStreamInvalidOperation_Returns400AndChangesNothing(string action, string body)
    {
        var before = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        var context = Build($"?action={action}", body);
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync(before);
    }

    [Test]
    public async Task MissingSession_Returns401BeforeReadingTheForm()
    {
        var context = Build("?action=internalBattlePassClaim", "tier=supplier", "missing-session");
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await Body(context), Does.Contain("Sign in again"));
    }

    [Test]
    public async Task ErrorPage_ShowsStatusAndNoStore()
    {
        var context = Build("?ACTION=internalBattlePassClaim", "tier=supplier");
        await Handle(context);
        var body = await Body(context);
        Assert.That(body, Does.Contain("Events request failed (400)"));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task CanonicalBattlePassClaim_StillApplies()
    {
        var before = (await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits;
        var context = Build("?action=internalBattlePassClaim", "tier=supplier");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalEvents&claimed=supplier"));
        var after = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(after.ClaimedBattlePassTiers, Does.Contain("supplier"));
        Assert.That(after.Credits, Is.EqualTo(before + 200));
    }

    private async Task AssertUnchangedAsync(LocalEquipmentState before)
    {
        var after = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(after.Credits, Is.EqualTo(before.Credits));
        Assert.That(after.ClaimedBattlePassTiers, Is.Empty);
        Assert.That(after.ExpeditionReadyAt, Is.EqualTo(before.ExpeditionReadyAt));
    }

    private Task Handle(HttpContext context) =>
        LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);

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

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }
}
