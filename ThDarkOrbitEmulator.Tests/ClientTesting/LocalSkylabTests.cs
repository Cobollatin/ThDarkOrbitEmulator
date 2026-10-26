using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSkylabTests
{
    [Test]
    public async Task ProductionAndUpgrades_PersistPerAccount()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var store = new LocalSkylabStore(services.GetRequiredService<IDistributedCache>(), clock);
        var account = Guid.NewGuid();
        Assert.That(await store.UpgradeAsync(account, "unknown", CancellationToken.None), Is.False);
        Assert.That(await store.UpgradeAsync(account, "prometium", CancellationToken.None), Is.True);
        Assert.That(await store.UpgradeAsync(account, "prometium", CancellationToken.None), Is.True);
        Assert.That(await store.UpgradeAsync(account, "prometium", CancellationToken.None), Is.False);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.True);
        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Collectors.Single(c => c.Id == "prometium").Level, Is.EqualTo(3));
            Assert.That(state.Collectors.Single(c => c.Id == "prometium").Stock, Is.EqualTo(260));
            Assert.That(state.Collectors.Single(c => c.Id == "endurium").Stock, Is.EqualTo(520));
        });
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Collectors[0].Stock, Is.EqualTo(500));
    }

    [Test]
    public async Task FirstVisit_StartsProductionClock()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var store = new LocalSkylabStore(services.GetRequiredService<IDistributedCache>(), clock);
        var account = Guid.NewGuid();
        await store.GetAsync(account, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.True);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Collectors[0].Stock, Is.EqualTo(510));
    }

    [Test]
    public async Task LongAbsence_CollectsOnlyStorageCap_WithoutSavingAnOldBacklog()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var store = new LocalSkylabStore(services.GetRequiredService<IDistributedCache>(), clock);
        var account = Guid.NewGuid();
        var start = (await store.GetAsync(account, CancellationToken.None)).LastCollected;
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(30));

        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.True);
        var collected = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(collected.Collectors[0].Stock, Is.EqualTo(500 + LocalSkylab.MaxCollectMinutes * 10));
            Assert.That(collected.LastCollected, Is.EqualTo(start.AddHours(3)));
            Assert.That(LocalSkylab.CollectableMinutes(collected.LastCollected, clock.GetUtcNow()), Is.Zero);
        });
        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.False);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That(await store.CollectAsync(account, CancellationToken.None), Is.True);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Collectors[0].Stock,
            Is.EqualTo(500 + (LocalSkylab.MaxCollectMinutes + 1) * 10));
    }

    [Test]
    public async Task Post_RequiresSessionCompanyAndValidAction()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalSkylabStore(cache, new TestClock());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalSkylabUpgrade", "collector=prometium");
        await LocalSkylabAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalSkylabUpgrade", "collector=prometium");
        await LocalSkylabAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var bad = Request(session.Dosid, "internalSkylabUpgrade", "collector=unknown");
        await LocalSkylabAction.HandleAsync(bad, sessions, store, CancellationToken.None);
        Assert.That(bad.Response.StatusCode, Is.EqualTo(400));
        var key = LocalSkylabAction.CreateFormKey(session.Dosid, "prometium", "1");
        var valid = Request(session.Dosid, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}");
        await LocalSkylabAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalSkylab&upgraded=prometium"));
        var replay = Request(session.Dosid, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}");
        await LocalSkylabAction.HandleAsync(replay, sessions, store, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        var foreign = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other", null, 8), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(foreign.Dosid, "mmo", CancellationToken.None), Is.True);
        var changedAccount = Request(foreign.Dosid, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}");
        await LocalSkylabAction.HandleAsync(changedAccount, sessions, store, CancellationToken.None);
        Assert.That(changedAccount.Response.StatusCode, Is.EqualTo(400));
        foreach (var body in new[]
        {
            $"collector=prometium&level=1&key={key}&quantity=-1",
            $"collector=prometium&collector=endurium&level=1&key={key}",
            $"collector=endurium&level=1&key={key}",
            $"collector=prometium&level=999999999999999999&key={key}",
            "collector=prometium&level=1"
        })
        {
            var invalid = Request(session.Dosid, "internalSkylabUpgrade", body);
            await LocalSkylabAction.HandleAsync(invalid, sessions, store, CancellationToken.None);
            Assert.That(invalid.Response.StatusCode, Is.EqualTo(400), body);
        }
        var expired = Request("missing-session", "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}");
        await LocalSkylabAction.HandleAsync(expired, sessions, store, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        var unknownKey = LocalSkylabAction.CreateFormKey(session.Dosid, "unknown", "1");
        var unknown = Request(session.Dosid, "internalSkylabUpgrade", $"collector=unknown&level=1&key={unknownKey}");
        await LocalSkylabAction.HandleAsync(unknown, sessions, store, CancellationToken.None);
        Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        var time = state.LastCollected.Ticks;
        var collectKey = LocalSkylabAction.CreateFormKey(session.Dosid, "collect", time.ToString());
        var early = Request(session.Dosid, "internalSkylabCollect", $"time={time}&key={collectKey}");
        await LocalSkylabAction.HandleAsync(early, sessions, store, CancellationToken.None);
        Assert.That(early.Response.StatusCode, Is.EqualTo(409));
        var badCollect = Request(session.Dosid, "internalSkylabCollect", $"time={time}&key={collectKey}&quantity=999999999999");
        await LocalSkylabAction.HandleAsync(badCollect, sessions, store, CancellationToken.None);
        Assert.That(badCollect.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ConcurrentUpgrade_OnlyOneChangesStock_AndCacheExpiryResetsLegacyState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new TestClock();
        var store = new LocalSkylabStore(cache, clock);
        var account = Guid.NewGuid();
        var first = store.ChangeAsync(account, "prometium", 1, default, "one", CancellationToken.None);
        var second = store.ChangeAsync(account, "prometium", 1, default, "two", CancellationToken.None);
        var results = await Task.WhenAll(first, second);
        Assert.That(results.Count(r => r == ThDarkOrbitEmulator.ApiService.Features.Economy.SkylabChange.Applied), Is.EqualTo(1));
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Collectors[0].Stock, Is.EqualTo(400));
        await cache.RemoveAsync($"localskylab:{account:N}");
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Collectors[0].Level, Is.EqualTo(1));
    }

    [Test]
    public void Page_ExposesCollectorActionsFromLocalMenu()
    {
        var page = LocalSkylab.Page(new GameAuthSession { Username = "pilot" },
            new LocalSkylabState(DateTimeOffset.UtcNow, [new("prometium", "Prometium", 2, 300)]));
        Assert.That(page, Does.Contain("id=\"skylab\""));
        Assert.That(page, Does.Contain("action=internalSkylabUpgrade"));
        Assert.That(page, Does.Contain("action=internalSkylabCollect"));
        Assert.That(page, Does.Contain("Upgrade (200 Prometium)"));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalSkylab"));
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
