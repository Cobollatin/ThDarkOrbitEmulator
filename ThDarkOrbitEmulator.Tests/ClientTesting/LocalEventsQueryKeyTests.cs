using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsQueryKeyTests
{
    [TestCase("ACTION=internalEvents")]
    [TestCase("Action=internalEvents")]
    [TestCase("action=INTERNALEVENTS")]
    [TestCase("action=internalEvents&From=internalStart")]
    [TestCase("action=internalEvents&PRC=100")]
    [TestCase("action=internalEvents&Lang=en")]
    [TestCase("action=internalEvents&DETAIL=supplier")]
    [TestCase("action=internalEvents&Claimed=supplier")]
    [TestCase("action=internalEvents&Expedition=started")]
    [TestCase("action=internalEvents&lang=a%0Db")]
    public void NonCanonicalPageQuery_IsRejected(string query) =>
        Assert.That(LocalEventsAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalEvents")]
    [TestCase("action=internalEvents&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalEvents&detail=supplier")]
    [TestCase("action=internalEvents&claimed=supplier")]
    [TestCase("action=internalEvents&expedition=started")]
    [TestCase("action=internalEvents&detail=supplier&claimed=supplier")]
    public void CanonicalPageQuery_IsAccepted(string query) =>
        Assert.That(LocalEventsAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
