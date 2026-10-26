using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalVoucherPageTests
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

    private Task<bool> RedeemAsync(string code) => _store.RedeemVoucherAsync(_account, code, CancellationToken.None);

    [Test]
    public async Task FreshAccount_ListsEveryCodeAsAvailable_WithQuickRedeemForms()
    {
        var page = LocalVouchers.Page(await StateAsync());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Redeemed 0 of 2 codes"));
            Assert.That(page, Does.Contain("Granted: nothing. Still available: 1,250 credits."));
            Assert.That(page, Does.Contain("data-voucher=\"TRAINING-500\" data-status=\"available\""));
            Assert.That(page, Does.Contain("data-voucher=\"HANGAR-750\" data-status=\"available\""));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"code\" value=\"TRAINING-500\">"));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"code\" value=\"HANGAR-750\">"));
            Assert.That(page, Does.Not.Contain("voucherLatest"));
            Assert.That(page, Does.Not.Contain("voucherEmpty"));
            Assert.That(page, Does.Contain("<title>Vouchers - DarkOrbit</title>"));
            Assert.That(page, Does.Contain("data-filter=\"all\" aria-current=\"page\""));
        });
    }

    [Test]
    public async Task AfterRedemption_ShowsTotalsLatestAndRemovesQuickForm()
    {
        Assert.That(await RedeemAsync("hangar-750"), Is.True);
        var page = LocalVouchers.Page(await StateAsync());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Redeemed 1 of 2 codes"));
            Assert.That(page, Does.Contain("Granted: 750 credits. Still available: 500 credits."));
            Assert.That(page, Does.Contain("Last redeemed: HANGAR-750 (+750 credits)."));
            Assert.That(page, Does.Contain("data-voucher=\"HANGAR-750\" data-status=\"redeemed\">HANGAR-750: 750 credits (redeemed)"));
            Assert.That(page, Does.Not.Contain("value=\"HANGAR-750\""));
            Assert.That(page, Does.Contain("value=\"TRAINING-500\""));
            Assert.That(page, Does.Contain("Balance: 10,750 local credits"));
            Assert.That(page, Does.Contain("Available (1)"));
            Assert.That(page, Does.Contain("Redeemed (1)"));
        });
    }

    [Test]
    public async Task LatestNotice_FollowsRedemptionOrder()
    {
        await RedeemAsync("HANGAR-750");
        await RedeemAsync("TRAINING-500");
        var page = LocalVouchers.Page(await StateAsync());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Last redeemed: TRAINING-500 (+500 credits)."));
            Assert.That(page, Does.Contain("Granted: 1,250 credits. Still available: nothing."));
            Assert.That(page, Does.Not.Contain("name=\"code\" value="));
        });
    }

    [TestCase("available", "TRAINING-500", "HANGAR-750")]
    [TestCase("redeemed", "HANGAR-750", "TRAINING-500")]
    public async Task Filter_ShowsOnlyMatchingCodes(string filter, string shown, string hidden)
    {
        await RedeemAsync("HANGAR-750");
        var page = LocalVouchers.Page(await StateAsync(), filter: filter);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"data-voucher=\"{shown}\""));
            Assert.That(page, Does.Not.Contain($"data-voucher=\"{hidden}\""));
            Assert.That(page, Does.Contain($"<title>Vouchers ({filter}) - DarkOrbit</title>"));
            Assert.That(page, Does.Contain($"data-filter=\"{filter}\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("Redeemed 1 of 2 codes"));
        });
    }

    [Test]
    public async Task EmptyFilter_ShowsNotice()
    {
        var page = LocalVouchers.Page(await StateAsync(), filter: "redeemed");
        Assert.That(page, Does.Contain("<p id=\"voucherEmpty\">No redeemed voucher codes.</p>"));
        Assert.That(page, Does.Not.Contain("data-voucher="));
    }

    [Test]
    public async Task UnknownFilterPassedDirectly_FallsBackToAll()
    {
        var page = LocalVouchers.Page(await StateAsync(), filter: "Bogus");
        Assert.That(page, Does.Contain("<main id=\"vouchers\" data-filter=\"all\">"));
        Assert.That(page, Does.Contain("data-voucher=\"HANGAR-750\""));
    }

    [Test]
    public async Task AdminOffers_ShowNames_AndMarkDisabledOrOverCapAsUnavailable()
    {
        IReadOnlyList<EconomyRewardOffer> offers =
        [
            new("voucher", "SPRING-<1>", "Spring <gift>", "uridium", 40, "once", 1, true),
            new("voucher", "OFF-1", "OFF-1", "credits", 900, "once", 1, false),
            new("voucher", "HUGE-1", "HUGE-1", "credits", LocalVouchers.MaxGrant + 1, "once", 1, true)
        ];
        var page = LocalVouchers.Page(await StateAsync(), offers);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-voucher=\"SPRING-&lt;1&gt;\" data-status=\"available\""));
            Assert.That(page, Does.Contain("(Spring &lt;gift&gt;)"));
            Assert.That(page, Does.Not.Contain("<gift>"));
            Assert.That(page, Does.Contain("data-voucher=\"OFF-1\" data-status=\"unavailable\">OFF-1: 900 credits (unavailable)"));
            Assert.That(page, Does.Contain("data-voucher=\"HUGE-1\" data-status=\"unavailable\""));
            Assert.That(page, Does.Contain("Still available: 40 uridium."));
            Assert.That(page, Does.Contain("Unavailable (2)"));
            Assert.That(page, Does.Contain("Redeemed 0 of 3 codes"));
        });
    }

    [TestCase("all", true)]
    [TestCase("available", true)]
    [TestCase("redeemed", true)]
    [TestCase("unavailable", true)]
    [TestCase("Available", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("expired", false)]
    public void IsKnownFilter_IsExactAndCaseSensitive(string? filter, bool expected) =>
        Assert.That(LocalVouchers.IsKnownFilter(filter), Is.EqualTo(expected));

    [TestCase("?action=internalVouchers&filter=available", true)]
    [TestCase("?action=internalVouchers&filter=redeemed&lang=es", true)]
    [TestCase("?action=internalVouchers&filter=a&filter=b", false)]
    [TestCase("?action=internalVouchers&filter=%0A", false)]
    public void PageQuery_AcceptsOneFilter(string query, bool expected) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected));

    [Test]
    public async Task FilterTabs_LinkEveryFilterOnce()
    {
        var page = LocalVouchers.Page(await StateAsync());
        foreach (var filter in LocalVouchers.Filters)
            Assert.That(page.Split($"data-filter=\"{filter}\"").Length - 1, Is.EqualTo(filter == "all" ? 2 : 1), filter);
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalVouchers&amp;filter=available\""));
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalLogbook&amp;category=account\""));
    }
}
