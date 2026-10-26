using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSkylabValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    [TestCase("", true)]
    [TestCase("&from=internalStart&prc=100&lang=es", true)]
    [TestCase("&FROM=internalStart", false)]
    [TestCase("&collector=prometium", false)]
    [TestCase("&lang=en&lang=es", false)]
    [TestCase("&action=internalSkylab", false)]
    [TestCase("&key=abc", false)]
    public void PageQuery_AllowsOnlyShellParameters(string extra, bool valid)
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab" + extra));
        Assert.That(LocalSkylabAction.ValidPageQuery(query), Is.EqualTo(valid));
    }

    [Test]
    public void PageQuery_RejectsOverlongValues()
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab&lang=" + new string('x', 65)));
        Assert.That(LocalSkylabAction.ValidPageQuery(query), Is.False);
    }

    [Test]
    public async Task Get_RequiresPost()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "internalSkylabCollect", "");
        context.Request.Method = "GET";
        await LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
    }

    [TestCase("?action=internalSkylabUpgrade&collector=prometium")]
    [TestCase("?action=internalSkylabUpgrade&action=internalSkylabCollect")]
    [TestCase("?action=internalSkylabSell")]
    public async Task ExtraOrUnknownQuery_Returns400(string query)
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "internalSkylabUpgrade", Upgrade(h, "prometium", 1));
        context.Request.QueryString = new QueryString(query);
        await LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "internalSkylabUpgrade", "{\"collector\":\"prometium\"}");
        context.Request.ContentType = "application/json";
        await LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ExpiredForm_Returns409_AndDoesNotSpend()
    {
        var h = await HarnessAsync();
        var body = Upgrade(h, "prometium", 1);
        h.Clock.Advance(LocalSkylabAction.FormLifetime + TimeSpan.FromSeconds(1));
        var status = await SendAsync(h, "internalSkylabUpgrade", body);
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(status.Status, Is.EqualTo(409));
            Assert.That(status.Html, Does.Contain("expired"));
            Assert.That(state.Collectors.Single(c => c.Id == "prometium").Level, Is.EqualTo(1));
            Assert.That(state.Collectors.Single(c => c.Id == "prometium").Stock, Is.EqualTo(500));
        });
    }

    [Test]
    public async Task FormAtLifetimeBoundary_IsAccepted()
    {
        var h = await HarnessAsync();
        var body = Upgrade(h, "prometium", 1);
        h.Clock.Advance(LocalSkylabAction.FormLifetime);
        Assert.That((await SendAsync(h, "internalSkylabUpgrade", body)).Status, Is.EqualTo(303));
    }

    [Test]
    public async Task FutureDatedForm_Returns400()
    {
        var h = await HarnessAsync();
        var key = LocalSkylabAction.CreateFormKey(h.Session.Dosid, "prometium", "1", Start.AddHours(1));
        var result = await SendAsync(h, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}");
        Assert.That(result.Status, Is.EqualTo(400));
    }

    [TestCase("0")]
    [TestCase("01")]
    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase("")]
    public async Task TamperedIssueTime_Returns400(string issued)
    {
        var h = await HarnessAsync();
        var parts = LocalSkylabAction.CreateFormKey(h.Session.Dosid, "prometium", "1", Start).Split('.');
        var key = $"{parts[0]}.{issued}.{parts[2]}";
        Assert.That((await SendAsync(h, "internalSkylabUpgrade", $"collector=prometium&level=1&key={Uri.EscapeDataString(key)}")).Status,
            Is.EqualTo(400));
    }

    [Test]
    public async Task ShiftedIssueTime_FailsSignature()
    {
        var h = await HarnessAsync();
        var parts = LocalSkylabAction.CreateFormKey(h.Session.Dosid, "prometium", "1", Start.AddHours(-2)).Split('.');
        var key = $"{parts[0]}.{Start.ToUnixTimeSeconds()}.{parts[2]}";
        Assert.That((await SendAsync(h, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}")).Status,
            Is.EqualTo(400));
    }

    [Test]
    public async Task OverlongOrOldFormatKey_Returns400()
    {
        var h = await HarnessAsync();
        var parts = LocalSkylabAction.CreateFormKey(h.Session.Dosid, "prometium", "1", Start).Split('.');
        foreach (var key in new[] { new string('a', 129), $"{parts[0]}.{parts[2]}", $"{parts[0]}.{parts[1]}.{parts[2]}.x" })
            Assert.That((await SendAsync(h, "internalSkylabUpgrade", $"collector=prometium&level=1&key={key}")).Status,
                Is.EqualTo(400), key);
    }

    [Test]
    public async Task UpgradeWithoutResources_Returns409WithReason()
    {
        var h = await HarnessAsync();
        for (var level = 1; level <= 2; level++)
            Assert.That((await SendAsync(h, "internalSkylabUpgrade", Upgrade(h, "terbium", level))).Status, Is.EqualTo(303));
        var poor = await SendAsync(h, "internalSkylabUpgrade", Upgrade(h, "terbium", 3));
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(poor.Status, Is.EqualTo(409));
            Assert.That(poor.Html, Does.Contain("not enough resources"));
            Assert.That(state.Collectors.Single(c => c.Id == "terbium").Level, Is.EqualTo(3));
            Assert.That(state.Collectors.Single(c => c.Id == "terbium").Stock, Is.EqualTo(500 - 100 - 200));
        });
    }

    [Test]
    public async Task StaleLevel_Returns409()
    {
        var h = await HarnessAsync();
        var first = Upgrade(h, "endurium", 1);
        var second = Upgrade(h, "endurium", 1);
        Assert.That((await SendAsync(h, "internalSkylabUpgrade", first)).Status, Is.EqualTo(303));
        var stale = await SendAsync(h, "internalSkylabUpgrade", second);
        Assert.Multiple(() =>
        {
            Assert.That(stale.Status, Is.EqualTo(409));
            Assert.That(stale.Html, Does.Contain("already submitted or the collector state has changed"));
        });
    }

    [Test]
    public async Task Store_ReportsMaxLevelAndInsufficientResources()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new TestClock();
        var store = new LocalSkylabStore(cache, clock);
        var account = Guid.NewGuid();
        await cache.SetStringAsync($"localskylab:{account:N}", System.Text.Json.JsonSerializer.Serialize(
            new LocalSkylabState(Start, [new("prometium", "Prometium", 10, 5000), new("endurium", "Endurium", 3, 10),
                new("terbium", "Terbium", 1, 500)])));
        {
            Assert.That(await store.ChangeAsync(account, "prometium", 10, default, "a", CancellationToken.None),
                Is.EqualTo(SkylabChange.MaxLevel));
            Assert.That(await store.ChangeAsync(account, "endurium", 3, default, "b", CancellationToken.None),
                Is.EqualTo(SkylabChange.InsufficientResources));
            Assert.That(await store.ChangeAsync(account, null, 0, Start, "c", CancellationToken.None),
                Is.EqualTo(SkylabChange.NothingToCollect));
            Assert.That(await store.ChangeAsync(Guid.Empty, null, 0, Start, "d", CancellationToken.None),
                Is.EqualTo(SkylabChange.Invalid));
        }
    }

    [Test]
    public async Task Collect_IsCappedAtSixtyMinutes_AndDiscardsElapsedTimeBeyondCapacity()
    {
        var h = await HarnessAsync();
        var initial = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(90.5));
        Assert.That((await SendAsync(h, "internalSkylabCollect", Collect(h, initial.LastCollected))).Status, Is.EqualTo(303));
        var state = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Collectors.Select(c => c.Stock), Is.All.EqualTo(500 + 60 * 10));
            Assert.That(state.LastCollected, Is.EqualTo(initial.LastCollected.AddMinutes(90)));
        });
        Assert.That((await SendAsync(h, "internalSkylabCollect", Collect(h, state.LastCollected))).Status, Is.EqualTo(409));
        Assert.That((await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None)).Collectors[0].Stock,
            Is.EqualTo(500 + 600));
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await SendAsync(h, "internalSkylabCollect", Collect(h, state.LastCollected))).Status, Is.EqualTo(303));
        Assert.That((await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None)).Collectors[0].Stock,
            Is.EqualTo(500 + 600 + 10));
    }

    [Test]
    public async Task Collect_TooEarly_Returns409WithReason()
    {
        var h = await HarnessAsync();
        var initial = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromSeconds(59));
        var early = await SendAsync(h, "internalSkylabCollect", Collect(h, initial.LastCollected));
        Assert.Multiple(() =>
        {
            Assert.That(early.Status, Is.EqualTo(409));
            Assert.That(early.Html, Does.Contain("Nothing to collect yet"));
        });
    }

    [Test]
    public async Task Collect_WithStaleTime_Returns409()
    {
        var h = await HarnessAsync();
        var initial = await h.Store.GetAsync(h.Session.AccountId, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That((await SendAsync(h, "internalSkylabCollect", Collect(h, initial.LastCollected.AddMinutes(-1)))).Status,
            Is.EqualTo(409));
    }

    [Test]
    public async Task ErrorPages_AreHtmlAndNotCached_AndDoNotEchoInput()
    {
        var h = await HarnessAsync();
        var context = Request(h.Session.Dosid, "internalSkylabUpgrade", "collector=%3Cscript%3E&level=1&key=x");
        await LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);
        var html = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Not.Contain("<script>"));
            Assert.That(html, Does.Contain("action=internalSkylab"));
        });
    }

    [Test]
    public void Page_ShowsPendingProductionAndCountdown()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "d" };
        var state = new LocalSkylabState(Start, [new("prometium", "Prometium", 2, 300), new("terbium", "Terbium", 10, 9000)]);
        var waiting = LocalSkylab.Page(session, state, Start.AddSeconds(20));
        var ready = LocalSkylab.Page(session, state, Start.AddMinutes(3).AddSeconds(30));
        var full = LocalSkylab.Page(session, state, Start.AddHours(5));
        Assert.Multiple(() =>
        {
            Assert.That(waiting, Does.Contain("Nothing to collect yet. The first minute completes in 40 s."));
            Assert.That(waiting, Does.Contain(">Collect production</button>").And.Contain(" disabled>Collect production"));
            Assert.That(ready, Does.Contain("3 minute(s) of production ready. Next minute completes in 30 s."));
            Assert.That(ready, Does.Contain("60 units pending"));
            Assert.That(ready, Does.Contain("<td>300</td><td>60</td>"));
            Assert.That(ready, Does.Not.Contain(" disabled>"));
            Assert.That(full, Does.Contain("Storage full: 60 minutes"));
            Assert.That(full, Does.Contain("6000 units pending"));
            Assert.That(full, Does.Contain("Maximum level"));
        });
    }

    [Test]
    public void Page_EncodesUsername()
    {
        var page = LocalSkylab.Page(new GameAuthSession { Username = "<b>x</b>", Dosid = "d" },
            new LocalSkylabState(Start, [new("prometium", "Prometium", 1, 0)]), Start);
        Assert.That(page, Does.Not.Contain("<b>x</b>"));
        Assert.That(page, Does.Contain("100 more units needed"));
    }

    private static string Upgrade(Harness h, string collector, int level) =>
        $"collector={collector}&level={level}&key={LocalSkylabAction.CreateFormKey(h.Session.Dosid, collector, level.ToString(), h.Clock.GetUtcNow())}";

    private static string Collect(Harness h, DateTimeOffset last) =>
        $"time={last.Ticks}&key={LocalSkylabAction.CreateFormKey(h.Session.Dosid, "collect", last.Ticks.ToString(), h.Clock.GetUtcNow())}";

    private static async Task<(int Status, string Html)> SendAsync(Harness h, string action, string body)
    {
        var context = Request(h.Session.Dosid, action, body);
        await LocalSkylabAction.HandleAsync(context, h.Sessions, h.Store, CancellationToken.None, h.Clock);
        return (context.Response.StatusCode, Body(context));
    }

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
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
