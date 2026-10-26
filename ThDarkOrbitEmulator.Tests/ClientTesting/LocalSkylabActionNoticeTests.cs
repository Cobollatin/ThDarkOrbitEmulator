using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-121: Skylab upgrade/collect redirect to a confirmation notice built from persisted state.
public class LocalSkylabActionNoticeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    private static LocalSkylabState State(int prometium = 1, int prometiumStock = 500) =>
        new(Start, [
            new("prometium", "Prometium", prometium, prometiumStock),
            new("endurium", "Endurium", 1, 520),
            new("terbium", "Terbium", 1, 530)
        ]);

    private static bool Valid(string extra) =>
        LocalSkylabAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab" + extra)));

    [TestCase("&upgraded=prometium")]
    [TestCase("&upgraded=unknown")]
    [TestCase("&collected=1")]
    [TestCase("&upgraded=terbium&detail=terbium")]
    [TestCase("&collected=1&detail=endurium&lang=es")]
    public void PageQuery_AcceptsNoticeKeys(string extra) => Assert.That(Valid(extra), Is.True);

    [TestCase("&upgraded=")]
    [TestCase("&UPGRADED=prometium")]
    [TestCase("&Upgraded=prometium")]
    [TestCase("&upgraded=Prometium")]
    [TestCase("&upgraded=prometium1")]
    [TestCase("&upgraded=prometium&upgraded=endurium")]
    [TestCase("&upgraded=%00")]
    [TestCase("&collected=")]
    [TestCase("&collected=0")]
    [TestCase("&collected=true")]
    [TestCase("&collected=1&collected=1")]
    [TestCase("&Collected=1")]
    [TestCase("&COLLECTED=1")]
    [TestCase("&upgraded=prometium&collected=1")]
    public void PageQuery_RejectsMalformedNoticeKeys(string extra) => Assert.That(Valid(extra), Is.False);

    [Test]
    public void PageQuery_RejectsOverlongUpgraded() =>
        Assert.That(Valid("&upgraded=" + new string('a', 33)), Is.False);

    [Test]
    public void Locations_AreCanonical()
    {
        Assert.That(LocalSkylabAction.UpgradedLocation("endurium"),
            Is.EqualTo("/indexInternal.es?action=internalSkylab&upgraded=endurium"));
        Assert.That(LocalSkylabAction.CollectedLocation,
            Is.EqualTo("/indexInternal.es?action=internalSkylab&collected=1"));
        Assert.That(Valid(LocalSkylabAction.UpgradedLocation("endurium")["/indexInternal.es?action=internalSkylab".Length..]), Is.True);
        Assert.That(Valid(LocalSkylabAction.CollectedLocation["/indexInternal.es?action=internalSkylab".Length..]), Is.True);
    }

    [Test]
    public void UpgradedNotice_ShowsLevelOutputAndStock()
    {
        var notice = LocalSkylab.ActionNotice(State(prometium: 3, prometiumStock: 200), "prometium", false);
        Assert.That(notice, Is.EqualTo("<p id=\"skylabActionNotice\" role=\"status\" data-collector=\"prometium\">" +
            "Prometium collector upgraded to level 3/10. Output: 30 units/minute. Stock: 200.</p>"));
    }

    [Test]
    public void UpgradedNotice_AtMaximumLevel_SaysSo() =>
        Assert.That(LocalSkylab.ActionNotice(State(prometium: 10), "prometium", false),
            Does.Contain("level 10/10").And.Contain("Maximum level reached."));

    [TestCase("prometium", 1)]
    [TestCase("unknown", 5)]
    [TestCase("Prometium", 5)]
    [TestCase("", 5)]
    public void UpgradedNotice_HiddenWhenUnknownOrNeverUpgraded(string id, int level) =>
        Assert.That(LocalSkylab.ActionNotice(State(prometium: level), id, false), Is.Empty);

    [Test]
    public void UpgradedNotice_ClampsNegativeStock() =>
        Assert.That(LocalSkylab.ActionNotice(State(prometium: 2, prometiumStock: -5), "prometium", false),
            Does.Contain("Stock: 0."));

    [Test]
    public void CollectedNotice_ListsAllStocks() =>
        Assert.That(LocalSkylab.ActionNotice(State(), null, true),
            Is.EqualTo("<p id=\"skylabActionNotice\" role=\"status\" data-collected=\"true\">" +
                "Production collected. Stock: Prometium 500, Endurium 520, Terbium 530.</p>"));

    [Test]
    public void NoNoticeKeys_ShowNothing() =>
        Assert.That(LocalSkylab.ActionNotice(State(prometium: 4), null, false), Is.Empty);

    [Test]
    public void Notice_EncodesCollectorNames()
    {
        var state = new LocalSkylabState(Start, [new("prometium", "<b>P</b>", 2, 10)]);
        Assert.Multiple(() =>
        {
            Assert.That(LocalSkylab.ActionNotice(state, "prometium", false), Does.Not.Contain("<b>"));
            Assert.That(LocalSkylab.ActionNotice(state, null, true), Does.Not.Contain("<b>"));
        });
    }

    [Test]
    public void Page_RendersNoticeBeforeDetailPanel()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 2), Start, "endurium", "prometium");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"skylabActionNotice\""));
            Assert.That(page.IndexOf("skylabActionNotice", StringComparison.Ordinal),
                Is.LessThan(page.IndexOf("id=\"skylabDetail\"", StringComparison.Ordinal)));
        });
        Assert.That(LocalSkylab.Page(Session, State(prometium: 2), Start), Does.Not.Contain("skylabActionNotice"));
    }

    [Test]
    public async Task UpgradeAndCollect_RedirectToNoticeThatRendersPersistedState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clock = new TestClock(Start);
        var store = new LocalSkylabStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);

        var upgrade = Request(session.Dosid, "internalSkylabUpgrade",
            $"collector=endurium&level=1&key={LocalSkylabAction.CreateFormKey(session.Dosid, "endurium", "1", Start)}");
        await LocalSkylabAction.HandleAsync(upgrade, sessions, store, CancellationToken.None, clock);
        Assert.That(upgrade.Response.StatusCode, Is.EqualTo(303));
        Assert.That(upgrade.Response.Headers.Location.ToString(), Is.EqualTo(LocalSkylabAction.UpgradedLocation("endurium")));
        Assert.That(LocalSkylab.ActionNotice(await store.GetAsync(session.AccountId, CancellationToken.None), "endurium", false),
            Does.Contain("Endurium collector upgraded to level 2/10. Output: 20 units/minute. Stock: 400."));

        clock.Now = Start.AddMinutes(2);
        var ticks = state.LastCollected.Ticks.ToString();
        var collect = Request(session.Dosid, "internalSkylabCollect",
            $"time={ticks}&key={LocalSkylabAction.CreateFormKey(session.Dosid, "collect", ticks, clock.Now)}");
        await LocalSkylabAction.HandleAsync(collect, sessions, store, CancellationToken.None, clock);
        Assert.That(collect.Response.StatusCode, Is.EqualTo(303));
        Assert.That(collect.Response.Headers.Location.ToString(), Is.EqualTo(LocalSkylabAction.CollectedLocation));
        Assert.That(LocalSkylab.ActionNotice(await store.GetAsync(session.AccountId, CancellationToken.None), null, true),
            Does.Contain("Stock: Prometium 520, Endurium 440, Terbium 520."));
    }

    [Test]
    public async Task FailedCollect_DoesNotRedirect()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clock = new TestClock(Start);
        var store = new LocalSkylabStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var ticks = (await store.GetAsync(session.AccountId, CancellationToken.None)).LastCollected.Ticks.ToString();
        var collect = Request(session.Dosid, "internalSkylabCollect",
            $"time={ticks}&key={LocalSkylabAction.CreateFormKey(session.Dosid, "collect", ticks, Start)}");
        await LocalSkylabAction.HandleAsync(collect, sessions, store, CancellationToken.None, clock);
        Assert.That(collect.Response.StatusCode, Is.EqualTo(409));
        Assert.That(collect.Response.Headers.Location.ToString(), Is.Empty);
    }

    private static DefaultHttpContext Request(string dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
