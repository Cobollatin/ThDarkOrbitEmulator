using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalVoucherSearchTests
{
    private static IQueryCollection Query(string query) =>
        new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("action=internalVouchers&q=TRAINING")]
    [TestCase("action=internalVouchers&filter=available&q=Spring+Gift")]
    public void SearchQuery_AcceptsWellFormedInput(string query) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("action=internalVouchers&Q=TRAINING")]
    [TestCase("action=internalVouchers&q=x&q=y")]
    [TestCase("action=internalVouchers&q=x&unknown=y")]
    public void SearchQuery_RejectsWrongOrRepeatedKeys(string query) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("")]
    [TestCase(" a")]
    [TestCase("a ")]
    [TestCase("a_b")]
    [TestCase("a<b")]
    [TestCase("123456789012345678901234567890123")]
    [TestCase("é")]
    public void SearchTerm_RejectsMalformedInput(string term) =>
        Assert.That(LocalVouchers.IsWellFormedSearch(term), Is.False);

    [Test]
    public void Search_FindsCodeOrAdminOfferName_AndKeepsFilterAndDetailScope()
    {
        var state = new LocalEquipmentState([], []);
        IReadOnlyList<EconomyRewardOffer> offers =
        [
            new("voucher", "SPRING-1", "Spring Gift", "credits", 100, "once", 1, true),
            new("voucher", "SUMMER-1", "Summer Gift", "credits", 200, "once", 1, false),
            new("voucher", "SPRING-2", "Other", "uridium", 10, "once", 1, true)
        ];
        var page = LocalVouchers.Page(state, offers, "available", search: "spring");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-voucher=\"SPRING-1\""));
            Assert.That(page, Does.Contain("data-voucher=\"SPRING-2\""));
            Assert.That(page, Does.Not.Contain("data-voucher=\"SUMMER-1\""));
            Assert.That(page, Does.Contain("filter=available&amp;q=spring&amp;voucher=SPRING-1"));
            Assert.That(page, Does.Contain("filter=unavailable&amp;q=spring"));
            Assert.That(page, Does.Contain("id=\"voucherClearSearch\" href=\"/indexInternal.es?action=internalVouchers&amp;filter=available\""));
            Assert.That(LocalVouchers.DetailPage(state, offers, "available", "SUMMER-1", "spring"), Is.Null);
        });
        var detail = LocalVouchers.DetailPage(state, offers, "available", "SPRING-1", "spring");
        Assert.That(detail, Does.Contain("filter=available&amp;q=spring&amp;voucher=SPRING-2"));
    }

    [Test]
    public void Search_NoMatches_ShowsEmptyState_AndEncodesInput()
    {
        var page = LocalVouchers.Page(new LocalEquipmentState([], []), search: "no match");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("No voucher codes match this search."));
            Assert.That(page, Does.Not.Contain("data-voucher=\"TRAINING-500\""));
            Assert.That(page, Does.Contain("name=\"q\""));
        });
    }
}
