using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-190: the local Command Center accepts only canonical query spelling.
public class LocalCommandCenterQueryKeyTests
{
    [TestCase("ACTION=internalCommandCenter")]
    [TestCase("Action=internalCommandCenter")]
    [TestCase("action=INTERNALCOMMANDCENTER")]
    [TestCase("action=internalcommandcenter")]
    [TestCase("action=internalCommandCenter&Category=events")]
    [TestCase("action=internalCommandCenter&ITEM=Voyagers2607")]
    [TestCase("action=internalCommandCenter&From=internalStart")]
    [TestCase("action=internalCommandCenter&PRC=100")]
    [TestCase("action=internalCommandCenter&Lang=en")]
    [TestCase("action=internalCommandCenter&SORT=ending")]
    [TestCase("action=internalCommandCenter&Urgency=running")]
    [TestCase("action=internalCommandCenter&Q=discord")]
    [TestCase("action=internalCommandCenter&category=events&CATEGORY=deals")]
    public void NonCanonicalQuery_Returns400(string query)
    {
        var result = LocalCommandCenter.Resolve(Query(query));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400), result.Html);
            Assert.That(result.Html, Does.Contain("Command center request failed (400)"));
            Assert.That(LocalCommandCenter.Validate(Query(query))?.Status, Is.EqualTo(400));
        });
    }

    [TestCase("action=internalCommandCenter")]
    [TestCase("action=internalCommandCenter&category=events&sort=ending")]
    [TestCase("action=internalCommandCenter&item=Voyagers2607")]
    [TestCase("action=internalCommandCenter&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalCommandCenter&urgency=running&q=discord")]
    public void CanonicalQuery_IsAccepted(string query)
    {
        Assert.That(LocalCommandCenter.Validate(Query(query)), Is.Null);
        Assert.That(LocalCommandCenter.Resolve(Query(query)).Status, Is.EqualTo(200));
    }

    [TestCase("action=internalCommandCenter&Category=events", "Category")]
    [TestCase("action=internalCommandCenter&SORT=ending", "SORT")]
    [TestCase("action=INTERNALCOMMANDCENTER", "INTERNALCOMMANDCENTER")]
    public void RejectedInput_IsNotEchoed(string query, string rejected)
    {
        Assert.That(LocalCommandCenter.Resolve(Query(query)).Html, Does.Not.Contain(rejected));
    }

    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return context.Request.Query;
    }
}
