using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-189: Map overview query keys and the action value match exactly, like Logbook and Rankings.
public class LocalMapOverviewQueryKeyTests
{
    [TestCase("ACTION=internalMapOverview")]
    [TestCase("Action=internalMapOverview")]
    [TestCase("action=INTERNALMAPOVERVIEW")]
    [TestCase("action=internalmapoverview")]
    [TestCase("action=internalMapOverview&Map=5")]
    [TestCase("action=internalMapOverview&MAP=1")]
    [TestCase("action=internalMapOverview&Company=eic")]
    [TestCase("action=internalMapOverview&From=internalStart")]
    [TestCase("action=internalMapOverview&PRC=100")]
    [TestCase("action=internalMapOverview&Lang=en")]
    [TestCase("action=internalMapOverview&company=eic&MAP=5")]
    public void NonCanonicalKeyOrAction_Returns400(string query)
    {
        var result = Resolve(query);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400), result.Html);
            Assert.That(result.Html, Does.Contain("Map overview request failed (400)"));
            Assert.That(result.Html, Does.Not.Contain("localMapOverview"));
            Assert.That(Validate(query)?.Status, Is.EqualTo(400));
        });
    }

    [TestCase("action=internalMapOverview")]
    [TestCase("action=internalMapOverview&map=5")]
    [TestCase("action=internalMapOverview&company=eic&map=5")]
    [TestCase("action=internalMapOverview&from=internalStart&prc=100&lang=en")]
    [TestCase("map=1")] // unit callers may omit action; the endpoint always routes with one
    public void CanonicalQuery_IsServed(string query)
    {
        Assert.That(Validate(query), Is.Null);
        Assert.That(Resolve(query).Status, Is.EqualTo(200));
    }

    [TestCase("ACTION=internalMapOverview")]
    [TestCase("action=internalMapOverview&Map=5")]
    [TestCase("action=internalMapOverview&COMPANY=eic")]
    public void RejectedKey_IsNotEchoed(string query)
    {
        var html = Resolve(query).Html;

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("ACTION"));
            Assert.That(html, Does.Not.Contain("'Map'"));
            Assert.That(html, Does.Not.Contain("COMPANY"));
            Assert.That(html, Does.Contain("Unknown map overview parameter."));
        });
    }

    [Test]
    public void WrongCaseActionValue_HasOwnMessageWithoutEchoingIt()
    {
        var html = Resolve("action=INTERNALMAPOVERVIEW").Html;

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Invalid map overview action."));
            Assert.That(html, Does.Not.Contain("INTERNALMAPOVERVIEW"));
        });
    }

    [Test]
    public void DuplicateKeyInDifferentCase_Returns400()
    {
        // ASP.NET merges map and MAP into one key with two values; either rule rejects it.
        var result = Resolve("action=internalMapOverview&map=1&MAP=5");

        Assert.That(result.Status, Is.EqualTo(400));
    }

    private static LocalMapOverview.Result? Validate(string query) => LocalMapOverview.Validate(Query(query));

    private static LocalMapOverview.Result Resolve(string query) =>
        LocalMapOverview.Resolve(Query(query), new GameAuthSession { Company = "eic" });

    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return context.Request.Query;
    }
}
