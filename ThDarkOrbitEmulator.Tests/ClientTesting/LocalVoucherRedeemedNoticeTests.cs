using System.Text;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-114: a successful redeem redirects to `internalVouchers&redeemed={id}` and the list confirms the grant.
public class LocalVoucherRedeemedNoticeTests
{
    private ServiceProvider _services = null!;
    private IDistributedCache _cache = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private Guid _account;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        _cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(_cache);
        _store = new LocalHangarEquipmentStore(_cache);
        _account = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    private Task<LocalEquipmentState> StateAsync() => _store.GetAsync(_account, CancellationToken.None);

    [TestCase("TRAINING-500", "TRAINING-500")]
    [TestCase("training-500", "TRAINING-500")]
    [TestCase("  Hangar-750 ", "HANGAR-750")]
    public async Task RedeemPost_RedirectsToCanonicalRedeemedCode(string submitted, string canonical)
    {
        var session = await SessionAsync("mmo");
        var context = Post(session.Dosid, $"code={Uri.EscapeDataString(submitted)}");

        await LocalVoucherAction.HandleAsync(context, _sessions, _store, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
        Assert.That(context.Response.Headers.Location.ToString(),
            Is.EqualTo($"/indexInternal.es?action=internalVouchers&redeemed={canonical}"));
    }

    [Test]
    public async Task FailedRedeems_DoNotRedirect()
    {
        var session = await SessionAsync("mmo");
        var first = Post(session.Dosid, "code=TRAINING-500");
        await LocalVoucherAction.HandleAsync(first, _sessions, _store, CancellationToken.None);
        var repeat = Post(session.Dosid, "code=TRAINING-500");
        await LocalVoucherAction.HandleAsync(repeat, _sessions, _store, CancellationToken.None);
        var unknown = Post(session.Dosid, "code=NOPE-1");
        await LocalVoucherAction.HandleAsync(unknown, _sessions, _store, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(repeat.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(repeat.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(unknown.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [TestCase("TRAINING-500", "/indexInternal.es?action=internalVouchers&redeemed=TRAINING-500")]
    [TestCase("spring-2026", "/indexInternal.es?action=internalVouchers&redeemed=spring-2026")]
    [TestCase(null, "/indexInternal.es?action=internalVouchers")]
    [TestCase("", "/indexInternal.es?action=internalVouchers")]
    [TestCase("bad code", "/indexInternal.es?action=internalVouchers")]
    [TestCase("a_b", "/indexInternal.es?action=internalVouchers")]
    [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", "/indexInternal.es?action=internalVouchers")]
    public void RedeemedLocation_OnlyNamesIdsThePageAccepts(string? id, string expected) =>
        Assert.That(LocalVoucherAction.RedeemedLocation(id), Is.EqualTo(expected));

    [Test]
    public async Task Notice_AfterRedeem_ShowsRewardAndBalance()
    {
        Assert.That(await _store.RedeemVoucherAsync(_account, "hangar-750", CancellationToken.None), Is.True);
        var state = await StateAsync();
        var notice = LocalVouchers.RedeemedNotice(state, null, "HANGAR-750");
        var page = LocalVouchers.Page(state, null, "all", notice!);

        Assert.Multiple(() =>
        {
            Assert.That(notice, Is.EqualTo("<p id=\"voucherRedeemedNotice\" role=\"status\" data-voucher=\"HANGAR-750\" data-redeemed=\"true\">Voucher HANGAR-750 redeemed: +750 credits. Balance: 10,750 local credits.</p>"));
            Assert.That(page, Does.Contain("<h1>Vouchers</h1><p id=\"voucherRedeemedNotice\""));
            Assert.That(page, Does.Contain("Redeemed 1 of 2 codes"));
        });
    }

    [Test]
    public async Task Notice_ForCodeNotRedeemedOnThisAccount_IsNeutral()
    {
        Assert.That(await _store.RedeemVoucherAsync(Guid.NewGuid(), "TRAINING-500", CancellationToken.None), Is.True);
        var notice = LocalVouchers.RedeemedNotice(await StateAsync(), null, "TRAINING-500");

        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain("data-redeemed=\"false\""));
            Assert.That(notice, Does.Contain("Voucher TRAINING-500 is not redeemed on this account."));
            Assert.That(notice, Does.Not.Contain("+500"));
            Assert.That(notice, Does.Not.Contain("Balance"));
        });
    }

    [TestCase("training-500")]
    [TestCase("UNKNOWN-1")]
    [TestCase("WELCOME")]
    public async Task Notice_ForUnknownOrWrongCaseCode_IsNull(string id) =>
        Assert.That(LocalVouchers.RedeemedNotice(await StateAsync(), null, id), Is.Null);

    [Test]
    public async Task Notice_UsesOfferNameIdAndNonCreditAssetWithoutBalance()
    {
        var offers = new List<EconomyRewardOffer> { Offer("SPRING-URI", "uridium", 40, enabled: true) };
        var state = await StateAsync();
        state.RedeemedVoucherCodes.Add("SPRING-URI");

        var notice = LocalVouchers.RedeemedNotice(state, offers, "SPRING-URI");

        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain("Voucher SPRING-URI redeemed: +40 uridium."));
            Assert.That(notice, Does.Not.Contain("Balance"));
            // Catalog codes are not shown when Postgres offers replace them.
            Assert.That(LocalVouchers.RedeemedNotice(state, offers, "TRAINING-500"), Is.Null);
        });
    }

    [Test]
    public async Task Notice_ForDisabledOfferNotRedeemed_IsNeutral()
    {
        var offers = new List<EconomyRewardOffer> { Offer("OLD-1", "credits", 100, enabled: false) };

        var notice = LocalVouchers.RedeemedNotice(await StateAsync(), offers, "OLD-1");

        Assert.That(notice, Does.Contain("data-redeemed=\"false\""));
    }

    [Test]
    public async Task Page_WithoutNotice_HasNoNoticeMarkup() =>
        Assert.That(LocalVouchers.Page(await StateAsync()), Does.Not.Contain("voucherRedeemedNotice"));

    [TestCase("redeemed=TRAINING-500&voucher=TRAINING-500")]
    [TestCase("voucher=TRAINING-500&redeemed=TRAINING-500")]
    [TestCase("redeemed=TRAINING-500&redeemed=TRAINING-500")]
    [TestCase("REDEEMED=TRAINING-500")]
    [TestCase("Redeemed=TRAINING-500")]
    public async Task Endpoint_InvalidRedeemedQuery_Returns400BeforeSession(string extra)
    {
        var context = await GetPageAsync($"?action=internalVouchers&{extra}", dosid: null);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
    }

    [TestCase("%3Cscript%3Eboom%3C%2Fscript%3E")]
    [TestCase("a_b")]
    [TestCase("")]
    [TestCase("%20")]
    [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")]
    public async Task Endpoint_MalformedRedeemedCode_Returns400BeforeSession_WithoutEcho(string code)
    {
        var context = await GetPageAsync($"?action=internalVouchers&redeemed={code}", dosid: null);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(Body(context), Does.Not.Contain("<script>boom"));
        Assert.That(Body(context), Does.Contain("Invalid"));
    }

    [TestCase("?action=internalVouchers&redeemed=TRAINING-500")]
    [TestCase("?action=internalVouchers&filter=redeemed&redeemed=HANGAR-750")]
    public async Task Endpoint_WellFormedRedeemedCode_WithoutSession_Returns401(string query)
    {
        var context = await GetPageAsync(query, dosid: "missing-session");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    private static EconomyRewardOffer Offer(string id, string asset, long amount, bool enabled) =>
        new("voucher", id, id, asset, amount, "none", 0, enabled);

    private async Task<GameAuthSession> SessionAsync(string company)
    {
        var session = (await _sessions.CreateSessionAsync(
            new AccountSummary(_account, "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(session.Dosid, company, CancellationToken.None), Is.True);
        return session;
    }

    private static DefaultHttpContext Post(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<DefaultHttpContext> GetPageAsync(string query, string? dosid)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();
        // Only the session store is reached before the 400/401 responses checked here.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, _sessions, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
