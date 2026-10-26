using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSpaceSearchQueryKeyTests
{
    [TestCase("ACTION=internalSpaceSearch")]
    [TestCase("Action=internalSpaceSearch")]
    [TestCase("action=INTERNALSPACESEARCH")]
    [TestCase("action=internalspacesearch")]
    [TestCase("action=internalSpaceSearch&From=internalStart")]
    [TestCase("action=internalSpaceSearch&PRC=100")]
    [TestCase("action=internalSpaceSearch&Lang=en")]
    [TestCase("action=internalSpaceSearch&Filter=available")]
    [TestCase("action=internalSpaceSearch&Planet=mars")]
    [TestCase("action=internalSpaceSearch&Explored=mars")]
    [TestCase("action=internalSpaceSearch&action=INTERNALSPACESEARCH")]
    public void NonCanonicalKeyOrAction_IsRejected(string query) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalSpaceSearch")]
    [TestCase("action=internalSpaceSearch&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalSpaceSearch&filter=available&planet=mars")]
    [TestCase("action=internalSpaceSearch&explored=venus")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("planet=mars")]
    [TestCase("from=internalStart")]
    public void MissingAction_IsRejected(string query) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.False);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
