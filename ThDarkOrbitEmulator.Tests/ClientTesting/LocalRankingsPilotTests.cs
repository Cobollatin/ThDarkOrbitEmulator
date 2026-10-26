using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalRankingsPilotTests
{
    private static readonly GameAuthSession Session = new() { Username = "<pilot>", GameUserId = 7, Company = "mmo", Dosid = "d" };

    [Test]
    public void PilotNames_AreUniqueAndCoverEveryRankedCompany()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalRankings.PilotNames, Has.Count.EqualTo(LocalRankings.PilotsPerCompany * 3));
            Assert.That(LocalRankings.PilotNames, Is.Unique);
            Assert.That(LocalRankings.PilotNames.Any(name => name.EndsWith("-TST", StringComparison.Ordinal)), Is.False);
            Assert.That(LocalRankings.PilotNames.All(name => name.Length <= LocalRankings.MaxPilotLength), Is.True);
        });
    }

    [Test]
    public void Standings_MatchBoardsForEveryPilotAndCategory()
    {
        foreach (var pilot in LocalRankings.PilotNames)
        {
            var standings = LocalRankings.Standings(pilot)!;
            Assert.That(standings, Has.Count.EqualTo(LocalRankings.Categories.Count));
            foreach (var standing in standings)
            {
                var global = LocalRankings.Board(standing.Category, null).Single(entry => entry.Pilot == pilot);
                var own = LocalRankings.Board(standing.Category, global.Company).Single(entry => entry.Pilot == pilot);
                Assert.Multiple(() =>
                {
                    Assert.That(standing.GlobalPosition, Is.EqualTo(global.Position));
                    Assert.That(standing.CompanyPosition, Is.EqualTo(own.Position));
                    Assert.That(standing.CompanyPosition, Is.LessThanOrEqualTo(standing.GlobalPosition));
                    Assert.That(standing.Score, Is.EqualTo(global.Score).And.GreaterThanOrEqualTo(1));
                    Assert.That(own.Level, Is.EqualTo(global.Level));
                    Assert.That(standing.Level, Is.EqualTo(own.Level));
                    Assert.That(standing.Level, Is.GreaterThanOrEqualTo(1));
                });
            }
        }
    }

    [TestCase("nova-mmo")]
    [TestCase("Nova-TST")]
    [TestCase("Nova")]
    [TestCase("<pilot>")]
    public void Standings_UnknownPilot_IsNull(string pilot) => Assert.That(LocalRankings.Standings(pilot), Is.Null);

    [Test]
    public void CompanyStandings_AreSortedTotalsOfTheBoard()
    {
        foreach (var category in LocalRankings.Categories)
        {
            var board = LocalRankings.Board(category, null);
            var standings = LocalRankings.CompanyStandings(category);
            Assert.That(standings.Select(item => item.Company), Is.EquivalentTo(new[] { "mmo", "eic", "vru" }));
            Assert.That(standings.Select(item => item.Position), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(standings.Select(item => item.TotalScore), Is.Ordered.Descending);
            foreach (var standing in standings)
            {
                var entries = board.Where(entry => entry.Company == standing.Company).ToArray();
                Assert.That(standing.TotalScore, Is.EqualTo(entries.Sum(entry => entry.Score)));
                Assert.That(standing.TopPilot, Is.EqualTo(entries[0].Pilot));
            }
        }
    }

    [Test]
    public void Board_LinksEveryPilotAndShowsCompanyStandings()
    {
        var result = Resolve("action=internalHallofFame&category=honour&company=eic&page=2");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain(
                "href=\"/indexInternal.es?action=internalHallofFame&amp;category=honour&amp;company=eic&amp;page=2&amp;pilot=Draco-EIC\""));
            Assert.That(result.Html, Does.Contain("id=\"rankingCompanyStandings\""));
            Assert.That(result.Html, Does.Contain("data-company=\"eic\" class=\"own\""));
            Assert.That(result.Html, Does.Contain("data-company=\"mmo\">"));
            Assert.That(result.Html, Does.Not.Contain("data-company=\"tst\""));
        });
    }

    [Test]
    public void AllCompaniesBoard_HighlightsNoCompanyStanding()
    {
        var result = Resolve("action=internalHallofFame");
        Assert.That(result.Html, Does.Contain("id=\"rankingCompanyStandings\""));
        Assert.That(result.Html, Does.Not.Contain("class=\"own\"><td>"));
    }

    [Test]
    public void PilotView_ShowsAllCategoriesAndKeepsBackContext()
    {
        var result = Resolve("action=internalHallofFame&category=aliens&company=vru&page=2&pilot=Orion-VRU");
        var standings = LocalRankings.Standings("Orion-VRU")!;
        var aliens = standings.Single(item => item.Category.Code == "aliens");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("id=\"localHallOfFamePilot\""));
            Assert.That(result.Html, Does.Contain("<h1>Orion-VRU</h1>"));
            Assert.That(result.Html, Does.Contain("<p id=\"rankingPilotCompany\">Venus Resources Unlimited TEST · emulator-defined pilot</p>"));
            foreach (var category in LocalRankings.Categories)
                Assert.That(result.Html, Does.Contain($"data-category=\"{category.Code}\""));
            Assert.That(result.Html, Does.Contain("data-category=\"aliens\" class=\"current\""));
            Assert.That(result.Html, Does.Contain($"<td>#{aliens.GlobalPosition}</td><td>#{aliens.CompanyPosition}</td>"));
            Assert.That(result.Html, Does.Contain(
                "href=\"/indexInternal.es?action=internalHallofFame&amp;category=aliens&amp;company=vru&amp;page=2\">Back to ranking"));
            Assert.That(result.Html, Does.Contain("action=internalWebGL&amp;from=internalStart"));
            Assert.That(result.Html, Does.Not.Contain("<pilot>"));
        });
    }

    [Test]
    public void PilotView_WorksFromAnotherCompanyFilter()
    {
        // Direct profile links can use a different company filter, but must explain the mismatch.
        var result = Resolve("action=internalHallofFame&company=mmo&pilot=Nova-EIC");
        Assert.That(result.Status, Is.EqualTo(200));
        Assert.That(result.Html, Does.Contain("<h1>Nova-EIC</h1>"));
        Assert.That(result.Html, Does.Contain("outside the selected company"));
        Assert.That(result.Html, Does.Contain("company=eic&amp;page=1\">View their company ranking"));
    }

    [Test]
    public void PilotView_IdentifiesThePageContainingThePilot()
    {
        var result = Resolve("action=internalHallofFame&category=honour&company=eic&page=1&pilot=Draco-EIC");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Position #12 on the selected board"));
            Assert.That(result.Html, Does.Contain("company=eic&amp;page=2\">View on page 2"));
            Assert.That(result.Html, Does.Contain("company=eic&amp;page=1\">Back to ranking"));
        });
    }

    [TestCase("pilot=")]
    [TestCase("pilot=Nova%20MMO")]
    [TestCase("pilot=Nova_MMO")]
    [TestCase("pilot=%3Cscript%3E")]
    [TestCase("pilot=N%C3%B6va-MMO")]
    [TestCase("pilot=Nova-MMO&pilot=Nova-EIC")]
    [TestCase("PILOT=Nova-MMO")]
    [TestCase("pilot=%0ANova-MMO")]
    public void InvalidPilot_Returns400(string query) => AssertStatus("action=internalHallofFame&" + query, 400);

    [Test]
    public void OverlongPilot_Returns400()
    {
        AssertStatus("action=internalHallofFame&pilot=" + new string('A', LocalRankings.MaxPilotLength + 1), 400);
        AssertStatus("action=internalHallofFame&pilot=" + new string('A', LocalRankings.MaxPilotLength), 404);
    }

    [TestCase("pilot=nova-mmo")]
    [TestCase("pilot=Nova-TST")]
    [TestCase("pilot=Nova")]
    [TestCase("pilot=Ghost-MMO")]
    public void UnknownPilot_Returns404(string query) => AssertStatus("action=internalHallofFame&" + query, 404);

    [TestCase("category=unknown&pilot=Nova-MMO", 404)]
    [TestCase("company=tst&pilot=Nova-MMO", 400)]
    [TestCase("page=9&pilot=Nova-MMO", 404)]
    [TestCase("page=01&pilot=Nova-MMO", 400)]
    public void PilotView_StillValidatesBoardContext(string query, int status) =>
        AssertStatus("action=internalHallofFame&" + query, status);

    [Test]
    public void PilotView_IsDeterministic()
    {
        const string query = "action=internalHallofFame&category=rankpoints&pilot=Helix-MMO";
        var first = Resolve(query).Html;
        Assert.That(Resolve(query).Html, Is.EqualTo(first));
    }

    private static void AssertStatus(string query, int status) => Assert.That(Resolve(query).Status, Is.EqualTo(status), query);

    private static LocalRankings.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalRankings.Resolve(context.Request.Query, Session);
    }
}
