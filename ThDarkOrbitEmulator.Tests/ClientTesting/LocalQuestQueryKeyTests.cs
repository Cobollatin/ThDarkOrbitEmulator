using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-195: the Quests page requires canonical key casing and the exact action value.
public class LocalQuestQueryKeyTests
{
    [TestCase("ACTION=internalQuests")]
    [TestCase("Action=internalQuests")]
    [TestCase("action=INTERNALQUESTS")]
    [TestCase("action=internalquests")]
    [TestCase("action=internalQuestClaim")]
    [TestCase("action=internalQuests&From=internalStart")]
    [TestCase("action=internalQuests&PRC=100")]
    [TestCase("action=internalQuests&Lang=en")]
    [TestCase("action=internalQuests&Filter=ready")]
    [TestCase("action=internalQuests&action=INTERNALQUESTS")]
    [TestCase("action=internalQuests&lang=a%0Db")]
    public void NonCanonicalQuery_IsRejected(string query) =>
        Assert.That(LocalQuestAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalQuests")]
    [TestCase("action=internalQuests&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalQuests&filter=ready&detail=first_purchase")]
    [TestCase("action=internalQuests&claimed=first_purchase")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalQuestAction.ValidPageQuery(Query(query)), Is.True);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
