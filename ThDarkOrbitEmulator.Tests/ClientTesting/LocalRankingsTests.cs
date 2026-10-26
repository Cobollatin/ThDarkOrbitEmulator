using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalRankingsTests
{
    private static readonly GameAuthSession Session = new() { Username = "<pilot>", GameUserId = 7, Company = "mmo", Dosid = "d" };

    [Test]
    public void DefaultPage_ShowsExperienceBoardAndOwnUnrankedRow()
    {
        var result = Resolve("action=internalHallofFame");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Experience points · All companies · page 1 of 4"));
            Assert.That(result.Html, Does.Contain("&lt;pilot&gt; is unranked"));
            Assert.That(result.Html, Does.Not.Contain("<pilot>"));
            Assert.That(result.Html, Does.Contain("action=internalWebGL&amp;from=internalStart"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalHallofFame"));
        });
    }

    [Test]
    public void Boards_AreSortedPositiveAndCompanyFiltered()
    {
        foreach (var category in LocalRankings.Categories)
        {
            var all = LocalRankings.Board(category, null);
            Assert.That(all, Has.Count.EqualTo(3 * LocalRankings.PilotsPerCompany));
            Assert.That(all.Select(entry => entry.Score), Is.Ordered.Descending);
            Assert.That(all.All(entry => entry.Score > 0 && entry.Level >= 1), Is.True);
            Assert.That(all.Select(entry => entry.Position), Is.EqualTo(Enumerable.Range(1, all.Count)));
            Assert.That(all.Select(entry => entry.Company).Distinct(), Is.EquivalentTo(new[] { "mmo", "eic", "vru" }));
            var eic = LocalRankings.Board(category, "eic");
            Assert.That(eic, Has.Count.EqualTo(LocalRankings.PilotsPerCompany));
            Assert.That(eic.All(entry => entry.Company == "eic"), Is.True);
            Assert.That(eic[0].Position, Is.EqualTo(1));
        }
    }

    [Test]
    public void Board_IsDeterministic()
    {
        var category = LocalRankings.Categories[1];
        var firstBoard = LocalRankings.Board(category, null);
        var secondBoard = LocalRankings.Board(category, null);
        Assert.That(firstBoard, Is.EqualTo(secondBoard));
    }

    [TestCase("category=honour&company=vru", 200)]
    [TestCase("category=aliens&company=all&page=4", 200)]
    [TestCase("company=eic&page=2", 200)]
    [TestCase("category=unknown", 404)]
    [TestCase("page=5", 404)]
    [TestCase("company=eic&page=3", 404)]
    [TestCase("page=0", 400)]
    [TestCase("page=-1", 400)]
    [TestCase("page=99999", 400)]
    [TestCase("page=1.5", 400)]
    [TestCase("page=%EF%BC%91", 400)]
    [TestCase("company=tst", 400)]
    [TestCase("company=MMO", 400)]
    [TestCase("company=", 200)]
    [TestCase("page=1&page=2", 400)]
    [TestCase("category=honour&category=aliens", 400)]
    [TestCase("item=1", 400)]
    public void Query_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve("action=internalHallofFame&" + query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.That(result.Html, Does.Contain($"Hall of Fame request failed ({status})"));
    }

    [Test]
    public void Pagination_LinksStayWithinBounds()
    {
        var first = Resolve("action=internalHallofFame&category=rankpoints&company=mmo");
        var last = Resolve("action=internalHallofFame&category=rankpoints&company=mmo&page=2");
        Assert.Multiple(() =>
        {
            Assert.That(first.Html, Does.Contain("category=rankpoints&amp;company=mmo&amp;page=2\">Next"));
            Assert.That(first.Html, Does.Not.Contain(">Previous<"));
            Assert.That(last.Html, Does.Contain("page=1\">Previous"));
            Assert.That(last.Html, Does.Not.Contain(">Next<"));
            Assert.That(last.Html, Does.Contain("<td>12</td>"));
        });
    }

    private static LocalRankings.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalRankings.Resolve(context.Request.Query, Session);
    }
}

