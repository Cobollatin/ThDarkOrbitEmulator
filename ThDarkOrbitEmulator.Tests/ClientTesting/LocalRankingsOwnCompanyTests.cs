using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-102: the board shows the signed-in pilot's company position in the current category.
public class LocalRankingsOwnCompanyTests
{
    [Test]
    public void OwnCompany_MatchesCompanyStandingsForEveryCategoryAndCompany()
    {
        foreach (var category in LocalRankings.Categories)
        {
            var standings = LocalRankings.CompanyStandings(category);
            foreach (var standing in standings)
            {
                var html = Resolve(standing.Company, $"category={category.Code}").Html;
                var panel = OwnPanel(html);
                var expected = $"is #{standing.Position} of {standings.Count} in {category.Title} with "
                    + standing.TotalScore.ToString("N0", CultureInfo.InvariantCulture) + " points";
                Assert.Multiple(() =>
                {
                    Assert.That(panel, Does.Contain($"data-company=\"{standing.Company}\""));
                    Assert.That(panel, Does.Contain(expected));
                    Assert.That(panel, Does.Contain($"&amp;company={standing.Company}&amp;page=1&amp;pilot={standing.TopPilot}\""));
                    Assert.That(Regex.Matches(html, "class=\"ownCompanyTag\"").Count, Is.EqualTo(1));
                    Assert.That(Regex.IsMatch(html, $"<tr data-company=\"{standing.Company}\"><td>{standing.Position}</td><td>[^<]+ <span class=\"ownCompanyTag\">"), Is.True);
                });
            }
        }
    }

    [Test]
    public void OwnCompany_ShowsGapToNeighbour()
    {
        var category = LocalRankings.Categories[0];
        var standings = LocalRankings.CompanyStandings(category);
        var leader = OwnPanel(Resolve(standings[0].Company, "").Html);
        var second = OwnPanel(Resolve(standings[1].Company, "").Html);
        var gap = (standings[0].TotalScore - standings[1].TotalScore).ToString("N0", CultureInfo.InvariantCulture);
        Assert.Multiple(() =>
        {
            Assert.That(standings[0].TotalScore, Is.GreaterThanOrEqualTo(standings[1].TotalScore));
            Assert.That(leader, Does.Contain($", {gap} ahead of "));
            Assert.That(second, Does.Contain($", {gap} behind "));
        });
    }

    [Test]
    public void OwnCompany_IsIndependentOfFilterAndPage()
    {
        var all = OwnPanel(Resolve("eic", "category=honour").Html);
        Assert.Multiple(() =>
        {
            Assert.That(OwnPanel(Resolve("eic", "category=honour&company=vru").Html), Is.EqualTo(all));
            Assert.That(OwnPanel(Resolve("eic", "category=honour&company=all&page=2").Html), Is.EqualTo(all));
        });
    }

    [Test]
    public void OwnCompany_TestCompany_IsNotRanked()
    {
        var html = Resolve("tst", "").Html;
        Assert.Multiple(() =>
        {
            Assert.That(OwnPanel(html), Does.Contain("is the local test company and is not ranked."));
            Assert.That(OwnPanel(html), Does.Not.Contain("<a "));
            Assert.That(html, Does.Not.Contain("ownCompanyTag"));
        });
    }

    [TestCase(null)]
    [TestCase("MMO")]
    [TestCase("<b>")]
    public void OwnCompany_MissingOrUnknownCompany_IsNotRankedAndNotEchoed(string? company)
    {
        var result = Resolve(company, "");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(OwnPanel(result.Html), Is.EqualTo("<p id=\"ownCompanyRanking\" class=\"own\">Your company is not ranked.</p>"));
            Assert.That(result.Html, Does.Not.Contain("<b>"));
            Assert.That(result.Html, Does.Not.Contain("ownCompanyTag"));
        });
    }

    [Test]
    public void OwnCompany_IsNotShownOnPilotDetailOrErrors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Resolve("mmo", "pilot=Nova-MMO").Html, Does.Not.Contain("ownCompanyRanking"));
            Assert.That(Resolve("mmo", "category=unknown").Html, Does.Not.Contain("ownCompanyRanking"));
            Assert.That(Resolve("mmo", "page=99").Html, Does.Not.Contain("ownCompanyRanking"));
        });
    }

    private static string OwnPanel(string html)
    {
        var match = Regex.Match(html, "<p id=\"ownCompanyRanking\".*?</p>");
        Assert.That(match.Success, Is.True, "own company panel missing");
        return match.Value;
    }

    private static LocalRankings.Result Resolve(string? company, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalHallofFame" + (query.Length > 0 ? "&" + query : ""));
        var session = new GameAuthSession { Username = "<pilot>", GameUserId = 7, Company = company, Dosid = "d" };
        return LocalRankings.Resolve(context.Request.Query, session);
    }
}
