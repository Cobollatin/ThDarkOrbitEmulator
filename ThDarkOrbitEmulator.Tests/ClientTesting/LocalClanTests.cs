using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalClanTests
{
    [Test]
    public async Task Membership_IsAccountScopedAndRestrictedToCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalClanStore(services.GetRequiredService<IDistributedCache>());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.That(await store.JoinAsync(first, "mmo", "eic-vanguard", CancellationToken.None), Is.False);
        Assert.That(await store.JoinAsync(first, "mmo", "mmo-guard", CancellationToken.None), Is.True);
        Assert.That(await store.JoinAsync(first, "mmo", "mmo-orbit", CancellationToken.None), Is.False);
        Assert.That(await store.GetMembershipAsync(second, CancellationToken.None), Is.Null);
        Assert.That(await store.GetMembershipAsync(first, CancellationToken.None), Is.EqualTo("mmo-guard"));
        Assert.That(await store.LeaveAsync(first, CancellationToken.None), Is.True);
        Assert.That(await store.LeaveAsync(first, CancellationToken.None), Is.False);
        Assert.That(await store.JoinAsync(first, "mmo", "mmo-orbit", CancellationToken.None), Is.True);
    }

    [Test]
    public async Task Actions_RequireSessionCompanyAndValidMembershipChange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clans = new LocalClanStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalClanJoin", Form(session.Dosid, "join", "mmo-guard"));
        await LocalClanAction.HandleAsync(anonymous, sessions, clans, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalClanJoin", Form(session.Dosid, "join", "mmo-guard"));
        await LocalClanAction.HandleAsync(noCompany, sessions, clans, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var otherCompany = Request(session.Dosid, "internalClanJoin", Form(session.Dosid, "join", "eic-vanguard"));
        await LocalClanAction.HandleAsync(otherCompany, sessions, clans, CancellationToken.None);
        Assert.That(otherCompany.Response.StatusCode, Is.EqualTo(403));
        var join = Request(session.Dosid, "internalClanJoin", Form(session.Dosid, "join", "mmo-guard"));
        await LocalClanAction.HandleAsync(join, sessions, clans, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(join.Response.StatusCode, Is.EqualTo(303));
            Assert.That(join.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalClan&joined=mmo-guard"));
        });
        var leave = Request(session.Dosid, "internalClanLeave", Form(session.Dosid, "leave", "mmo-guard"));
        await LocalClanAction.HandleAsync(leave, sessions, clans, CancellationToken.None);
        Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await clans.GetMembershipAsync(session.AccountId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Posts_RejectMalformedForeignAndReplayForms()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clans = new LocalClanStore(cache);
        var first = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "first", null, 7), "mmo", CancellationToken.None)).Session;
        var second = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "second", null, 8), "mmo", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(first.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await sessions.SelectCompanyAsync(second.Dosid, "mmo", CancellationToken.None), Is.True);
        var body = Form(first.Dosid, "join", "mmo-guard");
        foreach (var (dosid, form, expected) in new (string?, string, int)[]
        {
            (null, body, 401), (second.Dosid, body, 400),
            (first.Dosid, "clan=mmo-guard", 400),
            (first.Dosid, body + "&clan=mmo-orbit", 400),
            (first.Dosid, body + "&quantity=-1", 400),
            (first.Dosid, body + "&quantity=999999999999", 400),
            (first.Dosid, body.Replace("mmo-guard", "mmo-orbit"), 400)
        })
        {
            var request = Request(dosid, "internalClanJoin", form);
            await LocalClanAction.HandleAsync(request, sessions, clans, CancellationToken.None);
            Assert.That(request.Response.StatusCode, Is.EqualTo(expected), form);
            Assert.That(request.Response.ContentType, Does.StartWith("text/html"));
        }
        var join = Request(first.Dosid, "internalClanJoin", body);
        await LocalClanAction.HandleAsync(join, sessions, clans, CancellationToken.None);
        Assert.That(join.Response.StatusCode, Is.EqualTo(303));
        var replay = Request(first.Dosid, "internalClanJoin", body);
        await LocalClanAction.HandleAsync(replay, sessions, clans, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        var staleLeave = Request(first.Dosid, "internalClanLeave", Form(first.Dosid, "leave", "mmo-orbit"));
        await LocalClanAction.HandleAsync(staleLeave, sessions, clans, CancellationToken.None);
        Assert.That(staleLeave.Response.StatusCode, Is.EqualTo(409));
        var leaveBody = Form(first.Dosid, "leave", "mmo-guard");
        var leave = Request(first.Dosid, "internalClanLeave", leaveBody);
        await LocalClanAction.HandleAsync(leave, sessions, clans, CancellationToken.None);
        Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
        var leaveReplay = Request(first.Dosid, "internalClanLeave", leaveBody);
        await LocalClanAction.HandleAsync(leaveReplay, sessions, clans, CancellationToken.None);
        Assert.That(leaveReplay.Response.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task ConcurrentChanges_AcrossStoreInstancesAllowOneWinner()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = new LocalClanStore(cache);
        var second = new LocalClanStore(cache);
        var account = Guid.NewGuid();
        var changes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? first : second).ChangeAsync(account, "mmo", "mmo-guard", null,
                $"key-{i}", CancellationToken.None)));
        Assert.That(changes.Count(result => result == ThDarkOrbitEmulator.ApiService.Features.Economy.ClanChange.Applied), Is.EqualTo(1));
        Assert.That(await first.GetMembershipAsync(account, CancellationToken.None), Is.EqualTo("mmo-guard"));
    }

    [Test]
    public void Page_ShowsCompanyDirectoryAndEscapesPilotName()
    {
        var page = LocalClan.Page(new GameAuthSession { Username = "<pilot>", Company = "mmo" }, null);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"clan\""));
            Assert.That(page, Does.Contain("action=internalClanJoin"));
            Assert.That(page, Does.Contain("Mars Guard"));
            Assert.That(page, Does.Not.Contain("Earth Vanguard"));
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalClan"));
        });
    }

    [Test]
    public async Task Posts_RejectExpiredFutureAndOversizedFormsAndClosedClans()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clans = new LocalClanStore(cache);
        var session = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "pilot", null, 7), "mmo", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var expired = $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", clock.Now - LocalClanAction.FormLifetime - TimeSpan.FromSeconds(1))}";
        var future = $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", clock.Now + TimeSpan.FromHours(1))}";
        var legacyKey = "clan=mmo-guard&key=" + Guid.NewGuid().ToString("N") + "." + new string('a', 64);
        foreach (var (form, expected) in new (string, int)[]
        {
            (expired, 409), (future, 400), (legacyKey, 400),
            ("clan=" + new string('x', 65) + "&key=k", 400),
            ("clan=mmo-guard&key=" + new string('a', 161), 400),
            ("clan=&key=k", 400),
            (Form(session.Dosid, "join", "mmo-elite"), 409),
            (Form(session.Dosid, "join", "missing-clan"), 404)
        })
        {
            var request = Request(session.Dosid, "internalClanJoin", form);
            await LocalClanAction.HandleAsync(request, sessions, clans, clock, CancellationToken.None);
            Assert.That(request.Response.StatusCode, Is.EqualTo(expected), form);
        }
        Assert.That(await clans.GetMembershipAsync(session.AccountId, CancellationToken.None), Is.Null);
        Assert.That(await clans.JoinAsync(session.AccountId, "mmo", "mmo-elite", CancellationToken.None), Is.False);
        var fresh = Request(session.Dosid, "internalClanJoin",
            $"clan=mmo-guard&key={LocalClanAction.CreateFormKey(session.Dosid, "join", "mmo-guard", clock.Now - TimeSpan.FromMinutes(59))}");
        await LocalClanAction.HandleAsync(fresh, sessions, clans, clock, CancellationToken.None);
        Assert.That(fresh.Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task Leave_AllowsOwnMembershipFromPreviousCompanyButNotForeignJoin()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var clans = new LocalClanStore(cache);
        var account = Guid.NewGuid();
        var session = (await sessions.CreateSessionAsync(new AccountSummary(account, "pilot", null, 7), "eic", CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "eic", CancellationToken.None), Is.True);
        Assert.That(await clans.JoinAsync(account, "mmo", "mmo-guard", CancellationToken.None), Is.True);
        var page = LocalClan.Page(new GameAuthSession { Dosid = session.Dosid, Username = "pilot", Company = "eic" }, "mmo-guard");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("is not available for your company"));
            Assert.That(page, Does.Contain("action=internalClanLeave"));
        });
        var otherLeave = Request(session.Dosid, "internalClanLeave", Form(session.Dosid, "leave", "mmo-orbit"));
        await LocalClanAction.HandleAsync(otherLeave, sessions, clans, CancellationToken.None);
        Assert.That(otherLeave.Response.StatusCode, Is.EqualTo(403));
        var foreignJoin = Request(session.Dosid, "internalClanJoin", Form(session.Dosid, "join", "mmo-orbit"));
        await LocalClanAction.HandleAsync(foreignJoin, sessions, clans, CancellationToken.None);
        Assert.That(foreignJoin.Response.StatusCode, Is.EqualTo(403));
        var leave = Request(session.Dosid, "internalClanLeave", Form(session.Dosid, "leave", "mmo-guard"));
        await LocalClanAction.HandleAsync(leave, sessions, clans, CancellationToken.None);
        Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await clans.GetMembershipAsync(account, CancellationToken.None), Is.Null);
        var join = Request(session.Dosid, "internalClanJoin", Form(session.Dosid, "join", "eic-orbit"));
        await LocalClanAction.HandleAsync(join, sessions, clans, CancellationToken.None);
        Assert.That(join.Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public void Page_ShowsMemberCountsAndClosedClansWithoutJoinForm()
    {
        var session = new GameAuthSession { Dosid = "d", Username = "pilot", Company = "mmo" };
        var empty = LocalClan.Page(session, null);
        var joined = LocalClan.Page(session, "mmo-guard");
        Assert.Multiple(() =>
        {
            Assert.That(empty, Does.Contain("<td>42</td>"));
            Assert.That(joined, Does.Contain("<td>43</td>"));
            Assert.That(empty, Does.Contain("Mars Elite"));
            Assert.That(empty, Does.Contain("Not recruiting"));
            Assert.That(empty, Does.Not.Contain("value=\"mmo-elite\""));
            Assert.That(LocalClanStore.Directory.Where(c => c.Company is "mmo" or "eic" or "vru")
                .GroupBy(c => c.Company).All(g => g.Count() == 4), Is.True);
        });
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
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

    private static string Form(string dosid, string action, string clan) =>
        $"clan={clan}&key={LocalClanAction.CreateFormKey(dosid, action, clan)}";
}
