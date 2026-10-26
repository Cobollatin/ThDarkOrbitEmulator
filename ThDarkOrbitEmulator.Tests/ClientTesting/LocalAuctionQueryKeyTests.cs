using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionQueryKeyTests
{
    [TestCase("ACTION=internalAuction")]
    [TestCase("Action=internalAuction")]
    [TestCase("action=INTERNALAUCTION")]
    [TestCase("action=internalauction")]
    [TestCase("action=internalAuctionBid")]
    [TestCase("action=internalAuction&From=internalStart")]
    [TestCase("action=internalAuction&PRC=100")]
    [TestCase("action=internalAuction&Lang=en")]
    [TestCase("action=internalAuction&action=INTERNALAUCTION")]
    [TestCase("action=internalAuction&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalAuction")]
    [TestCase("action=internalAuction&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalAuction&filter=open&lot=laser_lf3")]
    [TestCase("action=internalAuction&placed=laser_lf3")]
    [TestCase("action=internalAuction&claimed=laser_lf3")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
