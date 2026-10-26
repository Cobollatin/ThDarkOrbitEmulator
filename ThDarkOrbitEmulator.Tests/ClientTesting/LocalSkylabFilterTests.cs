using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-148: emulator-defined Skylab collector filters (all, upgradable, saving, maxed).
public class LocalSkylabFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    // Prometium level 3 with 100 stock is saving (needs 300), Endurium level 1 with 500 is upgradable, Terbium is maxed.
    private static LocalSkylabState Mixed() =>
        new(Start, [
            new("prometium", "Prometium", 3, 100),
            new("endurium", "Endurium", 1, 500),
            new("terbium", "Terbium", LocalSkylab.MaxLevel, 50)
        ]);

    private static string[] Rows(string page) =>
        Regex.Matches(page, "<tr id=\"skylabRow-([a-z]+)\"").Select(m => m.Groups[1].Value).ToArray();

    private static bool Valid(string extra) =>
        LocalSkylabAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab" + extra)));

    private static string Page(string? filter, string? detail = null) =>
        LocalSkylab.Page(Session, Mixed(), Start, detail, filter: filter);

    [TestCase("&filter=all")]
    [TestCase("&filter=maxed&detail=terbium")]
    [TestCase("&filter=bogus")]
    [TestCase("&filter=saving&upgraded=prometium")]
    public void PageQuery_AcceptsLowercaseFilter(string extra) => Assert.That(Valid(extra), Is.True);

    [TestCase("&filter=")]
    [TestCase("&Filter=all")]
    [TestCase("&FILTER=all")]
    [TestCase("&filter=Maxed")]
    [TestCase("&filter=max-ed")]
    [TestCase("&filter=maxed1")]
    [TestCase("&filter=all&filter=maxed")]
    [TestCase("&filter=%00")]
    public void PageQuery_RejectsMalformedFilter(string extra) => Assert.That(Valid(extra), Is.False);

    [Test]
    public void PageQuery_RejectsOverlongFilter() => Assert.That(Valid("&filter=" + new string('a', 17)), Is.False);

    [TestCase(null, true)]
    [TestCase("all", true)]
    [TestCase("upgradable", true)]
    [TestCase("saving", true)]
    [TestCase("maxed", true)]
    [TestCase("ready", false)]
    public void IsFilter_KnowsOnlyCatalogFilters(string? filter, bool expected) =>
        Assert.That(LocalSkylab.IsFilter(filter), Is.EqualTo(expected));

    [TestCase(1, 99, "saving")]
    [TestCase(1, 100, "upgradable")]
    [TestCase(9, 900, "upgradable")]
    [TestCase(9, 899, "saving")]
    [TestCase(10, 0, "maxed")]
    [TestCase(10, 100000, "maxed")]
    public void StatusOf_UsesNextUpgradeCostAndMaxLevel(int level, int stock, string expected) =>
        Assert.That(LocalSkylab.StatusOf(new("prometium", "Prometium", level, stock)), Is.EqualTo(expected));

    [TestCase(null, new[] { "prometium", "endurium", "terbium" })]
    [TestCase("all", new[] { "prometium", "endurium", "terbium" })]
    [TestCase("upgradable", new[] { "endurium" })]
    [TestCase("saving", new[] { "prometium" })]
    [TestCase("maxed", new[] { "terbium" })]
    public void Filter_SelectsRows(string? filter, string[] expected) =>
        Assert.That(Rows(Page(filter)), Is.EqualTo(expected));

    [Test]
    public void Tabs_ShowCounts_AndMarkCurrent()
    {
        var page = Page("saving");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain(">All (3)</a>"));
            Assert.That(page, Does.Contain(">Upgradable (1)</a>"));
            Assert.That(page, Does.Contain(">Saving (1)</a>"));
            Assert.That(page, Does.Contain(">Maximum level (1)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"saving\" aria-current=\"page\""));
            Assert.That(page, Does.Not.Contain("data-filter=\"all\" aria-current"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSkylab\">All (3)"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSkylab&amp;filter=maxed\""));
        });
    }

    [Test]
    public void DefaultPage_MarksAllCurrent_AndRowsCarryStatus()
    {
        var page = Page(null);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-filter=\"all\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("<tr id=\"skylabRow-prometium\" data-status=\"saving\">"));
            Assert.That(page, Does.Contain("<tr id=\"skylabRow-endurium\" data-status=\"upgradable\">"));
            Assert.That(page, Does.Contain("<tr id=\"skylabRow-terbium\" data-status=\"maxed\">"));
            Assert.That(page, Does.Not.Contain("id=\"skylabEmpty\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSkylab&amp;detail=prometium\""));
        });
    }

    [Test]
    public void EmptyFilter_ShowsNotice_AndKeepsCollectForm()
    {
        var fresh = new LocalSkylabState(Start, [new("prometium", "Prometium", 1, 500)]);
        var page = LocalSkylab.Page(Session, fresh, Start.AddMinutes(5), filter: "maxed");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.Empty);
            Assert.That(page, Does.Contain("<p id=\"skylabEmpty\">No collectors match the maximum level filter.</p>"));
            Assert.That(page, Does.Contain("action=internalSkylabCollect"));
            Assert.That(page, Does.Contain("id=\"skylabSummary\""));
        });
    }

    [Test]
    public void FilteredRowLinks_KeepFilter()
    {
        var page = Page("upgradable");
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSkylab&amp;filter=upgradable&amp;detail=endurium\""));
    }

    [Test]
    public void Detail_NavigatesWithinFilter_AndCloseKeepsFilter()
    {
        var state = new LocalSkylabState(Start, [
            new("prometium", "Prometium", 1, 500),
            new("endurium", "Endurium", 1, 10),
            new("terbium", "Terbium", 1, 500)
        ]);
        var page = LocalSkylab.Page(Session, state, Start, "prometium", filter: "upgradable");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<p>Collector 1 of 2</p>"));
            Assert.That(page, Does.Not.Contain("id=\"skylabDetailPrevious\""));
            Assert.That(page, Does.Contain("id=\"skylabDetailNext\" href=\"/indexInternal.es?action=internalSkylab&amp;filter=upgradable&amp;detail=terbium\""));
            Assert.That(page, Does.Contain("id=\"skylabDetailClose\" href=\"/indexInternal.es?action=internalSkylab&amp;filter=upgradable\""));
        });
    }

    [Test]
    public void DetailOutsideFilter_IsNotInFilter_AndRendersNoPanel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalSkylab.InFilter(Mixed(), "terbium", "maxed"), Is.True);
            Assert.That(LocalSkylab.InFilter(Mixed(), "terbium", "saving"), Is.False);
            Assert.That(LocalSkylab.InFilter(Mixed(), "terbium", null), Is.True);
            Assert.That(LocalSkylab.InFilter(Mixed(), "seprom", null), Is.False);
            Assert.That(Page("saving", "terbium"), Does.Not.Contain("id=\"skylabDetail\""));
        });
    }

    // Form keys carry a random nonce, so compare the pages without them.
    private static string WithoutKeys(string page) => Regex.Replace(page, "name=\"key\" value=\"[^\"]*\"", "");

    [Test]
    public void AllFilter_MatchesUnfilteredPage() =>
        Assert.That(WithoutKeys(Page("all")), Is.EqualTo(WithoutKeys(Page(null))));

    // Endpoint order: malformed filter 400 before the session; unknown filter and out-of-filter detail 404 after it.
    [Test]
    public async Task Endpoint_MalformedFilter_Returns400BeforeSession()
    {
        var harness = await HarnessAsync();
        AssertError(await SendAsync("?action=internalSkylab&filter=Maxed", null, harness), StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Endpoint_UnknownFilter_Returns401WithoutSession_Then404()
    {
        var harness = await HarnessAsync();
        var anonymous = await SendAsync("?action=internalSkylab&filter=bogus", null, harness);
        var signedIn = await SendAsync("?action=internalSkylab&filter=bogus", harness.Session.Dosid, harness);
        AssertError(anonymous, StatusCodes.Status401Unauthorized);
        AssertError(signedIn, StatusCodes.Status404NotFound);
        Assert.That(Body(signedIn), Does.Not.Contain("bogus"));
    }

    [Test]
    public async Task Endpoint_DetailOutsideFilter_Returns404()
    {
        var harness = await HarnessAsync();
        // Fresh collectors are level 1 with 500 stock, so none is maxed.
        var context = await SendAsync("?action=internalSkylab&filter=maxed&detail=prometium", harness.Session.Dosid, harness);
        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Contain("not in the selected filter"));
    }

    [Test]
    public async Task Endpoint_UnknownCollector_StillReturns404BeforeFilterCheck()
    {
        var harness = await HarnessAsync();
        var context = await SendAsync("?action=internalSkylab&filter=upgradable&detail=seprom", harness.Session.Dosid, harness);
        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Contain("Unknown Skylab collector"));
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, LocalSkylabStore Skylab);

    private static async Task<Harness> HarnessAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return new Harness(sessions, session, new LocalSkylabStore(cache, TimeProvider.System));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid, Harness harness)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        // Only sessions and Skylab state are reached on these error paths; the 200 page needs Postgres balances.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, null!, null!,
            null!, harness.Skylab, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static void AssertError(DefaultHttpContext context, int status) =>
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), Body(context));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        });

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
