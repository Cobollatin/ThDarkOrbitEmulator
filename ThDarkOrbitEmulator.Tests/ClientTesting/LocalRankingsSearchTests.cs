using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalRankingsSearchTests
{
    [Test]
    public void Search_MatchesPilotsAcrossCompaniesAndKeepsFilters()
    {
        var all = Resolve("q=nOvA");
        var mmo = Resolve("q=nOvA&company=mmo");
        Assert.Multiple(() =>
        {
            Assert.That(all.Status, Is.EqualTo(200));
            Assert.That(all.Html, Does.Contain("3 pilots match nOvA"));
            Assert.That(all.Html, Does.Contain("Nova-MMO"));
            Assert.That(all.Html, Does.Contain("Nova-EIC"));
            Assert.That(all.Html, Does.Contain("Nova-VRU"));
            Assert.That(all.Html, Does.Contain("&amp;q=nOvA&amp;pilot=Nova-MMO"));
            Assert.That(all.Html, Does.Contain("company=eic&amp;page=1&amp;q=nOvA"));
            Assert.That(all.Html, Does.Contain("category=honour&amp;company=all&amp;page=1&amp;q=nOvA"));
            Assert.That(mmo.Html, Does.Contain("1 pilot matches nOvA"));
            Assert.That(mmo.Html, Does.Not.Contain("Nova-EIC</a></td>"));
        });
    }

    [Test]
    public void Search_PaginatesMatchingEntriesAndDetailReturnsToSamePage()
    {
        var second = Resolve("q=MMO&page=2");
        var detail = Resolve("q=MMO&page=2&pilot=Draco-MMO");
        Assert.Multiple(() =>
        {
            Assert.That(second.Status, Is.EqualTo(200));
            Assert.That(second.Html, Does.Contain("12 pilots match MMO"));
            Assert.That(second.Html, Does.Contain("Nebula-MMO"));
            Assert.That(second.Html, Does.Not.Contain("Nova-MMO</a></td>"));
            Assert.That(second.Html, Does.Contain("page=1&amp;q=MMO\">Previous"));
            Assert.That(detail.Status, Is.EqualTo(200));
            Assert.That(detail.Html, Does.Contain("page=2&amp;q=MMO\">Back to ranking"));
            Assert.That(detail.Html, Does.Not.Contain("View on page"));
        });
    }

    [Test]
    public void Search_EmptyResultsAndOutOfScopePilot()
    {
        var empty = Resolve("q=missing");
        Assert.Multiple(() =>
        {
            Assert.That(empty.Status, Is.EqualTo(200));
            Assert.That(empty.Html, Does.Contain("0 pilots match missing"));
            Assert.That(empty.Html, Does.Contain("id=\"rankingEmpty\""));
            Assert.That(Resolve("q=missing&page=2").Status, Is.EqualTo(404));
            Assert.That(Resolve("q=Nova&pilot=Vortex-MMO").Status, Is.EqualTo(404));
            Assert.That(Resolve("q=Nova&company=mmo&pilot=Nova-EIC").Status, Is.EqualTo(404));
        });
    }

    [Test]
    public void Search_FilterTabsShowCountsForTheirDestination()
    {
        var all = Resolve("q=Nova");
        var mmo = Resolve("q=Nova&company=mmo");
        var empty = Resolve("q=missing&company=mmo");
        Assert.Multiple(() =>
        {
            Assert.That(all.Html, Does.Contain("category=honour&amp;company=all&amp;page=1&amp;q=Nova\" data-count=\"3\">Honour points (3)</a>"));
            Assert.That(all.Html, Does.Contain("company=all&amp;page=1&amp;q=Nova\" data-count=\"3\" aria-current=\"page\">All companies (3)</a>"));
            Assert.That(all.Html, Does.Contain("company=mmo&amp;page=1&amp;q=Nova\" data-count=\"1\">"));
            Assert.That(mmo.Html, Does.Contain("category=honour&amp;company=mmo&amp;page=1&amp;q=Nova\" data-count=\"1\">Honour points (1)</a>"));
            Assert.That(mmo.Html, Does.Contain("company=mmo&amp;page=1&amp;q=Nova\" data-count=\"1\" aria-current=\"page\">"));
            Assert.That(empty.Html, Does.Contain("company=all&amp;page=1&amp;q=missing\" data-count=\"0\">All companies (0)</a>"));
            Assert.That(empty.Html, Does.Contain("category=honour&amp;company=mmo&amp;page=1&amp;q=missing\" data-count=\"0\">Honour points (0)</a>"));
        });
    }

    [Test]
    public void UnfilteredTabsShowFullBoardCounts()
    {
        var result = Resolve("");
        Assert.Multiple(() =>
        {
            Assert.That(result.Html, Does.Contain("category=honour&amp;company=all&amp;page=1\" data-count=\"36\">Honour points (36)</a>"));
            Assert.That(result.Html, Does.Contain("company=mmo&amp;page=1\" data-count=\"12\">"));
        });
    }

    [TestCase("q=")]
    [TestCase("q=x")]
    [TestCase("q=Nova&q=Vortex")]
    [TestCase("Q=Nova")]
    [TestCase("q=Nova%20MMO")]
    [TestCase("q=%3Cscript%3E")]
    [TestCase("q=abcdefghijklmnopqrstuvwxyz1234567")]
    public void Search_RejectsMalformedInputWithoutEcho(string query)
    {
        var result = Resolve(query);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400));
            Assert.That(result.Html, Does.Not.Contain("<script>"));
            Assert.That(result.Html, Does.Not.Contain("Nova MMO"));
        });
    }

    private static LocalRankings.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalHallofFame&" + query);
        return LocalRankings.Resolve(context.Request.Query,
            new GameAuthSession { Username = "Pilot", GameUserId = 7, Company = "mmo", Dosid = "d" });
    }
}
