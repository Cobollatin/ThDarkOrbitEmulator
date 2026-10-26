using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPaymentDetailTests
{
    private static QueryCollection Parse(string query) => new(QueryHelpers.ParseQuery(query));

    [TestCase("action=internalPayment&detail=starter")]
    [TestCase("action=internalPayment&detail=armada&from=internalStart&lang=es")]
    [TestCase("action=internalPayment&detail=missing_offer-2")]
    public void PageQuery_AcceptsWellFormedDetail(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.True);

    [TestCase("action=internalPayment&detail=")]
    [TestCase("action=internalPayment&detail=starter&detail=hangar")]
    [TestCase("action=internalPayment&DETAIL=starter")]
    [TestCase("action=internalPayment&Detail=starter")]
    [TestCase("action=internalPayment&detail=star%20ter")]
    [TestCase("action=internalPayment&detail=../starter")]
    [TestCase("action=internalPayment&detail=%3Cscript%3E")]
    [TestCase("action=internalPayment&detail=st%C3%A4rter")]
    [TestCase("action=internalPayment&detail=starter%0A")]
    [TestCase("action=internalPayment&detail=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("action=internalPayment&package=starter")]
    public void PageQuery_RejectsMalformedDetail(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.False);

    [Test]
    public void DetailId_ReturnsValueOrNull()
    {
        Assert.That(LocalPaymentAction.DetailId(Parse("action=internalPayment&detail=fleet")), Is.EqualTo("fleet"));
        Assert.That(LocalPaymentAction.DetailId(Parse("action=internalPayment")), Is.Null);
    }

    [Test]
    public void Find_IsExactAndCaseSensitive()
    {
        var catalog = LocalPayment.Packages(null);
        Assert.That(catalog.Select(package => package.Id), Is.EqualTo(new[] { "starter", "hangar", "fleet", "squadron", "armada" }));
        Assert.That(LocalPayment.Find(catalog, "fleet")!.Amount, Is.EqualTo(5000));
        Assert.That(LocalPayment.Find(catalog, "Fleet"), Is.Null);
        Assert.That(LocalPayment.Find(catalog, "missing"), Is.Null);
        Assert.That(LocalPayment.Find(catalog, null), Is.Null);
    }

    [Test]
    public void Find_UsesOffersWhenPresent()
    {
        var catalog = LocalPayment.Packages(Offers());
        Assert.That(LocalPayment.Find(catalog, "starter"), Is.Null);
        Assert.That(LocalPayment.Find(catalog, "uri_pack")!.AssetId, Is.EqualTo("uridium"));
    }

    [Test]
    public void Status_CoversAllStates()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["closed"] };
        var catalog = LocalPayment.Packages(Offers());
        Assert.That(LocalPayment.Status(state, LocalPayment.Find(catalog, "uri_pack")!), Is.EqualTo("available"));
        Assert.That(LocalPayment.Status(state, LocalPayment.Find(catalog, "retired")!), Is.EqualTo("unavailable"));
        Assert.That(LocalPayment.Status(state, LocalPayment.Find(catalog, "closed")!), Is.EqualTo("claimed"));
    }

    [Test]
    public void Page_TagsEachCardAndLinksToDetail()
    {
        var html = LocalPayment.Page(new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["hangar"] });
        Assert.That(html, Does.Contain("id=\"package-starter\" data-status=\"available\" data-asset=\"credits\""));
        Assert.That(html, Does.Contain("id=\"package-hangar\" data-status=\"claimed\""));
        Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalPayment&amp;detail=armada\""));
        Assert.That(html, Does.Not.Contain("id=\"packageDetail\""));
    }

    [Test]
    public void Page_RemainingSummary_TotalsAvailablePackagesPerAsset()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["closed"] };
        var html = LocalPayment.Page(state, offers: Offers());
        Assert.That(html, Does.Contain($"Still available: {700:N0} Credits, {50:N0} Uridium in 3 packages."));
    }

    [Test]
    public void Page_RemainingSummary_SaysWhenNothingIsLeft()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = [.. LocalPayment.Catalog.Select(p => p.Id)] };
        Assert.That(LocalPayment.Page(state), Does.Contain("No packages left to claim."));
    }

    [Test]
    public void Detail_AvailableCreditPackage_ShowsBalanceAfterClaimAndForm()
    {
        var state = new LocalEquipmentState([], []) { Credits = 12000 };
        var html = LocalPayment.Page(state, detail: "fleet");
        var panel = Panel(html);
        Assert.That(panel, Does.Contain("data-package=\"fleet\" data-status=\"available\""));
        Assert.That(panel, Does.Contain("<dt>Package</dt><dd>3 of 5</dd>"));
        Assert.That(panel, Does.Contain($"<dt>Grant</dt><dd>{5000:N0} Credits</dd>"));
        Assert.That(panel, Does.Contain($"<dt>Credits now</dt><dd>{12000:N0}</dd>"));
        Assert.That(panel, Does.Contain($"<dt>Credits after claim</dt><dd>{17000:N0}</dd>"));
        Assert.That(panel, Does.Contain("name=\"package\" value=\"fleet\""));
        Assert.That(panel, Does.Contain("rel=\"prev\" href=\"/indexInternal.es?action=internalPayment&amp;detail=hangar\""));
        Assert.That(panel, Does.Contain("rel=\"next\" href=\"/indexInternal.es?action=internalPayment&amp;detail=squadron\""));
        Assert.That(panel, Does.Contain("href=\"/indexInternal.es?action=internalPayment\">Close</a>"));
    }

    [Test]
    public void Detail_ClaimedPackage_HasNoFormOrProjection()
    {
        var state = new LocalEquipmentState([], []) { ClaimedPaymentPackages = ["starter"] };
        var panel = Panel(LocalPayment.Page(state, detail: "starter"));
        Assert.That(panel, Does.Contain("<dt>Status</dt><dd>Claimed</dd>"));
        Assert.That(panel, Does.Not.Contain("<form"));
        Assert.That(panel, Does.Not.Contain("Credits after claim"));
        Assert.That(panel, Does.Not.Contain("rel=\"prev\""));
        Assert.That(panel, Does.Contain("rel=\"next\""));
    }

    [Test]
    public void Detail_LastPackage_HasNoNextLink()
    {
        var panel = Panel(LocalPayment.Page(new LocalEquipmentState([], []), detail: "armada"));
        Assert.That(panel, Does.Contain("<dt>Package</dt><dd>5 of 5</dd>"));
        Assert.That(panel, Does.Not.Contain("rel=\"next\""));
    }

    [Test]
    public void Detail_NegativeBalance_IsShownAsZero()
    {
        var panel = Panel(LocalPayment.Page(new LocalEquipmentState([], []) { Credits = -50 }, detail: "starter"));
        Assert.That(panel, Does.Contain("<dt>Credits now</dt><dd>0</dd>"));
        Assert.That(panel, Does.Contain($"<dt>Credits after claim</dt><dd>{1000:N0}</dd>"));
    }

    [Test]
    public void Detail_UridiumOffer_HasNoCreditProjection()
    {
        var panel = Panel(LocalPayment.Page(new LocalEquipmentState([], []), offers: Offers(), detail: "uri_pack"));
        Assert.That(panel, Does.Contain($"<dt>Grant</dt><dd>{50:N0} Uridium</dd>"));
        Assert.That(panel, Does.Not.Contain("Credits now"));
        Assert.That(panel, Does.Contain("<form"));
    }

    [Test]
    public void Detail_DisabledOffer_IsUnavailableWithoutForm()
    {
        var panel = Panel(LocalPayment.Page(new LocalEquipmentState([], []), offers: Offers(), detail: "retired"));
        Assert.That(panel, Does.Contain("<dt>Status</dt><dd>Unavailable</dd>"));
        Assert.That(panel, Does.Not.Contain("<form"));
    }

    [Test]
    public void Detail_UnknownId_RendersNoPanel()
    {
        Assert.That(LocalPayment.Page(new LocalEquipmentState([], []), detail: "missing"), Does.Not.Contain("packageDetail"));
    }

    [Test]
    public void Detail_EncodesOfferNames()
    {
        IReadOnlyList<EconomyRewardOffer> offers = [new("payment", "odd", "<b>Bold</b>", "credits", 10, "none", 0, true)];
        var html = LocalPayment.Page(new LocalEquipmentState([], []), offers: offers, detail: "odd");
        Assert.That(html, Does.Not.Contain("<b>Bold</b>"));
        Assert.That(html, Does.Contain("&lt;b&gt;Bold&lt;/b&gt;"));
    }

    [Test]
    public void Page_LinksToVouchersAndEconomyLogbook()
    {
        var html = LocalPayment.Page(new LocalEquipmentState([], []));
        Assert.That(html, Does.Contain("action=internalVouchers"));
        Assert.That(html, Does.Contain("action=internalLogbook&amp;category=economy"));
        Assert.That(html, Does.Not.Contain("dosid"));
    }

    private static string Panel(string html)
    {
        var start = html.IndexOf("<section id=\"packageDetail\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        return html[start..(html.IndexOf("</section>", start, StringComparison.Ordinal) + 10)];
    }

    private static IReadOnlyList<EconomyRewardOffer> Offers() =>
    [
        new("payment", "cred_small", "Small credits", "credits", 200, "none", 0, true),
        new("payment", "cred_large", "Large credits", "credits", 500, "none", 0, true),
        new("payment", "uri_pack", "Uridium pack", "uridium", 50, "none", 0, true),
        new("payment", "retired", "Retired pack", "credits", 900, "none", 0, false),
        new("payment", "closed", "Closed pack", "credits", 300, "none", 0, true)
    ];
}
