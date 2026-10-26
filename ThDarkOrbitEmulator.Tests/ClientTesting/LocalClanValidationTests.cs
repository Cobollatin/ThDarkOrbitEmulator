using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalClanValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task Action_NonPostReturns405WithAllowHeader(string method)
    {
        var (sessions, clans, session) = await ReadyAsync();
        var request = Request(session.Dosid, "?action=internalClanJoin", Form(session.Dosid, "join", "mmo-guard"));
        request.Request.Method = method;
        await LocalClanAction.HandleAsync(request, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(request.Response.StatusCode, Is.EqualTo(405));
            Assert.That(request.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
            Assert.That(request.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        await AssertNoMembershipAsync(clans, session);
    }

    [TestCase("?action=internalclanjoin")]
    [TestCase("?action=INTERNALCLANJOIN")]
    [TestCase("?action=internalClanJoin&action=internalClanJoin")]
    [TestCase("?action=internalClanJoin&from=internalStart")]
    [TestCase("?action=internalClanJoin&clan=mmo-guard")]
    [TestCase("")]
    public async Task Action_StrictQueryReturns400(string query)
    {
        var (sessions, clans, session) = await ReadyAsync();
        var request = Request(session.Dosid, query, Form(session.Dosid, "join", "mmo-guard"));
        await LocalClanAction.HandleAsync(request, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(request), Does.Contain("Invalid Clan action."));
        await AssertNoMembershipAsync(clans, session);
    }

    [Test]
    public async Task Action_UnreadableOrNonFormBodiesReturn400()
    {
        var (sessions, clans, session) = await ReadyAsync();
        var broken = Request(session.Dosid, "?action=internalClanJoin",
            "--x\r\nContent-Disposition: form-data; name=\"clan\"\r\n\r\nmmo-gu");
        broken.Request.ContentType = "multipart/form-data; boundary=x";
        await LocalClanAction.HandleAsync(broken, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(broken.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(broken), Does.Contain("could not be read"));

        var json = Request(session.Dosid, "?action=internalClanJoin", "{\"clan\":\"mmo-guard\"}");
        json.Request.ContentType = "application/json";
        await LocalClanAction.HandleAsync(json, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(json), Does.Contain("Submit a Clan form."));
        await AssertNoMembershipAsync(clans, session);
    }

    [TestCase("MMO-GUARD")]
    [TestCase("mmo--guard")]
    [TestCase("-mmo-guard")]
    [TestCase("mmo-guard-")]
    [TestCase("mmo_guard")]
    [TestCase("mmo guard")]
    [TestCase("mmo-guárd")]
    [TestCase("<b>x</b>")]
    public async Task Action_MalformedClanIdsReturn400EvenWithMatchingKey(string clanId)
    {
        var (sessions, clans, session) = await ReadyAsync();
        var request = Request(session.Dosid, "?action=internalClanJoin",
            $"clan={Uri.EscapeDataString(clanId)}&key={LocalClanAction.CreateFormKey(session.Dosid, "join", clanId, Now)}");
        await LocalClanAction.HandleAsync(request, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(request.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(request), Does.Not.Contain(clanId));
            Assert.That(Body(request), Does.Contain("The form is invalid or has been changed."));
        });
        await AssertNoMembershipAsync(clans, session);
    }

    [Test]
    public async Task Action_OverlongClanIdReturns400()
    {
        var (sessions, clans, session) = await ReadyAsync();
        var clanId = new string('a', 65);
        var request = Request(session.Dosid, "?action=internalClanJoin",
            $"clan={clanId}&key={LocalClanAction.CreateFormKey(session.Dosid, "join", clanId, Now)}");
        await LocalClanAction.HandleAsync(request, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public void IsWellFormedClanId_AcceptsEveryDirectoryId()
    {
        Assert.That(LocalClanStore.Directory.All(entry => LocalClanAction.IsWellFormedClanId(entry.Id)), Is.True);
        Assert.That(LocalClanAction.IsWellFormedClanId(""), Is.False);
        Assert.That(LocalClanAction.IsWellFormedClanId(new string('a', 64)), Is.True);
    }

    [Test]
    public async Task Action_KeyLifetimeBoundaries()
    {
        var (sessions, clans, session) = await ReadyAsync();
        var future = Request(session.Dosid, "?action=internalClanJoin",
            $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", Now + TimeSpan.FromMinutes(6))}");
        await LocalClanAction.HandleAsync(future, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(future.Response.StatusCode, Is.EqualTo(400));

        var expired = Request(session.Dosid, "?action=internalClanJoin",
            $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", Now - LocalClanAction.FormLifetime - TimeSpan.FromSeconds(1))}");
        await LocalClanAction.HandleAsync(expired, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(409));
        Assert.That(Body(expired), Does.Contain("expired"));
        await AssertNoMembershipAsync(clans, session);

        var edge = Request(session.Dosid, "?action=internalClanJoin",
            $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", Now - LocalClanAction.FormLifetime)}");
        await LocalClanAction.HandleAsync(edge, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(edge.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await clans.GetMembershipAsync(session.AccountId, CancellationToken.None), Is.EqualTo("mmo-guard"));
    }

    [Test]
    public async Task Action_JoinKeyCannotBeUsedToLeaveAndReplayIsRejected()
    {
        var (sessions, clans, session) = await ReadyAsync();
        var form = Form(session.Dosid, "join", "mmo-guard");
        var join = Request(session.Dosid, "?action=internalClanJoin", form);
        await LocalClanAction.HandleAsync(join, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(join.Response.StatusCode, Is.EqualTo(303));

        var wrongAction = Request(session.Dosid, "?action=internalClanLeave", form);
        await LocalClanAction.HandleAsync(wrongAction, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(wrongAction.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await clans.GetMembershipAsync(session.AccountId, CancellationToken.None), Is.EqualTo("mmo-guard"));

        var leaveForm = Form(session.Dosid, "leave", "mmo-guard");
        var leave = Request(session.Dosid, "?action=internalClanLeave", leaveForm);
        await LocalClanAction.HandleAsync(leave, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
        var replay = Request(session.Dosid, "?action=internalClanJoin", form);
        await LocalClanAction.HandleAsync(replay, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        await AssertNoMembershipAsync(clans, session);
    }

    [Test]
    public async Task Action_JoiningClosedClanReturns409()
    {
        var (sessions, clans, session) = await ReadyAsync();
        var request = Request(session.Dosid, "?action=internalClanJoin", Form(session.Dosid, "join", "mmo-elite"));
        await LocalClanAction.HandleAsync(request, sessions, clans, new FixedClock(Now), CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(409));
        Assert.That(Body(request), Does.Contain("not recruiting"));
        await AssertNoMembershipAsync(clans, session);
    }

    [TestCase("?action=internalClan", true)]
    [TestCase("?action=internalClan&from=internalStart", true)]
    [TestCase("?action=internalClan&from=internalStart&prc=100&lang=es", true)]
    [TestCase("?action=internalClan&LANG=es", false)]
    [TestCase("?action=internalClan&clan=mmo-guard", false)]
    [TestCase("?action=internalClan&from=a&from=b", false)]
    [TestCase("?action=internalClan&action=internalClan", false)]
    [TestCase("?from=internalStart", false)]
    [TestCase("?action=internalClan&lang=%0A", false)]
    public void PageQuery_AcceptsOnlyNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
        Assert.That(LocalClanAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValue()
    {
        var parsed = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            $"?action=internalClan&from={new string('a', 65)}"));
        Assert.That(LocalClanAction.ValidPageQuery(parsed), Is.False);
    }

    [TestCase("mmo", "3 of 4 clans recruiting")]
    [TestCase("tst", "1 of 1 clans recruiting")]
    public void Page_ShowsRecruitingSummary(string company, string expected)
    {
        var page = LocalClan.Page(new GameAuthSession { Dosid = "d", Username = "pilot", Company = company }, null, Now);
        Assert.That(page, Does.Contain($"<p id=\"clan-summary\">{expected}</p>"));
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalClanStore Clans, GameAuthSession Session)> ReadyAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalClanStore(cache), session);
    }

    private static async Task AssertNoMembershipAsync(LocalClanStore clans, GameAuthSession session) =>
        Assert.That(await clans.GetMembershipAsync(session.AccountId, CancellationToken.None), Is.Null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DefaultHttpContext Request(string dosid, string query, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static string Form(string dosid, string action, string clan) =>
        $"clan={clan}&key={LocalClanAction.CreateFormKey(dosid, action, clan, Now)}";
}
