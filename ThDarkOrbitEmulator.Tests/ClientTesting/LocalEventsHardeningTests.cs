using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsHardeningTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("internalBattlePassClaim", "tier")]
    [TestCase("internalExpeditionStart", "start")]
    [TestCase("internalExpeditionClaim", "claim")]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing(string action, string field)
    {
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var before = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        var context = Build(action, $"--b\r\nContent-Disposition: form-data; name=\"{field}\"\r\n\r\nsupp");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        var after = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(after.Credits, Is.EqualTo(before.Credits));
        Assert.That(after.ClaimedBattlePassTiers, Is.Empty);
        Assert.That(after.ExpeditionReadyAt, Is.Null);
    }

    [TestCase("internalBattlePassClaim")]
    [TestCase("internalExpeditionStart")]
    [TestCase("internalExpeditionClaim")]
    public async Task WrongMethod_Returns405WithAllowHeader_BeforeSessionLookup(string action)
    {
        var context = Build(action, "", "missing-session");
        context.Request.Method = "PUT";
        await LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [TestCase("?action=internalBattlePassClaim&from=internalStart")]
    [TestCase("?action=internalBattlePassClaim&tier=supplier")]
    public async Task ActionQuery_MustBeExactlyOneAction(string query)
    {
        await _store.PurchaseAsync(_session.AccountId, "laser_lf3", 1, CancellationToken.None);
        var context = Build("internalBattlePassClaim", "tier=supplier");
        context.Request.QueryString = new QueryString(query);
        await LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).ClaimedBattlePassTiers, Is.Empty);
    }

    [TestCase("?action=InternalBattlePassClaim")]
    [TestCase("?action=internalbattlepassclaim")]
    // A repeated action reads as "a,a", which names no Events action.
    [TestCase("?action=internalBattlePassClaim&action=internalBattlePassClaim")]
    public async Task ActionQuery_WrongCaseOrRepeated_Returns404(string query)
    {
        var context = Build("internalBattlePassClaim", "tier=supplier");
        context.Request.QueryString = new QueryString(query);
        await LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task ErrorPages_DoNotEchoTierInput()
    {
        var context = Build("internalBattlePassClaim", "tier=%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        await LocalEventsAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var body = await Body(context);
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(body, Does.Not.Contain("alert"));
    }

    [TestCase("?action=internalEvents", true)]
    [TestCase("?action=internalEvents&from=internalStart", true)]
    [TestCase("?action=internalEvents&from=internalStart&prc=100&lang=es", true)]
    [TestCase("?action=internalEvents&LANG=es", false)]
    [TestCase("?action=internalEvents&tier=supplier", false)]
    [TestCase("?action=internalEvents&from=a&from=b", false)]
    [TestCase("?action=internalEvents&action=internalEvents", false)]
    [TestCase("?from=internalStart", false)]
    [TestCase("?action=internalEvents&lang=%0A", false)]
    [TestCase("?action=internalEvents&prc=%00", false)]
    public void PageQuery_AcceptsOnlyNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalEventsAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValue()
    {
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalEvents&from=" + new string('a', 64)))), Is.True);
        Assert.That(LocalEventsAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalEvents&from=" + new string('a', 65)))), Is.False);
    }

    [Test]
    public void Page_ShowsRemainingProgress_AndClaimedSummary()
    {
        var state = new LocalEquipmentState([], []) { Credits = 10000, PurchasedItems = 2, CraftedItems = 0 };
        state.ClaimedBattlePassTiers.Add("supplier");
        var page = LocalEvents.Page(state, DateTimeOffset.UtcNow);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"1 of {LocalEvents.Tiers.Count} tiers claimed"));
            Assert.That(page, Does.Contain("In progress (1 more needed)"), "quartermaster: 2/3");
            Assert.That(page, Does.Contain("In progress (8 more needed)"), "tycoon: 2/10");
            Assert.That(page, Does.Contain("In progress (3 more needed)"), "artisan: 0/3");
            Assert.That(page, Does.Not.Contain("In progress (0 more needed)"));
            Assert.That(page, Does.Not.Contain("value=\"supplier\""), "claimed tier has no form");
        });
    }

    [Test]
    public void Page_ShowsExpeditionMinutesLeft_RoundedUp()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var state = new LocalEquipmentState([new LocalEquipmentItem(LocalEvents.ExpeditionRewardItemId,
            "SG3N-B02 shield", "Equipment", 4, "shield")], [])
            { Credits = 9900, ExpeditionReadyAt = now.AddSeconds(61) };
        Assert.That(LocalEvents.Page(state, now), Does.Contain("(2 min left)"));
        Assert.That(LocalEvents.MinutesLeft(now.AddSeconds(1), now), Is.EqualTo(1));
        Assert.That(LocalEvents.MinutesLeft(now.AddMinutes(2), now), Is.EqualTo(2));
        Assert.That(LocalEvents.Page(state, now.AddSeconds(61)), Does.Contain("Claim shield"));
    }

    private DefaultHttpContext Build(string action, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
