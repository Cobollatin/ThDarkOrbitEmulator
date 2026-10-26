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

// Menu-207: emulator-defined Skylab collector search (`q`), combined with the Menu-148 filters.
public class LocalSkylabSearchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    // Prometium is saving, Endurium upgradable, Terbium maxed.
    private static LocalSkylabState Mixed() =>
        new(Start, [
            new("prometium", "Prometium", 3, 100),
            new("endurium", "Endurium", 1, 500),
            new("terbium", "Terbium", LocalSkylab.MaxLevel, 50)
        ]);

    private static string[] Rows(string page) =>
        Regex.Matches(page, "<tr id=\"skylabRow-([a-z]+)\"").Select(m => m.Groups[1].Value).ToArray();

    private static IQueryCollection Query(string url) => new QueryCollection(QueryHelpers.ParseQuery(url));

    [TestCase("?action=internalSkylab&q=PROM", true)]
    [TestCase("?action=internalSkylab&filter=saving&q=prom", true)]
    [TestCase("?action=internalSkylab&q=", true)]
    [TestCase("?action=internalSkylab&q=%20%20", true)]
    [TestCase("?action=internalSkylab&q=%20ium%20", true)]
    [TestCase("?action=internalSkylab&q=end_ur-ium%202", true)]
    [TestCase("?action=internalSkylab&q=upgraded&upgraded=terbium", true)]
    [TestCase("?action=internalSkylab&q=&detail=terbium", true)]
    [TestCase("?action=internalSkylab&q=p", false)]
    [TestCase("?action=internalSkylab&q=%20p%20", false)]
    [TestCase("?action=internalSkylab&q=prom&q=terb", false)]
    [TestCase("?action=internalSkylab&Q=prom", false)]
    [TestCase("?action=internalSkylab&q=prom%0A", false)]
    [TestCase("?action=internalSkylab&q=%3Cscript%3E", false)]
    [TestCase("?action=internalSkylab&q=prom%C3%A9", false)]
    [TestCase("?action=internalSkylab&q=prom&detail=prometium", false)]
    public void Query_ValidatesSearch(string url, bool expected) =>
        Assert.That(LocalSkylabAction.ValidPageQuery(Query(url)), Is.EqualTo(expected));

    [Test]
    public void Query_RejectsSearchLongerThan40() =>
        Assert.Multiple(() =>
        {
            Assert.That(LocalSkylabAction.ValidPageQuery(Query("?action=internalSkylab&q=" + new string('a', 40))), Is.True);
            Assert.That(LocalSkylabAction.ValidPageQuery(Query("?action=internalSkylab&q=" + new string('a', 41))), Is.False);
        });

    [Test]
    public void Search_IsTrimmedAndBlankIsNull() =>
        Assert.Multiple(() =>
        {
            Assert.That(LocalSkylabAction.Search(Query("?action=internalSkylab&q=%20Terb%20")), Is.EqualTo("Terb"));
            Assert.That(LocalSkylabAction.Search(Query("?action=internalSkylab&q=%20%20")), Is.Null);
            Assert.That(LocalSkylabAction.Search(Query("?action=internalSkylab")), Is.Null);
        });

    [Test]
    public void Search_MatchesIdOrNameIgnoringCase()
    {
        var page = LocalSkylab.Page(Session, Mixed(), Start, search: "RIUM");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.EqualTo(new[] { "endurium" }));
            Assert.That(page, Does.Contain("1 of 3 collectors match"));
            Assert.That(page, Does.Contain("data-filter=\"all\" aria-current=\"page\" href=\"/indexInternal.es?action=internalSkylab&amp;q=RIUM\">All (1)"));
            Assert.That(page, Does.Contain("action=internalSkylab&amp;filter=upgradable&amp;q=RIUM\">Upgradable (1)"));
            Assert.That(page, Does.Contain("action=internalSkylab&amp;filter=saving&amp;q=RIUM\">Saving (0)"));
            Assert.That(page, Does.Contain("action=internalSkylab&amp;filter=maxed&amp;q=RIUM\">Maximum level (0)"));
            Assert.That(page, Does.Contain("name=\"q\" maxlength=\"40\" value=\"RIUM\""));
            // Row links open the detail panel without the search, since q and detail cannot be combined.
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSkylab&amp;detail=endurium\""));
            Assert.That(page, Does.Not.Contain("detail=endurium&amp;q="));
        });
    }

    [Test]
    public void Search_WithFilter_KeepsFilterInFormAndClearLink()
    {
        var page = LocalSkylab.Page(Session, Mixed(), Start, filter: "saving", search: "prom");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.EqualTo(new[] { "prometium" }));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"filter\" value=\"saving\">"));
            Assert.That(page, Does.Contain("1 of 1 collectors match"));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalSkylab&amp;filter=saving\">Clear search</a>"));
        });
    }

    [Test]
    public void NoMatches_ShowsSearchEmptyState()
    {
        var page = LocalSkylab.Page(Session, Mixed(), Start, filter: "maxed", search: "prom");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.Empty);
            Assert.That(page, Does.Contain("<p id=\"skylabEmpty\">No collectors match this search.</p>"));
            Assert.That(page, Does.Contain("0 of 1 collectors match"));
        });
    }

    [Test]
    public void Search_DoesNotChangeSummaryStatusOrCollectForm()
    {
        var full = LocalSkylab.Page(Session, Mixed(), Start.AddMinutes(5));
        var searched = LocalSkylab.Page(Session, Mixed(), Start.AddMinutes(5), search: "terb");
        string Part(string page, string id) => Regex.Match(page, $"<p id=\"{id}\">[^<]*</p>").Value;
        Assert.Multiple(() =>
        {
            Assert.That(Part(searched, "skylabSummary"), Is.EqualTo(Part(full, "skylabSummary")).And.Not.Empty);
            Assert.That(Part(searched, "skylabStatus"), Is.EqualTo(Part(full, "skylabStatus")).And.Not.Empty);
            Assert.That(searched, Does.Contain("action=\"/indexInternal.es?action=internalSkylabCollect\""));
            Assert.That(Rows(searched), Is.EqualTo(new[] { "terbium" }));
        });
    }

    [Test]
    public void NoSearch_HasFormButNoSummary()
    {
        var page = LocalSkylab.Page(Session, Mixed(), Start, search: "   ");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"skylabSearch\""));
            Assert.That(page, Does.Not.Contain("id=\"skylabSearchSummary\""));
            Assert.That(page, Does.Not.Contain("&amp;q="));
            Assert.That(Rows(page), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public void Detail_IgnoresSearch()
    {
        var page = LocalSkylab.Page(Session, Mixed(), Start, "terbium", search: "prom");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<section id=\"skylabDetail\" data-collector=\"terbium\">"));
            Assert.That(Rows(page), Has.Length.EqualTo(3));
            Assert.That(page, Does.Not.Contain("id=\"skylabSearchSummary\""));
        });
    }

    [TestCase("?action=internalSkylab&q=p")]
    [TestCase("?action=internalSkylab&q=prom&detail=prometium")]
    [TestCase("?action=internalSkylab&q=prom&q=terb")]
    [TestCase("?action=internalSkylab&Q=prom")]
    public async Task Endpoint_InvalidSearch_Returns400BeforeSession(string query)
    {
        var harness = await HarnessAsync();
        foreach (var dosid in new[] { null, harness.Session.Dosid })
        {
            var context = await SendAsync(query, dosid, harness);
            Assert.Multiple(() =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
                Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            });
        }
    }

    [Test]
    public async Task Endpoint_ValidSearchWithoutSession_Returns401()
    {
        var harness = await HarnessAsync();
        var context = await SendAsync("?action=internalSkylab&q=prom", null, harness);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
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

        // Only the query check and session lookup are reached on these paths; the 200 page needs Postgres balances.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, null!, null!,
            null!, harness.Skylab, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }
}
