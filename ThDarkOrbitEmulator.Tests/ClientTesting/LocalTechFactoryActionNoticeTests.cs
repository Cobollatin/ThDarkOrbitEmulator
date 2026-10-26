using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-123: Tech Factory confirms a started or claimed job from saved state after the redirect.
public class LocalTechFactoryActionNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static bool Valid(string suffix) => LocalTechFactoryAction.ValidPageQuery(
        new QueryCollection(QueryHelpers.ParseQuery("?action=internalTechFactory" + suffix)));

    [TestCase("&started=targeting")]
    [TestCase("&claimed=targeting")]
    [TestCase("&started=tech_shield&filter=all")]
    [TestCase("&claimed=repair&detail=repair")]
    [TestCase("&started=unknown-recipe")]
    public void PageQuery_AcceptsNoticeParameters(string suffix) => Assert.That(Valid(suffix), Is.True);

    [TestCase("&started=")]
    [TestCase("&claimed=")]
    [TestCase("&started=Targeting")]
    [TestCase("&claimed=TARGETING")]
    [TestCase("&Started=targeting")]
    [TestCase("&started=targeting&started=shield")]
    [TestCase("&claimed=targeting&claimed=targeting")]
    [TestCase("&started=targeting&claimed=targeting")]
    [TestCase("&started=a%20b")]
    [TestCase("&claimed=%3Cscript%3E")]
    [TestCase("&started=x%00")]
    public void PageQuery_RejectsMalformedNoticeParameters(string suffix) => Assert.That(Valid(suffix), Is.False);

    [Test]
    public void PageQuery_EnforcesNoticeLengthLimit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Valid("&started=" + new string('a', 64)), Is.True);
            Assert.That(Valid("&started=" + new string('a', 65)), Is.False);
            Assert.That(Valid("&claimed=" + new string('a', 65)), Is.False);
        });
    }

    [TestCase("started", "targeting", "/indexInternal.es?action=internalTechFactory&started=targeting")]
    [TestCase("claimed", "tech_shield", "/indexInternal.es?action=internalTechFactory&claimed=tech_shield")]
    [TestCase("claimed", "Bad Id", "/indexInternal.es?action=internalTechFactory")]
    [TestCase("claimed", "", "/indexInternal.es?action=internalTechFactory")]
    [TestCase("claimed", null, "/indexInternal.es?action=internalTechFactory")]
    public void RedirectTarget_OnlyCarriesValidRecipeIds(string parameter, string? recipe, string expected) =>
        Assert.That(LocalTechFactoryAction.RedirectTarget(parameter, recipe), Is.EqualTo(expected));

    [Test]
    public void StartedNotice_ShowsSavedJobAndRemainingComponents()
    {
        var state = new LocalTechFactoryState(900, new Dictionary<string, int>(),
            new LocalTechJob("targeting", Now.AddMinutes(2), Guid.NewGuid()));
        var html = Page(state, started: "targeting");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"techFactoryStarted\""));
            Assert.That(html, Does.Contain("role=\"status\""));
            Assert.That(html, Does.Contain("Started Targeting boost for 100 components."));
            Assert.That(html, Does.Contain("Ready at 2026-10-24 12:02 UTC (2 min left)."));
            Assert.That(html, Does.Contain("Components left: 900."));
            Assert.That(html, Does.Not.Contain("techFactoryClaimed"));
        });
    }

    [Test]
    public void StartedNotice_SaysReadyWhenJobAlreadyFinished()
    {
        var state = new LocalTechFactoryState(900, new Dictionary<string, int>(),
            new LocalTechJob("targeting", Now.AddMinutes(-1), Guid.NewGuid()));
        Assert.That(Page(state, started: "targeting"), Does.Contain("Ready to claim."));
    }

    [TestCase("shield")]
    [TestCase("unknown")]
    public void StartedNotice_HiddenWhenSavedJobDoesNotMatch(string started)
    {
        var state = new LocalTechFactoryState(900, new Dictionary<string, int>(),
            new LocalTechJob("targeting", Now.AddMinutes(2), Guid.NewGuid()));
        Assert.That(Page(state, started: started), Does.Not.Contain("techFactoryStarted"));
    }

    [Test]
    public void StartedNotice_HiddenWithoutJobOrWhenJobRecipeLeftCatalog()
    {
        var idle = new LocalTechFactoryState(1000, new Dictionary<string, int>(), null);
        var orphan = new LocalTechFactoryState(1000, new Dictionary<string, int>(),
            new LocalTechJob("retired", Now.AddMinutes(2), Guid.NewGuid()));
        Assert.Multiple(() =>
        {
            Assert.That(Page(idle, started: "targeting"), Does.Not.Contain("techFactoryStarted"));
            Assert.That(Page(orphan, started: "retired"), Does.Not.Contain("techFactoryStarted"));
        });
    }

    [Test]
    public void ClaimedNotice_ShowsOwnedResourceAndFreeSlot()
    {
        var state = new LocalTechFactoryState(850, new Dictionary<string, int> { ["shield"] = 3 }, null);
        var html = Page(state, claimed: "shield");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"techFactoryClaimed\""));
            Assert.That(html, Does.Contain("Claimed Shield boost. Owned resource: 3. Components: 850. The production slot is free."));
            Assert.That(html, Does.Not.Contain("techFactoryStarted"));
        });
    }

    [Test]
    public void ClaimedNotice_OmitsFreeSlotWhenAnotherJobRuns()
    {
        var state = new LocalTechFactoryState(700, new Dictionary<string, int> { ["shield"] = 1 },
            new LocalTechJob("repair", Now.AddMinutes(4), Guid.NewGuid()));
        var html = Page(state, claimed: "shield");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Claimed Shield boost. Owned resource: 1. Components: 700.</p>"));
            Assert.That(html, Does.Not.Contain("The production slot is free."));
        });
    }

    [TestCase("shield")]
    [TestCase("unknown")]
    public void ClaimedNotice_HiddenWithoutProducedResource(string claimed)
    {
        var state = new LocalTechFactoryState(1000, new Dictionary<string, int> { ["shield"] = 0, ["unknown"] = 5 }, null);
        Assert.That(Page(state, claimed: claimed), Does.Not.Contain("techFactoryClaimed"));
    }

    [Test]
    public void Notices_EncodeRecipeNames()
    {
        var catalog = new[] { new LocalTechRecipe("evil", "<b>Evil</b> & co", 10, 1) };
        var started = new LocalTechFactoryState(5, new Dictionary<string, int>(), new LocalTechJob("evil", Now.AddMinutes(1), Guid.NewGuid()));
        var claimed = new LocalTechFactoryState(5, new Dictionary<string, int> { ["evil"] = 2 }, null);
        var startedHtml = LocalTechFactory.Notice(started, Now, catalog, "evil", null);
        var claimedHtml = LocalTechFactory.Notice(claimed, Now, catalog, null, "evil");
        Assert.Multiple(() =>
        {
            Assert.That(startedHtml, Does.Not.Contain("<b>"));
            Assert.That(startedHtml, Does.Contain("&lt;b&gt;Evil&lt;/b&gt;"));
            Assert.That(claimedHtml, Does.Not.Contain("<b>"));
            Assert.That(claimedHtml, Does.Contain("&lt;b&gt;Evil&lt;/b&gt;"));
        });
    }

    [Test]
    public void Notice_NeverShowsNegativeComponents()
    {
        var state = new LocalTechFactoryState(-5, new Dictionary<string, int> { ["targeting"] = 1 }, null);
        Assert.That(Page(state, claimed: "targeting"), Does.Contain("Components: 0."));
    }

    [Test]
    public void Notice_WorksWithDetailAndFilter()
    {
        var state = new LocalTechFactoryState(900, new Dictionary<string, int>(),
            new LocalTechJob("targeting", Now.AddMinutes(2), Guid.NewGuid()));
        var html = LocalTechFactory.Page(Session(), state, Now, null, "targeting", "all", "targeting", null);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("techFactoryStarted"));
            Assert.That(html, Does.Contain("techFactoryDetail"));
        });
    }

    [Test]
    public async Task StartThenClaim_RedirectsWithRecipeAndRendersNotices()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clock = new MutableClock(Now);
        var store = new LocalTechFactoryStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        var start = Request(session.Dosid, "internalTechFactoryStart",
            $"recipe=shield&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "start", "shield", Now)}");
        await LocalTechFactoryAction.HandleAsync(start, sessions, store, CancellationToken.None, clock);
        var afterStart = await store.GetAsync(session.AccountId, CancellationToken.None);
        var startedHtml = LocalTechFactory.Page(session, afterStart, clock.GetUtcNow(), null, null, null, "shield", null);

        clock.Now = Now.AddMinutes(3);
        var jobId = afterStart.Job!.Id.ToString("N");
        var claim = Request(session.Dosid, "internalTechFactoryClaim",
            $"job={jobId}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", jobId, clock.Now)}");
        await LocalTechFactoryAction.HandleAsync(claim, sessions, store, CancellationToken.None, clock);
        var afterClaim = await store.GetAsync(session.AccountId, CancellationToken.None);
        var claimedHtml = LocalTechFactory.Page(session, afterClaim, clock.GetUtcNow(), null, null, null, null, "shield");

        Assert.Multiple(() =>
        {
            Assert.That(start.Response.StatusCode, Is.EqualTo(303));
            Assert.That(start.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalTechFactory&started=shield"));
            Assert.That(startedHtml, Does.Contain("Started Shield boost for 150 components. Ready at 2026-10-24 12:03 UTC (3 min left). Components left: 850."));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(303));
            Assert.That(claim.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalTechFactory&claimed=shield"));
            Assert.That(claimedHtml, Does.Contain("Claimed Shield boost. Owned resource: 1. Components: 850. The production slot is free."));
        });
    }

    [Test]
    public async Task EarlyClaim_DoesNotRedirect()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clock = new MutableClock(Now);
        var store = new LocalTechFactoryStore(cache, clock);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), "mmo", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await store.StartAsync(session.AccountId, "targeting", CancellationToken.None), Is.True);
        var jobId = (await store.GetAsync(session.AccountId, CancellationToken.None)).Job!.Id.ToString("N");
        var claim = Request(session.Dosid, "internalTechFactoryClaim",
            $"job={jobId}&key={LocalTechFactoryAction.CreateFormKey(session.Dosid, "claim", jobId, Now)}");
        await LocalTechFactoryAction.HandleAsync(claim, sessions, store, CancellationToken.None, clock);
        Assert.Multiple(() =>
        {
            Assert.That(claim.Response.StatusCode, Is.EqualTo(409));
            Assert.That(claim.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    private static string Page(LocalTechFactoryState state, string? started = null, string? claimed = null) =>
        LocalTechFactory.Page(Session(), state, Now, null, null, null, started, claimed);

    private static GameAuthSession Session() => new()
    {
        Dosid = "notice-dosid", AccountId = Guid.NewGuid(), Username = "pilot", Company = "mmo"
    };

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

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
