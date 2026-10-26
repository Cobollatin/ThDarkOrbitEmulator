using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-183: list pages show the score needed to enter the selected board.
public class LocalRankingsEntryThresholdTests
{
    [Test]
    public void Threshold_IsOneAboveLastListedPilot_ForEveryCategoryAndFilter()
    {
        foreach (var category in LocalRankings.Categories)
            foreach (var company in new string?[] { null, "mmo", "eic", "vru" })
            {
                var board = LocalRankings.Board(category, company);
                var (last, needed, page) = LocalRankings.EntryThreshold(category, company);
                var panel = Panel(Resolve($"category={category.Code}&company={company ?? "all"}").Html);
                Assert.Multiple(() =>
                {
                    Assert.That(last, Is.EqualTo(board[^1]));
                    Assert.That(board.All(entry => entry.Score >= last.Score), Is.True);
                    Assert.That(needed, Is.EqualTo(last.Score + 1));
                    Assert.That(page, Is.EqualTo((board.Count + LocalRankings.PageSize - 1) / LocalRankings.PageSize));
                    Assert.That(panel, Does.Contain($"data-entry-score=\"{needed}\""));
                    Assert.That(panel, Does.Contain($"data-entry-position=\"{last.Position}\""));
                    Assert.That(panel, Does.Contain(needed.ToString("N0", CultureInfo.InvariantCulture) + " " + category.Title));
                    Assert.That(panel, Does.Contain($"#{last.Position} {last.Pilot} with "));
                    Assert.That(panel, Does.Contain(
                        $"category={category.Code}&amp;company={company ?? "all"}&amp;page={page}\">View the last page"));
                });
            }
    }

    [Test]
    public void Threshold_LinkedPageIsServedAndListsTheLastPilot()
    {
        var (last, _, page) = LocalRankings.EntryThreshold(LocalRankings.Categories[1], "eic");
        var result = Resolve($"category=honour&company=eic&page={page}");
        Assert.That(result.Status, Is.EqualTo(200));
        Assert.That(result.Html, Does.Contain($"pilot={last.Pilot}\">{last.Pilot}</a>"));
    }

    [Test]
    public void Threshold_IgnoresPageAndSearch()
    {
        var expected = Panel(Resolve("category=aliens&company=vru").Html);
        Assert.Multiple(() =>
        {
            Assert.That(Panel(Resolve("category=aliens&company=vru&page=2").Html), Is.EqualTo(expected));
            Assert.That(Panel(Resolve("category=aliens&company=vru&q=Nova").Html), Is.EqualTo(expected));
            Assert.That(Panel(Resolve("category=aliens&company=vru&q=zzzz").Html), Is.EqualTo(expected));
        });
    }

    [Test]
    public void Threshold_ChangesWithCompanyFilter()
    {
        var all = LocalRankings.EntryThreshold(LocalRankings.Categories[0], null);
        var mmo = LocalRankings.EntryThreshold(LocalRankings.Categories[0], "mmo");
        Assert.That(mmo.Last.Company, Is.EqualTo("mmo"));
        Assert.That(mmo.Needed, Is.GreaterThanOrEqualTo(all.Needed));
    }

    [Test]
    public void Threshold_IsNotShownOnPilotDetailOrErrors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Resolve("pilot=Nova-MMO").Html, Does.Not.Contain("ownRankingThreshold"));
            Assert.That(Resolve("category=unknown").Html, Does.Not.Contain("ownRankingThreshold"));
            Assert.That(Resolve("page=99").Html, Does.Not.Contain("ownRankingThreshold"));
        });
    }

    private static string Panel(string html)
    {
        var match = Regex.Match(html, "<p id=\"ownRankingThreshold\".*?</p>");
        Assert.That(match.Success, Is.True, "entry threshold panel missing");
        Assert.That(Regex.Matches(html, "id=\"ownRankingThreshold\"").Count, Is.EqualTo(1));
        return match.Value;
    }

    private static LocalRankings.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalHallofFame&" + query);
        var session = new GameAuthSession { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" };
        return LocalRankings.Resolve(context.Request.Query, session);
    }
}
