using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-120: a successful package claim redirects to internalPayment&claimed={package}, and the page
// confirms the grant only for a package the account has actually claimed.
[TestFixture]
public sealed class LocalPaymentClaimedNoticeTests
{
    private static QueryCollection Parse(string query) => new(QueryHelpers.ParseQuery(query));

    [Test]
    public async Task Claim_RedirectsWithClaimedPackage_AndPageConfirmsGrant()
    {
        var (sessions, store, session) = await HarnessAsync();
        var claim = ClaimRequest(session.Dosid, "fleet");
        await LocalPaymentAction.HandleAsync(claim, sessions, store, CancellationToken.None);

        Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
        var location = claim.Response.Headers.Location.ToString();
        Assert.That(location, Is.EqualTo("/indexInternal.es?action=internalPayment&claimed=fleet"));

        var query = Parse(new Uri("http://local" + location).Query);
        Assert.That(LocalPaymentAction.ValidPageQuery(query), Is.True);
        var html = LocalPayment.Page(await store.GetAsync(session.AccountId, CancellationToken.None),
            claimed: LocalPaymentAction.ClaimedId(query));
        Assert.That(Notice(html), Is.EqualTo(
            $"<p id=\"paymentClaimed\" role=\"status\" data-package=\"fleet\">Package claimed: Fleet supply · {5000:N0} Credits added to your account. Balance: {15000:N0} local credits.</p>"));
    }

    [Test]
    public async Task FailedClaims_DoNotRedirect()
    {
        var (sessions, store, session) = await HarnessAsync();
        var first = ClaimRequest(session.Dosid, "starter");
        await LocalPaymentAction.HandleAsync(first, sessions, store, CancellationToken.None);
        var repeat = ClaimRequest(session.Dosid, "starter");
        await LocalPaymentAction.HandleAsync(repeat, sessions, store, CancellationToken.None);
        var unknown = ClaimRequest(session.Dosid, "platinum");
        await LocalPaymentAction.HandleAsync(unknown, sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
            Assert.That(repeat.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(repeat.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(unknown.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [TestCase("starter", "/indexInternal.es?action=internalPayment&claimed=starter")]
    [TestCase("uri_pack-2", "/indexInternal.es?action=internalPayment&claimed=uri_pack-2")]
    public void ClaimedLocation_UsesLowercaseKey(string id, string expected) =>
        Assert.That(LocalPaymentAction.ClaimedLocation(id), Is.EqualTo(expected));

    [TestCase("action=internalPayment&claimed=starter")]
    [TestCase("action=internalPayment&claimed=armada&detail=fleet&from=internalStart&lang=es")]
    [TestCase("action=internalPayment&claimed=missing_offer-2")]
    public void PageQuery_AcceptsWellFormedClaimed(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.True);

    [TestCase("action=internalPayment&claimed=")]
    [TestCase("action=internalPayment&claimed=starter&claimed=hangar")]
    [TestCase("action=internalPayment&CLAIMED=starter")]
    [TestCase("action=internalPayment&Claimed=starter")]
    [TestCase("action=internalPayment&claimed=star%20ter")]
    [TestCase("action=internalPayment&claimed=../starter")]
    [TestCase("action=internalPayment&claimed=%3Cscript%3E")]
    [TestCase("action=internalPayment&claimed=starter%0A")]
    [TestCase("action=internalPayment&claimed=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PageQuery_RejectsMalformedClaimed(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.False);

    [Test]
    public void ClaimedId_ReturnsValueOrNull()
    {
        Assert.That(LocalPaymentAction.ClaimedId(Parse("action=internalPayment&claimed=fleet")), Is.EqualTo("fleet"));
        Assert.That(LocalPaymentAction.ClaimedId(Parse("action=internalPayment&detail=fleet")), Is.Null);
    }

    [TestCase(null)]
    [TestCase("hangar")]
    [TestCase("missing")]
    [TestCase("STARTER")]
    public void Notice_IsHiddenUnlessThisAccountClaimedThatPackage(string? claimed)
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["starter"] };
        Assert.That(LocalPayment.Page(state, claimed: claimed), Does.Not.Contain("paymentClaimed"));
    }

    [Test]
    public void Notice_ForUridiumOffer_HasNoCreditBalance()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["uri_pack"] };
        var notice = Notice(LocalPayment.Page(state, offers: Offers(), claimed: "uri_pack"));
        Assert.That(notice, Does.Contain($"Uridium pack · {50:N0} Uridium added to your account."));
        Assert.That(notice, Does.Not.Contain("Balance"));
    }

    [Test]
    public void Notice_StillShowsForOfferDisabledAfterClaim()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["retired"] };
        Assert.That(Notice(LocalPayment.Page(state, offers: Offers(), claimed: "retired")),
            Does.Contain($"Retired pack · {900:N0} Credits added"));
    }

    [Test]
    public void Notice_BalanceNeverBelowZero_AndNameIsEncoded()
    {
        IReadOnlyList<EconomyRewardOffer> offers = [new("payment", "odd", "<b>Bold</b>", "credits", 10, "none", 0, true)];
        var state = new LocalEquipmentState([], []) { Credits = -20, ClaimedPaymentPackages = ["odd"] };
        var notice = Notice(LocalPayment.Page(state, offers: offers, claimed: "odd"));
        Assert.That(notice, Does.Contain("&lt;b&gt;Bold&lt;/b&gt;"));
        Assert.That(notice, Does.Not.Contain("<b>"));
        Assert.That(notice, Does.EndWith("Balance: 0 local credits.</p>"));
    }

    [Test]
    public void Notice_WorksWithDetailPanel_AndOnProxyPage()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["starter"] };
        var html = LocalPayment.Page(state, proxy: true, detail: "hangar", claimed: "starter");
        Assert.That(html, Does.Contain("id=\"paymentProxy\""));
        Assert.That(html, Does.Contain("data-package=\"starter\">Package claimed: Starter supply"));
        Assert.That(html, Does.Contain("<section id=\"packageDetail\" data-package=\"hangar\""));
        Assert.That(html.IndexOf("paymentClaimed", StringComparison.Ordinal),
            Is.LessThan(html.IndexOf("packageDetail", StringComparison.Ordinal)));
    }

    private static string Notice(string html)
    {
        var start = html.IndexOf("<p id=\"paymentClaimed\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "notice missing");
        return html[start..(html.IndexOf("</p>", start, StringComparison.Ordinal) + 4)];
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Store, GameAuthSession Session)> HarnessAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session);
    }

    private static DefaultHttpContext ClaimRequest(string dosid, string package)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalPaymentClaim");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes($"package={package}");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static IReadOnlyList<EconomyRewardOffer> Offers() =>
    [
        new("payment", "uri_pack", "Uridium pack", "uridium", 50, "none", 0, true),
        new("payment", "retired", "Retired pack", "credits", 900, "none", 0, false)
    ];
}
