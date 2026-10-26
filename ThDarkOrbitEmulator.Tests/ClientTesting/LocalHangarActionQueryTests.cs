using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarActionQueryTests
{
    private const string EquipmentBody = "slot=1&configuration=1&item=drone_flax&quantity=1";
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _equipment = null!;
    private LocalHangarStore _hangars = null!;
    private GameAuthSession _session = null!;
    private readonly TimeProvider _clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _equipment = new LocalHangarEquipmentStore(cache);
        _hangars = new LocalHangarStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("ACTION")]
    [TestCase("Action")]
    [TestCase("aCTION")]
    public async Task EquipmentSet_NonCanonicalActionKey_Returns400AndChangesNothing(string key)
    {
        var context = Build($"?{key}=internalDockEquipmentSet", EquipmentBody);
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid equipment action."));
        Assert.That(await Assignments(), Is.Empty);
    }

    [Test]
    public async Task EquipmentSet_RepeatedMixedCaseKey_Returns400()
    {
        var context = Build("?action=internalDockEquipmentSet&ACTION=internalDockEquipmentSet", EquipmentBody);
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Assignments(), Is.Empty);
    }

    [Test]
    public async Task EquipmentSet_NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalDockEquipmentSet", EquipmentBody, "missing-session");
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task EquipmentSet_NonCanonicalActionKey_WithGet_StillReturns405()
    {
        var context = Build("?ACTION=internalDockEquipmentSet", "");
        context.Request.Method = "GET";
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
    }

    [Test]
    public Task EquipmentSet_BodyIoFailure_Returns400AndChangesNothing() =>
        AssertUnreadableEquipmentBody(new IOException("Client disconnected."));

    [Test]
    public Task EquipmentSet_BodyInvalidOperation_Returns400AndChangesNothing() =>
        AssertUnreadableEquipmentBody(new InvalidOperationException("Body already consumed."));

    [Test]
    public async Task EquipmentSet_MissingSession_Returns401BeforeReadingTheForm()
    {
        var context = Build("?action=internalDockEquipmentSet", EquipmentBody, "missing-session");
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task EquipmentSet_CanonicalQuery_StillApplies()
    {
        var context = Build("?action=internalDockEquipmentSet", EquipmentBody);
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That(context.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalDockEquipment&slot=1&configuration=1&saved=drone_flax"));
        Assert.That(await Assignments(), Is.Not.Empty);
    }

    [TestCase("ACTION")]
    [TestCase("Action")]
    public async Task DockSelect_NonCanonicalActionKey_Returns400AndKeepsActiveSlot(string key)
    {
        var context = Build($"?{key}=internalDockSelect", SelectBody());
        await HandleSelect(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid hangar action."));
        Assert.That(await _hangars.GetActiveSlotAsync(_session.AccountId, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task DockSelect_NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalDockSelect", SelectBody(), "missing-session");
        await HandleSelect(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(InvalidOperationException))]
    public async Task DockSelect_UnreadableBody_Returns400AndKeepsActiveSlot(Type failure)
    {
        var context = Build("?action=internalDockSelect", SelectBody());
        context.Request.Body = new FailingStream((Exception)Activator.CreateInstance(failure, "Body unavailable.")!);
        await HandleSelect(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        Assert.That(await _hangars.GetActiveSlotAsync(_session.AccountId, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task DockSelect_CanonicalQuery_StillApplies()
    {
        var context = Build("?action=internalDockSelect", SelectBody());
        await HandleSelect(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await _hangars.GetActiveSlotAsync(_session.AccountId, CancellationToken.None), Is.EqualTo(2));
    }

    private async Task AssertUnreadableEquipmentBody(Exception failure)
    {
        var context = Build("?action=internalDockEquipmentSet", EquipmentBody);
        context.Request.Body = new FailingStream(failure);
        await HandleEquipment(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await Assignments(), Is.Empty);
    }

    private string SelectBody() =>
        $"slot=2&current=1&key={Uri.EscapeDataString(LocalHangars.CreateFormKey(_session.Dosid, 2, 1, _clock.GetUtcNow()))}";

    private async Task<object> Assignments() =>
        (await _equipment.GetAsync(_session.AccountId, CancellationToken.None)).Assignments;

    private Task HandleEquipment(HttpContext context) =>
        LocalHangarEquipmentSelection.HandleAsync(context, _sessions, _equipment, CancellationToken.None);

    private Task HandleSelect(HttpContext context) =>
        LocalHangarSelection.HandleAsync(context, _sessions, _hangars, CancellationToken.None, _clock);

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
