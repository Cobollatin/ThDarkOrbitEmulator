using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalVoucherDetailTests
{
    private ServiceProvider _services = null!;
    private LocalHangarEquipmentStore _store = null!;
    private Guid _account;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        _store = new LocalHangarEquipmentStore(_services.GetRequiredService<IDistributedCache>());
        _account = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    private Task<LocalEquipmentState> StateAsync() => _store.GetAsync(_account, CancellationToken.None);

    [Test]
    public async Task AvailableCode_ShowsRewardBalanceRedeemFormAndNext()
    {
        var page = LocalVouchers.DetailPage(await StateAsync(), null, "all", "TRAINING-500");
        Assert.That(page, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<main id=\"voucherDetail\" data-voucher=\"TRAINING-500\" data-status=\"available\" data-filter=\"all\">"));
            Assert.That(page, Does.Contain("<title>Voucher TRAINING-500 - DarkOrbit</title>"));
            Assert.That(page, Does.Contain("Reward: 500 credits"));
            Assert.That(page, Does.Contain("Balance: 10,000 local credits"));
            Assert.That(page, Does.Contain("Code 1 of 2 in all."));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"code\" value=\"TRAINING-500\">"));
            Assert.That(page, Does.Contain("action=\"/indexInternal.es?action=internalVoucherRedeem\""));
            Assert.That(page, Does.Not.Contain("voucherPrevious"));
            Assert.That(page, Does.Contain("<a id=\"voucherNext\" href=\"/indexInternal.es?action=internalVouchers&amp;voucher=HANGAR-750\">"));
            Assert.That(page, Does.Contain("<a id=\"voucherClose\" href=\"/indexInternal.es?action=internalVouchers\">"));
        });
    }

    [Test]
    public async Task RedeemedCode_HasNoForm_AndLastCodeHasNoNext()
    {
        Assert.That(await _store.RedeemVoucherAsync(_account, "HANGAR-750", CancellationToken.None), Is.True);
        var page = LocalVouchers.DetailPage(await StateAsync(), null, "all", "HANGAR-750");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-status=\"redeemed\""));
            Assert.That(page, Does.Contain("Redeemed on this account."));
            Assert.That(page, Does.Contain("Balance: 10,750 local credits"));
            Assert.That(page, Does.Not.Contain("name=\"code\""));
            Assert.That(page, Does.Contain("<a id=\"voucherPrevious\" href=\"/indexInternal.es?action=internalVouchers&amp;voucher=TRAINING-500\">"));
            Assert.That(page, Does.Not.Contain("voucherNext"));
        });
    }

    [Test]
    public async Task Filter_ScopesNavigation_AndRejectsCodesOutsideIt()
    {
        Assert.That(await _store.RedeemVoucherAsync(_account, "TRAINING-500", CancellationToken.None), Is.True);
        var state = await StateAsync();
        var page = LocalVouchers.DetailPage(state, null, "available", "HANGAR-750");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-filter=\"available\""));
            Assert.That(page, Does.Contain("Code 1 of 1 in available."));
            Assert.That(page, Does.Not.Contain("voucherPrevious"));
            Assert.That(page, Does.Not.Contain("voucherNext"));
            Assert.That(page, Does.Contain("<a id=\"voucherClose\" href=\"/indexInternal.es?action=internalVouchers&amp;filter=available\">"));
            Assert.That(LocalVouchers.DetailPage(state, null, "available", "TRAINING-500"), Is.Null);
            Assert.That(LocalVouchers.DetailPage(state, null, "redeemed", "TRAINING-500"), Is.Not.Null);
        });
    }

    [TestCase("training-500")]
    [TestCase("UNKNOWN-1")]
    [TestCase("")]
    public async Task UnknownOrDifferentlyCasedCode_ReturnsNull(string code) =>
        Assert.That(LocalVouchers.DetailPage(await StateAsync(), null, "all", code), Is.Null);

    [Test]
    public async Task AdminOffers_EncodeIds_ShowNames_AndExplainUnavailable()
    {
        IReadOnlyList<EconomyRewardOffer> offers =
        [
            new("voucher", "SPRING-<1>", "Spring <gift>", "uridium", 40, "once", 1, true),
            new("voucher", "OFF-1", "OFF-1", "credits", 900, "once", 1, false)
        ];
        var state = await StateAsync();
        var spring = LocalVouchers.DetailPage(state, offers, "all", "SPRING-<1>");
        var off = LocalVouchers.DetailPage(state, offers, "all", "OFF-1");
        Assert.Multiple(() =>
        {
            Assert.That(spring, Does.Contain("<h1>Voucher SPRING-&lt;1&gt;</h1>"));
            Assert.That(spring, Does.Contain("Offer: Spring &lt;gift&gt;"));
            Assert.That(spring, Does.Not.Contain("<gift>"));
            Assert.That(spring, Does.Contain("Reward: 40 uridium"));
            Assert.That(spring, Does.Not.Contain("voucherBalance"));
            Assert.That(spring, Does.Contain("&amp;voucher=OFF-1\">Next</a>"));
            Assert.That(off, Does.Contain("data-status=\"unavailable\""));
            Assert.That(off, Does.Contain("Unavailable. This offer is disabled"));
            Assert.That(off, Does.Not.Contain("name=\"code\""));
            Assert.That(off, Does.Not.Contain("voucherName"));
            Assert.That(off, Does.Contain("&amp;voucher=SPRING-%3C1%3E\">Previous</a>"));
        });
    }

    [Test]
    public async Task ListRows_LinkToDetail_KeepingTheFilter()
    {
        var all = LocalVouchers.Page(await StateAsync());
        var available = LocalVouchers.Page(await StateAsync(), filter: "available");
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("<a href=\"/indexInternal.es?action=internalVouchers&amp;voucher=TRAINING-500\" data-detail=\"TRAINING-500\">Details</a></li>"));
            Assert.That(available, Does.Contain("<a href=\"/indexInternal.es?action=internalVouchers&amp;filter=available&amp;voucher=HANGAR-750\" data-detail=\"HANGAR-750\">Details</a></li>"));
        });
    }

    [TestCase("?action=internalVouchers&voucher=TRAINING-500", true)]
    [TestCase("?action=internalVouchers&filter=redeemed&voucher=HANGAR-750", true)]
    [TestCase("?action=internalVouchers&voucher=a&voucher=b", false)]
    [TestCase("?action=internalVouchers&voucher=%0A", false)]
    [TestCase("?action=internalVouchers&code=TRAINING-500", false)]
    public void PageQuery_AcceptsOneVoucher(string query, bool expected) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected));
}
