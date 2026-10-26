using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSkylabQueryKeyTests
{
    [TestCase("ACTION=internalSkylab")]
    [TestCase("Action=internalSkylab")]
    [TestCase("action=INTERNALSKYLAB")]
    [TestCase("action=internalskylab")]
    [TestCase("action=internalSkylab&From=internalStart")]
    [TestCase("action=internalSkylab&PRC=100")]
    [TestCase("action=internalSkylab&Lang=en")]
    [TestCase("action=internalSkylab&action=INTERNALSKYLAB")]
    [TestCase("action=internalSkylab&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalSkylabAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalSkylab")]
    [TestCase("action=internalSkylab&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalSkylab&detail=prometium&filter=upgradable")]
    [TestCase("action=internalSkylab&upgraded=prometium")]
    [TestCase("action=internalSkylab&collected=1")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalSkylabAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
