using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalClanQueryKeyTests
{
    [TestCase("ACTION=internalClan")]
    [TestCase("Action=internalClan")]
    [TestCase("action=INTERNALCLAN")]
    [TestCase("action=internalclan")]
    [TestCase("action=internalClan&From=internalStart")]
    [TestCase("action=internalClan&PRC=100")]
    [TestCase("action=internalClan&Lang=en")]
    [TestCase("action=internalClan&action=INTERNALCLAN")]
    [TestCase("action=internalClan&Detail=mmo-guard")]
    [TestCase("action=internalClan&FILTER=recruiting")]
    [TestCase("action=internalClan&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalClanAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalClan")]
    [TestCase("action=internalClan&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalClan&detail=mmo-guard&filter=recruiting")]
    [TestCase("action=internalClan&q=Mars%20Guard")]
    [TestCase("action=internalClan&joined=mmo-guard")]
    [TestCase("action=internalClan&left=mmo-guard")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalClanAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
