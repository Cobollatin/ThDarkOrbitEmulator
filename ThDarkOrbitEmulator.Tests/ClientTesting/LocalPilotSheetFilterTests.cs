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

// Menu-147: emulator-defined Pilot Sheet skill filters (all, trainable, trained, maxed, locked).
public class LocalPilotSheetFilterTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" };

    private static LocalPilotSkillState State(params (string Skill, int Rank)[] ranks) =>
        new(ranks.ToDictionary(r => r.Skill, r => r.Rank), 1);

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private static string[] Rows(string page) =>
        Regex.Matches(page, "<tr data-skill=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

    [TestCase("?action=internalPilotSheet&filter=all", true)]
    [TestCase("?action=internalPilotSheet&filter=trainable", true)]
    [TestCase("?action=internalPilotSheet&filter=locked&detail=evasion", true)]
    [TestCase("?action=internalPilotSheet&filter=unknown", true)]
    [TestCase("?action=internalPilotSheet&filter=trained&trained=hull", true)]
    [TestCase("?action=internalPilotSheet&filter=", false)]
    [TestCase("?action=internalPilotSheet&filter", false)]
    [TestCase("?action=internalPilotSheet&filter=all&filter=all", false)]
    [TestCase("?action=internalPilotSheet&Filter=all", false)]
    [TestCase("?action=internalPilotSheet&FILTER=maxed", false)]
    [TestCase("?action=internalPilotSheet&filter=Maxed", false)]
    [TestCase("?action=internalPilotSheet&filter=no-points", false)]
    [TestCase("?action=internalPilotSheet&filter=max3d", false)]
    [TestCase("?action=internalPilotSheet&filter=%3Cscript%3E", false)]
    [TestCase("?action=internalPilotSheet&filter=abcdefghijklmnopq", false)]
    public void PageQuery_FilterRules(string query, bool expected) =>
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query(query)), Is.EqualTo(expected), query);

    [TestCase("?action=internalPilotSheet", true, null)]
    [TestCase("?action=internalPilotSheet&filter=all", true, "all")]
    [TestCase("?action=internalPilotSheet&filter=locked", true, "locked")]
    [TestCase("?action=internalPilotSheet&filter=unknown", false, "unknown")]
    public void TryFindFilter_KnownNamesOnly(string query, bool found, string? filter)
    {
        var ok = LocalPilotSkillAction.TryFindFilter(Query(query), out var parsed);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.EqualTo(found));
            Assert.That(parsed, Is.EqualTo(filter));
        });
    }

    [Test]
    public void Matches_FollowsSavedRanks()
    {
        var state = State(("hull", 5), ("evasion", 1));
        string[] Ids(string filter) => LocalPilotSkillStore.Skills
            .Where(skill => LocalPilotSheet.Matches(skill, state, filter)).Select(skill => skill.Id).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Ids("all"), Has.Length.EqualTo(6));
            Assert.That(Ids("trainable"), Is.EqualTo(new[] { "shields", "lasers", "evasion" }));
            Assert.That(Ids("trained"), Is.EqualTo(new[] { "hull", "evasion" }));
            Assert.That(Ids("maxed"), Is.EqualTo(new[] { "hull" }));
            Assert.That(Ids("locked"), Is.EqualTo(new[] { "shieldEngineering", "rocketFusion" }));
        });
    }

    [Test]
    public void NoPointsLeft_NothingIsTrainable()
    {
        var page = LocalPilotSheet.Page(Session, State(("hull", 5), ("shields", 5)), filter: "trainable");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.Empty);
            Assert.That(page, Does.Contain("<p id=\"pilotSkillEmpty\" role=\"status\">No skills match the Can train filter.</p>"));
            Assert.That(page, Does.Not.Contain("<table>"));
            Assert.That(page, Does.Contain("data-filter=\"trainable\" data-count=\"0\" aria-current=\"page\""));
            // The reset form stays reachable even when the filter hides every row.
            Assert.That(page, Does.Contain("internalPilotSkillReset"));
        });
    }

    [Test]
    public void FilteredPage_ShowsCountedTabsAndOnlyMatchingRows()
    {
        var page = LocalPilotSheet.Page(Session, State(("hull", 3)), filter: "locked");
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.EqualTo(new[] { "shieldEngineering", "rocketFusion" }));
            Assert.That(page, Does.Contain("data-filter=\"all\" data-count=\"6\">All (6)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"trainable\" data-count=\"4\">Can train (4)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"trained\" data-count=\"1\">Trained (1)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"maxed\" data-count=\"0\">Maximum rank (0)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"locked\" data-count=\"2\" aria-current=\"page\">Locked (2)</a>"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalPilotSheet\" data-filter=\"all\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=rocketFusion&amp;filter=locked\""));
            Assert.That(page, Does.Not.Contain("pilotSkillEmpty"));
            // Active bonuses still list every trained skill regardless of the filter.
            Assert.That(page, Does.Contain("<li data-skill=\"hull\">"));
        });
    }

    [Test]
    public void DefaultPage_KeepsUnfilteredLinksAndMarksAll()
    {
        var page = LocalPilotSheet.Page(Session, State());
        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Has.Length.EqualTo(6));
            Assert.That(page, Does.Contain("data-filter=\"all\" data-count=\"6\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=hull\""));
            Assert.That(page, Does.Not.Contain("&amp;filter=all"));
        });
    }

    [Test]
    public void Detail_NavigatesWithinFilter_AndPrerequisiteLinksDropIt()
    {
        var state = State(("hull", 3));
        var first = LocalPilotSheet.Page(Session, state, detail: LocalPilotSkillStore.Find("shieldEngineering"), filter: "locked");
        var last = LocalPilotSheet.Page(Session, state, detail: LocalPilotSkillStore.Find("rocketFusion"), filter: "locked");
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Not.Contain("detailPreviousSkill"));
            Assert.That(first, Does.Contain("<a id=\"detailNextSkill\" rel=\"next\" href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=rocketFusion&amp;filter=locked\">"));
            Assert.That(first, Does.Contain("<a id=\"detailClose\" href=\"/indexInternal.es?action=internalPilotSheet&amp;filter=locked\">"));
            Assert.That(first, Does.Contain("<a href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=shields\">Shield Training</a> rank 3"));
            Assert.That(last, Does.Contain("<a id=\"detailPreviousSkill\" rel=\"prev\" href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=shieldEngineering&amp;filter=locked\">"));
            Assert.That(last, Does.Not.Contain("detailNextSkill"));
        });
    }

    [Test]
    public void Detail_Unfiltered_StepsThroughAllSkills()
    {
        var page = LocalPilotSheet.Page(Session, State(), detail: LocalPilotSkillStore.Find("lasers"));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("rel=\"prev\" href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=shields\""));
            Assert.That(page, Does.Contain("rel=\"next\" href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=evasion\""));
            Assert.That(page, Does.Contain("<a id=\"detailClose\" href=\"/indexInternal.es?action=internalPilotSheet\">"));
        });
    }

    // Endpoint order: malformed filter 400 before the session; unknown filter and out-of-filter detail 404 after it.
    [Test]
    public async Task Endpoint_MalformedFilter_Returns400BeforeSession()
    {
        var harness = await HarnessAsync();
        var context = await SendAsync("?action=internalPilotSheet&filter=Locked", null, harness);
        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Endpoint_UnknownFilter_Returns401WithoutSession_Then404()
    {
        var harness = await HarnessAsync();
        var anonymous = await SendAsync("?action=internalPilotSheet&filter=bogus", null, harness);
        var signedIn = await SendAsync("?action=internalPilotSheet&filter=bogus", harness.Session.Dosid, harness);
        AssertError(anonymous, StatusCodes.Status401Unauthorized);
        AssertError(signedIn, StatusCodes.Status404NotFound);
        Assert.That(Body(signedIn), Does.Not.Contain("bogus"));
    }

    [Test]
    public async Task Endpoint_DetailOutsideFilter_Returns404()
    {
        var harness = await HarnessAsync();
        // A fresh pilot has no maxed skills, so Hull is outside `maxed` even though it exists.
        var context = await SendAsync("?action=internalPilotSheet&filter=maxed&detail=hull", harness.Session.Dosid, harness);
        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Contain("not in the selected filter"));
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, LocalPilotSkillStore Skills);

    private static async Task<Harness> HarnessAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return new Harness(sessions, session, new LocalPilotSkillStore(cache));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid, Harness harness)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        // Only sessions and pilot skills are reached on these error paths; the 200 page needs Postgres balances.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, null!, null!,
            null!, null!, null!, null!, harness.Skills, null!, null!, TimeProvider.System);
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
