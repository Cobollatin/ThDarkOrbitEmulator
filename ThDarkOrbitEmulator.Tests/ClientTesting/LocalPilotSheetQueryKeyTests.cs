using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSheetQueryKeyTests
{
    [TestCase("ACTION=internalPilotSheet")]
    [TestCase("Action=internalPilotSheet")]
    [TestCase("action=INTERNALPILOTSHEET")]
    [TestCase("action=internalpilotsheet")]
    [TestCase("action=internalPilotSheet&From=internalStart")]
    [TestCase("action=internalPilotSheet&PRC=100")]
    [TestCase("action=internalPilotSheet&Lang=en")]
    [TestCase("action=internalPilotSheet&Detail=shieldEngineering")]
    [TestCase("action=internalPilotSheet&FILTER=trained")]
    [TestCase("action=internalPilotSheet&Trained=shieldEngineering")]
    [TestCase("action=internalPilotSheet&Reset=1")]
    [TestCase("action=internalPilotSheet&action=INTERNALPILOTSHEET")]
    public void NonCanonicalKeyOrAction_IsRejected(string query) =>
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("action=internalPilotSheet")]
    [TestCase("action=internalPilotSheet&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalPilotSheet&detail=shieldEngineering&filter=trained")]
    [TestCase("action=internalPilotSheet&trained=shieldEngineering")]
    [TestCase("action=internalPilotSheet&reset=1")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("detail=shieldEngineering")]
    [TestCase("from=internalStart")]
    public void MissingAction_IsRejected(string query) =>
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query(query)), Is.False);

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
