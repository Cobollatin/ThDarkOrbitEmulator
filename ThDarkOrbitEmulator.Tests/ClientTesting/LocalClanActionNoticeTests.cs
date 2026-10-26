using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-126: Clan join/leave confirmations. Emulator-defined; no Live Clan form was captured.
public class LocalClanActionNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalClanStore _clans = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _clans = new LocalClanStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
        _session = (await _sessions.TryGetByDosidAsync(_session.Dosid, CancellationToken.None))!;
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalClan&joined=mmo-guard", true)]
    [TestCase("?action=internalClan&left=mmo-guard", true)]
    [TestCase("?action=internalClan&left=retired-clan", true)]
    [TestCase("?action=internalClan&joined=mmo-guard&detail=mmo-guard&filter=recruiting", true)]
    [TestCase("?action=internalClan&joined=mmo-guard&left=mmo-orbit", false)]
    [TestCase("?action=internalClan&joined=mmo-guard&joined=mmo-orbit", false)]
    [TestCase("?action=internalClan&left=a&left=b", false)]
    [TestCase("?action=internalClan&Joined=mmo-guard", false)]
    [TestCase("?action=internalClan&LEFT=mmo-guard", false)]
    [TestCase("?action=internalClan&joined=MMO-GUARD", false)]
    [TestCase("?action=internalClan&joined=", false)]
    [TestCase("?action=internalClan&left=-guard", false)]
    [TestCase("?action=internalClan&left=mmo--guard", false)]
    [TestCase("?action=internalClan&joined=%3Cscript%3E", false)]
    [TestCase("?action=internalClan&left=mmo%0Aguard", false)]
    public void PageQuery_ValidatesNoticeParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalClanAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongNotice()
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery($"?action=internalClan&joined={new string('a', 65)}"));
        Assert.That(LocalClanAction.ValidPageQuery(parsed), Is.False);
    }

    [Test]
    public void SuccessLocation_NamesActionAndClan()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalClanAction.SuccessLocation(true, "mmo-guard"),
                Is.EqualTo("/indexInternal.es?action=internalClan&joined=mmo-guard"));
            Assert.That(LocalClanAction.SuccessLocation(false, "mmo-orbit"),
                Is.EqualTo("/indexInternal.es?action=internalClan&left=mmo-orbit"));
        });
    }

    [Test]
    public void JoinedNotice_ShowsOnlyForCurrentMembership()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalClan.Notice("mmo", "mmo-guard", "mmo-guard", null),
                Does.Contain("You joined [MG] Mars Guard. Members: 43.").And.Contain("data-notice=\"joined\""));
            Assert.That(LocalClan.Notice("mmo", null, "mmo-guard", null), Is.Empty);
            Assert.That(LocalClan.Notice("mmo", "mmo-orbit", "mmo-guard", null), Is.Empty);
            Assert.That(LocalClan.Notice("eic", "mmo-guard", "mmo-guard", null), Is.Empty);
            Assert.That(LocalClan.Notice("mmo", "retired-clan", "retired-clan", null), Is.Empty);
            Assert.That(LocalClan.Notice("mmo", null, null, null), Is.Empty);
        });
    }

    [Test]
    public void LeftNotice_ShowsOnlyOnceMembershipChanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalClan.Notice("mmo", null, null, "mmo-guard"),
                Is.EqualTo("<p id=\"clan-notice\" role=\"status\" data-notice=\"left\">You left [MG] Mars Guard. You can join a recruiting clan now.</p>"));
            Assert.That(LocalClan.Notice("mmo", "mmo-orbit", null, "mmo-guard"),
                Is.EqualTo("<p id=\"clan-notice\" role=\"status\" data-notice=\"left\">You left [MG] Mars Guard.</p>"));
            Assert.That(LocalClan.Notice("mmo", null, null, "retired-clan"), Does.Contain("You left your previous clan."));
            Assert.That(LocalClan.Notice("mmo", "mmo-guard", null, "mmo-guard"), Is.Empty);
            Assert.That(LocalClan.Notice("mmo", null, null, "<b>"), Is.Empty);
        });
    }

    [Test]
    public void Page_RendersNoticeBeforeSummary_AndOmitsItWithoutParameters()
    {
        var withNotice = LocalClan.Page(_session, "mmo-guard", Now, joined: "mmo-guard");
        var plain = LocalClan.Page(_session, "mmo-guard", Now);
        Assert.Multiple(() =>
        {
            Assert.That(withNotice.IndexOf("id=\"clan-notice\"", StringComparison.Ordinal),
                Is.LessThan(withNotice.IndexOf("id=\"clan-summary\"", StringComparison.Ordinal)).And.GreaterThan(0));
            Assert.That(plain, Does.Not.Contain("clan-notice"));
        });
    }

    [Test]
    public async Task JoinThenLeave_RedirectsToMatchingNotices()
    {
        var join = Build("internalClanJoin", Body("join"));
        await Handle(join);
        var joinLocation = join.Response.Headers.Location.ToString();
        var afterJoin = await PageForAsync(joinLocation);
        Assert.That(afterJoin, Does.Contain("You joined [MG] Mars Guard. Members: 43."));

        var leave = Build("internalClanLeave", Body("leave"));
        await Handle(leave);
        var leaveLocation = leave.Response.Headers.Location.ToString();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(join.Response.StatusCode, Is.EqualTo(303));
            Assert.That(leave.Response.StatusCode, Is.EqualTo(303));
            Assert.That(leaveLocation, Is.EqualTo("/indexInternal.es?action=internalClan&left=mmo-guard"));
            Assert.That(await PageForAsync(leaveLocation), Does.Contain("You left [MG] Mars Guard. You can join a recruiting clan now."));
            // The old join link no longer shows a confirmation once membership changed.
            Assert.That(await PageForAsync(joinLocation), Does.Not.Contain("clan-notice"));
        });
    }

    [Test]
    public async Task FailedJoin_DoesNotRedirect()
    {
        var closed = Build("internalClanJoin",
            $"clan=mmo-elite&key={Uri.EscapeDataString(LocalClanAction.CreateFormKey(_session.Dosid, "join", "mmo-elite", Now))}");
        await Handle(closed);
        Assert.Multiple(() =>
        {
            Assert.That(closed.Response.StatusCode, Is.EqualTo(409));
            Assert.That(closed.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    private async Task<string> PageForAsync(string location)
    {
        var query = QueryHelpers.ParseQuery(new Uri("http://local" + location).Query);
        Assert.That(LocalClanAction.ValidPageQuery(new QueryCollection(query)), Is.True, location);
        return LocalClan.Page(_session, await _clans.GetMembershipAsync(_session.AccountId, CancellationToken.None), Now,
            joined: query.TryGetValue("joined", out var joined) ? joined.ToString() : null,
            left: query.TryGetValue("left", out var left) ? left.ToString() : null);
    }

    private string Body(string kind) =>
        $"clan=mmo-guard&key={Uri.EscapeDataString(LocalClanAction.CreateFormKey(_session.Dosid, kind, "mmo-guard", Now))}";

    private Task Handle(HttpContext context) =>
        LocalClanAction.HandleAsync(context, _sessions, _clans, new FixedClock(Now), CancellationToken.None);

    private DefaultHttpContext Build(string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={_session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
