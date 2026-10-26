using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-196: the Vouchers page requires canonical key casing and the exact action value.
public class LocalVoucherQueryKeyTests
{
    [TestCase("ACTION=internalVouchers")]
    [TestCase("Action=internalVouchers")]
    [TestCase("action=INTERNALVOUCHERS")]
    [TestCase("action=internalvouchers")]
    [TestCase("action=internalVoucherRedeem")]
    [TestCase("action=internalVouchers&From=internalStart")]
    [TestCase("action=internalVouchers&PRC=100")]
    [TestCase("action=internalVouchers&Lang=en")]
    [TestCase("action=internalVouchers&Filter=available")]
    [TestCase("action=internalVouchers&VOUCHER=TRAINING-500")]
    [TestCase("action=internalVouchers&REDEEMED=TRAINING-500")]
    [TestCase("action=internalVouchers&Q=TRAINING")]
    [TestCase("action=internalVouchers&action=INTERNALVOUCHERS")]
    [TestCase("action=internalVouchers&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalVouchers")]
    [TestCase("action=internalVouchers&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalVouchers&filter=available&voucher=TRAINING-500&q=TRAINING")]
    [TestCase("action=internalVouchers&redeemed=TRAINING-500")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalVoucherAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
