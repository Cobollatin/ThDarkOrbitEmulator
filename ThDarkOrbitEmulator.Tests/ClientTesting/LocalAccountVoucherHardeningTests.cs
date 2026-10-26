using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAccountVoucherHardeningTests
{
    private sealed record Harness(ServiceProvider Services, IDistributedCache Cache, GameAuthSessionStore Sessions,
        GameAuthSession Session) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private static async Task<Harness> CreateAsync(string? company = "mmo")
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        if (company is not null)
            Assert.That(await sessions.SelectCompanyAsync(session.Dosid, company, CancellationToken.None), Is.True);
        return new Harness(services, cache, sessions, session);
    }

    private static DefaultHttpContext Request(string? dosid, string body, string query,
        string contentType = "application/x-www-form-urlencoded")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = contentType;
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static async Task<int> SaveAsync(Harness h, LocalAccountPreferencesStore store, string body,
        string? dosid = "", string query = "?action=internalAccountSave")
    {
        var context = Request(dosid == "" ? h.Session.Dosid : dosid, body, query);
        await LocalAccountSettingsAction.HandleAsync(context, h.Sessions, store, CancellationToken.None);
        return context.Response.StatusCode;
    }

    private static async Task<int> RedeemAsync(Harness h, LocalHangarEquipmentStore store, string body,
        string? dosid = "", string query = "?action=internalVoucherRedeem")
    {
        var context = Request(dosid == "" ? h.Session.Dosid : dosid, body, query);
        await LocalVoucherAction.HandleAsync(context, h.Sessions, store, CancellationToken.None);
        return context.Response.StatusCode;
    }

    [TestCase("language=en&sound=false")]
    [TestCase("language=en&sound=false&notifications=true&extra=1")]
    [TestCase("language=en&language=de&sound=false&notifications=true")]
    [TestCase("language=en&sound=maybe&notifications=true")]
    [TestCase("language=EN&sound=false&notifications=true")]
    [TestCase("language=en&sound=false&notifications=true&rev=-1")]
    [TestCase("language=en&sound=false&notifications=true&rev=99999999")]
    [TestCase("language=en&sound=false&notifications=true&rev=1e3")]
    [TestCase("language=en&sound=false&notifications=true&rev=")]
    public async Task AccountSave_RejectsMalformedOrTamperedForms(string body)
    {
        using var h = await CreateAsync();
        var store = new LocalAccountPreferencesStore(h.Cache);
        Assert.That(await SaveAsync(h, store, body), Is.EqualTo(400));
        Assert.That(await store.GetStateAsync(h.Session.AccountId, CancellationToken.None),
            Is.EqualTo((LocalAccountPreferencesStore.Default, 0)));
    }

    [Test]
    public async Task AccountSave_ReturnsHtmlErrors_ForSessionCompanyQueryAndContentType()
    {
        using var h = await CreateAsync(company: null);
        var store = new LocalAccountPreferencesStore(h.Cache);
        const string body = "language=fr&sound=true&notifications=false&rev=0";
        var expired = Request("not-a-session", body, "?action=internalAccountSave");
        await LocalAccountSettingsAction.HandleAsync(expired, h.Sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
            Assert.That(Body(expired), Does.Contain("Account request failed (401)"));
        });
        Assert.That(await SaveAsync(h, store, body, dosid: null), Is.EqualTo(401));
        Assert.That(await SaveAsync(h, store, body), Is.EqualTo(403));
        Assert.That(await h.Sessions.SelectCompanyAsync(h.Session.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await SaveAsync(h, store, body, query: "?action=internalAccountSave&account=2"), Is.EqualTo(400));
        var json = Request(h.Session.Dosid, "{}", "?action=internalAccountSave", "application/json");
        await LocalAccountSettingsAction.HandleAsync(json, h.Sessions, store, CancellationToken.None);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await SaveAsync(h, store, body), Is.EqualTo(303));
        Assert.That(await store.GetStateAsync(h.Session.AccountId, CancellationToken.None),
            Is.EqualTo((new LocalAccountPreferences("fr", true, false), 1)));
    }

    [Test]
    public async Task AccountSave_ReplayOrStaleRevision_Returns409_AndKeepsFirstSave()
    {
        using var h = await CreateAsync();
        var store = new LocalAccountPreferencesStore(h.Cache);
        Assert.That(await SaveAsync(h, store, "language=de&sound=false&notifications=false&rev=0"), Is.EqualTo(303));
        Assert.That(await SaveAsync(h, store, "language=pl&sound=true&notifications=true&rev=0"), Is.EqualTo(409));
        Assert.That(await SaveAsync(h, store, "language=pl&sound=true&notifications=true&rev=5"), Is.EqualTo(409));
        Assert.That(await store.GetStateAsync(h.Session.AccountId, CancellationToken.None),
            Is.EqualTo((new LocalAccountPreferences("de", false, false), 1)));
        Assert.That(await SaveAsync(h, store, "language=pl&sound=true&notifications=true&rev=1"), Is.EqualTo(303));
        var page = LocalAccountSettings.Page(h.Session, new LocalAccountPreferences("pl", true, true), 2);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("name=\"rev\" value=\"2\""));
            Assert.That(page, Does.Contain("value=\"pl\" selected"));
            Assert.That(page, Does.Contain("id=\"accountNotice\""));
            Assert.That(page.Split("<option value=").Length - 1, Is.EqualTo(LocalAccountPreferencesStore.Languages.Count + 4));
        });
    }

    [Test]
    public async Task AccountSave_ConcurrentSameRevision_AppliesOnce_AndAccountsStayIsolated()
    {
        using var h = await CreateAsync();
        var store = new LocalAccountPreferencesStore(h.Cache);
        var otherStore = new LocalAccountPreferencesStore(h.Cache);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            (i % 2 == 0 ? store : otherStore).SaveAsync(h.Session.AccountId, i % 2 == 0 ? "en" : "it", "true", "true", 0,
                CancellationToken.None)));
        Assert.That(results.Count(result => result == AccountPreferencesChange.Applied), Is.EqualTo(1));
        Assert.That(results.Count(result => result == AccountPreferencesChange.Conflict), Is.EqualTo(7));
        Assert.That((await store.GetStateAsync(h.Session.AccountId, CancellationToken.None)).Revision, Is.EqualTo(1));
        Assert.That(await store.GetStateAsync(Guid.NewGuid(), CancellationToken.None),
            Is.EqualTo((LocalAccountPreferencesStore.Default, 0)));
    }

    [TestCase("not json")]
    [TestCase("{\"Language\":\"xx\",\"SoundEnabled\":false,\"NotificationsEnabled\":false,\"Revision\":-5}")]
    [TestCase("{\"Language\":\"en\",\"SoundEnabled\":false,\"NotificationsEnabled\":true}")]
    public async Task AccountPreferences_CorruptOrLegacyCache_FallsBackSafely(string json)
    {
        using var h = await CreateAsync();
        await h.Cache.SetStringAsync($"localaccountprefs:{h.Session.AccountId:N}", json);
        var store = new LocalAccountPreferencesStore(h.Cache);
        var (preferences, revision) = await store.GetStateAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(revision, Is.EqualTo(0));
            Assert.That(LocalAccountPreferencesStore.Languages.Select(item => item.Code), Does.Contain(preferences.Language));
        });
        Assert.That(await SaveAsync(h, store, "language=tr&sound=true&notifications=true&rev=0"), Is.EqualTo(303));
    }

    [Test]
    public async Task AccountSave_AfterCacheExpiry_StartsFromDefaults()
    {
        using var h = await CreateAsync();
        var store = new LocalAccountPreferencesStore(h.Cache);
        Assert.That(await SaveAsync(h, store, "language=pt&sound=false&notifications=false&rev=0"), Is.EqualTo(303));
        await h.Cache.RemoveAsync($"localaccountprefs:{h.Session.AccountId:N}");
        Assert.That(await store.GetStateAsync(h.Session.AccountId, CancellationToken.None),
            Is.EqualTo((LocalAccountPreferencesStore.Default, 0)));
        Assert.That(await SaveAsync(h, store, "language=pt&sound=false&notifications=false&rev=1"), Is.EqualTo(409));
    }

    [TestCase("code=")]
    [TestCase("code=TRAINING-500&code=HANGAR-750")]
    [TestCase("code=TRAINING-500&quantity=2")]
    [TestCase("code=TRAINING%3C500")]
    [TestCase("code=TRAINING-500-TRAINING-500-TRAINING-500")]
    [TestCase("voucher=TRAINING-500")]
    public async Task VoucherRedeem_RejectsMalformedOrTamperedForms(string body)
    {
        using var h = await CreateAsync();
        var store = new LocalHangarEquipmentStore(h.Cache);
        Assert.That(await RedeemAsync(h, store, body), Is.EqualTo(400));
        var state = await store.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(10000));
            Assert.That(state.RedeemedVoucherCodes, Is.Empty);
        });
    }

    [Test]
    public async Task VoucherRedeem_StatusCodes_ForUnknownReplayCompanyAndQuery()
    {
        using var h = await CreateAsync(company: null);
        var store = new LocalHangarEquipmentStore(h.Cache);
        Assert.That(await RedeemAsync(h, store, "code=TRAINING-500", dosid: "expired"), Is.EqualTo(401));
        Assert.That(await RedeemAsync(h, store, "code=TRAINING-500"), Is.EqualTo(403));
        Assert.That(await h.Sessions.SelectCompanyAsync(h.Session.Dosid, "eic", CancellationToken.None), Is.True);
        Assert.That(await RedeemAsync(h, store, "code=TRAINING-500",
            query: "?action=internalVoucherRedeem&accountId=1"), Is.EqualTo(400));
        Assert.That(await RedeemAsync(h, store, "code=UNKNOWN-1"), Is.EqualTo(404));
        Assert.That(await RedeemAsync(h, store, "code=%20training-500%20"), Is.EqualTo(303));
        var replay = Request(h.Session.Dosid, "code=TRAINING-500", "?action=internalVoucherRedeem");
        await LocalVoucherAction.HandleAsync(replay, h.Sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(replay), Does.Contain("already redeemed"));
        });
        Assert.That((await store.GetAsync(h.Session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10500));
    }

    [Test]
    public async Task VoucherRedeem_ConcurrentDoubleSubmit_GrantsOnce_AndShopSpendStaysNonNegative()
    {
        using var h = await CreateAsync();
        var first = new LocalHangarEquipmentStore(h.Cache);
        var second = new LocalHangarEquipmentStore(h.Cache);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? first : second).RedeemVoucherResultAsync(h.Session.AccountId, "HANGAR-750", CancellationToken.None)));
        Assert.That(results.Count(result => result == VoucherRedeemResult.Applied), Is.EqualTo(1));
        Assert.That(results.Count(result => result == VoucherRedeemResult.AlreadyRedeemed), Is.EqualTo(9));
        Assert.That((await first.GetAsync(h.Session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10750));
        for (var i = 0; i < 100 && await first.PurchaseAsync(h.Session.AccountId, "laser_lf3", 1, CancellationToken.None); i++) { }
        var state = await second.GetAsync(h.Session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.GreaterThanOrEqualTo(0));
        Assert.That(await second.RedeemVoucherResultAsync(h.Session.AccountId, "HANGAR-750", CancellationToken.None),
            Is.EqualTo(VoucherRedeemResult.AlreadyRedeemed));
        Assert.That(await second.RedeemVoucherResultAsync(Guid.Empty, "HANGAR-750", CancellationToken.None),
            Is.EqualTo(VoucherRedeemResult.Invalid));
    }

    [Test]
    public async Task VoucherPage_ShowsProgressAndFormLimits()
    {
        using var h = await CreateAsync();
        var store = new LocalHangarEquipmentStore(h.Cache);
        Assert.That(await store.RedeemVoucherAsync(h.Session.AccountId, "TRAINING-500", CancellationToken.None), Is.True);
        var page = LocalVouchers.Page(await store.GetAsync(h.Session.AccountId, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Redeemed 1 of 2 codes"));
            Assert.That(page, Does.Contain($"maxlength=\"{LocalVouchers.MaxCodeLength}\""));
            Assert.That(LocalVouchers.IsWellFormed("HANGAR-750"), Is.True);
            Assert.That(LocalVouchers.IsWellFormed("HANGAR 750"), Is.False);
            Assert.That(LocalVouchers.IsWellFormed(new string('A', LocalVouchers.MaxCodeLength + 1)), Is.False);
        });
    }
}
