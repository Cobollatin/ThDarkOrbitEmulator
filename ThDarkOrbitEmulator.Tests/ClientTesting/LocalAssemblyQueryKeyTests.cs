using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAssemblyQueryKeyTests
{
    [TestCase("ACTION=internalAssembly")]
    [TestCase("Action=internalAssembly")]
    [TestCase("action=INTERNALASSEMBLY")]
    [TestCase("action=internalassembly")]
    [TestCase("action=internalAssemblyCraft")]
    [TestCase("action=internalAssembly&From=internalStart")]
    [TestCase("action=internalAssembly&PRC=100")]
    [TestCase("action=internalAssembly&Lang=en")]
    [TestCase("action=internalAssembly&action=INTERNALASSEMBLY")]
    [TestCase("action=internalAssembly&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalAssembly")]
    [TestCase("action=internalAssembly&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalAssembly&status=ready&detail=lf3")]
    [TestCase("action=internalAssembly&crafted=lf3")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
