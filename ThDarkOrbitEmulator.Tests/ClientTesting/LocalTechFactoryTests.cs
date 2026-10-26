using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalTechFactoryTests
{
    [Test]
    public async Task Production_DebitsOnceAndOnlyClaimsWhenReady()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var store = new LocalTechFactoryStore(services.GetRequiredService<IDistributedCache>(), clock);
        var account = Guid.NewGuid();
        Assert.That(await store.StartAsync(account, "unknown", CancellationToken.None), Is.False);
        Assert.That(await store.StartAsync(account, "targeting", CancellationToken.None), Is.True);
        Assert.That(await store.StartAsync(account, "shield", CancellationToken.None), Is.False);
        Assert.That(await store.ClaimAsync(account, CancellationToken.None), Is.False);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await store.ClaimAsync(account, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimAsync(account, CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Components, Is.EqualTo(900));
            Assert.That(state.Produced["targeting"], Is.EqualTo(1));
            Assert.That(state.Job, Is.Null);
        });
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Components, Is.EqualTo(1000));
    }

    [Test]
    public async Task Posts_RequireSessionCompanyAndValidRecipe()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalTechFactoryStore(cache, new TestClock());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalTechFactoryStart", "recipe=targeting");
        await LocalTechFactoryAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalTechFactoryStart", "recipe=targeting");
        await LocalTechFactoryAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var bad = Request(session.Dosid, "internalTechFactoryStart", Form(session.Dosid, "unknown"));
        await LocalTechFactoryAction.HandleAsync(bad, sessions, store, CancellationToken.None);
        Assert.That(bad.Response.StatusCode, Is.EqualTo(404));
        var valid = Request(session.Dosid, "internalTechFactoryStart", Form(session.Dosid, "targeting"));
        await LocalTechFactoryAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalTechFactory&started=targeting"));
    }

    [Test]
    public async Task Posts_RejectTamperingForeignAccountAndReplay()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalTechFactoryStore(cache, new TestClock());
        var first = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "first", null, 7), "mmo", CancellationToken.None)).Session;
        var second = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "second", null, 8), "mmo", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(first.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await sessions.SelectCompanyAsync(second.Dosid, "mmo", CancellationToken.None), Is.True);
        var body = Form(first.Dosid, "targeting");
        foreach (var (dosid, form, expected) in new (string?, string, int)[]
        {
            (null, body, 401), (second.Dosid, body, 400),
            (first.Dosid, "recipe=targeting", 400),
            (first.Dosid, body + "&recipe=targeting", 400),
            (first.Dosid, body + "&quantity=-1", 400),
            (first.Dosid, body + "&quantity=999999999999", 400),
            (first.Dosid, body.Replace("targeting", "shield"), 400)
        })
        {
            var request = Request(dosid, "internalTechFactoryStart", form);
            await LocalTechFactoryAction.HandleAsync(request, sessions, store, CancellationToken.None);
            Assert.That(request.Response.StatusCode, Is.EqualTo(expected), form);
            Assert.That(request.Response.ContentType, Does.StartWith("text/html"));
        }
        var start = Request(first.Dosid, "internalTechFactoryStart", body);
        await LocalTechFactoryAction.HandleAsync(start, sessions, store, CancellationToken.None);
        Assert.That(start.Response.StatusCode, Is.EqualTo(303));
        var replay = Request(first.Dosid, "internalTechFactoryStart", body);
        await LocalTechFactoryAction.HandleAsync(replay, sessions, store, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await store.GetAsync(first.AccountId, CancellationToken.None)).Components, Is.EqualTo(900));
    }

    [Test]
    public async Task ConcurrentStartsAndClaims_OnlyOneSucceeds()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var store = new LocalTechFactoryStore(services.GetRequiredService<IDistributedCache>(), clock);
        var account = Guid.NewGuid();
        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.StartAsync(account, "targeting", CancellationToken.None)));
        Assert.That(starts.Count(value => value), Is.EqualTo(1));
        clock.Advance(TimeSpan.FromMinutes(2));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(account, CancellationToken.None)));
        Assert.That(claims.Count(value => value), Is.EqualTo(1));
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Components, Is.EqualTo(900));
        Assert.That(state.Produced["targeting"], Is.EqualTo(1));
    }

    [Test]
    public async Task FullOutput_KeepsReadyJobAndExplainsBlockedClaim()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new TestClock();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalTechFactoryStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 11), "mmo", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var job = new LocalTechJob("targeting", clock.GetUtcNow(), Guid.NewGuid());
        var state = new LocalTechFactoryState(900,
            new Dictionary<string, int> { ["targeting"] = int.MaxValue }, job);
        await cache.SetStringAsync($"localtechfactory:{session.AccountId:N}",
            JsonSerializer.Serialize(state), CancellationToken.None);

        var loaded = await store.GetAsync(session.AccountId, CancellationToken.None);
        var page = LocalTechFactory.Page(session, loaded, clock.GetUtcNow());
        var form = $"job={job.Id:N}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", job.Id.ToString("N"), clock.GetUtcNow())}";
        var request = Request(session.Dosid, "internalTechFactoryClaim", form);
        request.Response.Body = new MemoryStream();
        await LocalTechFactoryAction.HandleAsync(request, sessions, store, CancellationToken.None, clock);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.ClaimOutputFull, Is.True);
            Assert.That(page, Does.Contain("id=\"techFactoryClaimBlocked\""));
            Assert.That(page, Does.Not.Contain("action=internalTechFactoryClaim"));
            Assert.That(request.Response.StatusCode, Is.EqualTo(409));
        });
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Job, Is.Not.Null);
        Assert.That(await store.ClaimAsync(session.AccountId, CancellationToken.None), Is.False);
    }

    [Test]
    public async Task ClaimForm_RejectsForeignJobEarlyClaimAndDoubleSubmit()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new TestClock();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalTechFactoryStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "pilot", null, 9), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var start = Request(session.Dosid, "internalTechFactoryStart", Form(session.Dosid, "targeting"));
        await LocalTechFactoryAction.HandleAsync(start, sessions, store, CancellationToken.None);
        Assert.That(start.Response.StatusCode, Is.EqualTo(303));
        var job = (await store.GetAsync(session.AccountId, CancellationToken.None)).Job!;
        var body = $"job={job.Id:N}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", job.Id.ToString("N"))}";
        var early = Request(session.Dosid, "internalTechFactoryClaim", body);
        await LocalTechFactoryAction.HandleAsync(early, sessions, store, CancellationToken.None);
        Assert.That(early.Response.StatusCode, Is.EqualTo(409));
        var foreign = Request(session.Dosid, "internalTechFactoryClaim",
            $"job={Guid.NewGuid():N}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", Guid.NewGuid().ToString("N"))}");
        await LocalTechFactoryAction.HandleAsync(foreign, sessions, store, CancellationToken.None);
        Assert.That(foreign.Response.StatusCode, Is.EqualTo(400));
        var other = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "other", null, 10), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(other.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await store.StartAsync(other.AccountId, "shield", CancellationToken.None), Is.True);
        var otherJob = (await store.GetAsync(other.AccountId, CancellationToken.None)).Job!;
        var foreignBody = $"job={otherJob.Id:N}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", otherJob.Id.ToString("N"))}";
        foreign = Request(session.Dosid, "internalTechFactoryClaim", foreignBody);
        await LocalTechFactoryAction.HandleAsync(foreign, sessions, store, CancellationToken.None);
        Assert.That(foreign.Response.StatusCode, Is.EqualTo(409));
        clock.Advance(TimeSpan.FromMinutes(2));
        var claim = Request(session.Dosid, "internalTechFactoryClaim", body);
        await LocalTechFactoryAction.HandleAsync(claim, sessions, store, CancellationToken.None);
        Assert.That(claim.Response.StatusCode, Is.EqualTo(303));
        var replay = Request(session.Dosid, "internalTechFactoryClaim", body);
        await LocalTechFactoryAction.HandleAsync(replay, sessions, store, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        await sessions.InvalidateAsync(session.Dosid, CancellationToken.None);
        var expired = Request(session.Dosid, "internalTechFactoryStart", Form(session.Dosid, "repair"));
        await LocalTechFactoryAction.HandleAsync(expired, sessions, store, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
    }

    private static string Form(string dosid, string recipe) =>
        $"recipe={recipe}&key={LocalTechFactoryAction.CreateFormKey(dosid, "start", recipe)}";

    [Test]
    public void Page_ShowsProductionAndNavigation()
    {
        var page = LocalTechFactory.Page(new GameAuthSession { Username = "pilot" },
            new LocalTechFactoryState(900, new Dictionary<string, int>(), null), DateTimeOffset.UtcNow);
        Assert.That(page, Does.Contain("id=\"techFactory\""));
        Assert.That(page, Does.Contain("action=internalTechFactoryStart"));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalTechFactory"));
    }

    private static DefaultHttpContext Request(string? dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
