using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-143: emulator-defined Clan directory search (`q`).
public class LocalClanSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static GameAuthSession Session(string company = "mmo") =>
        new() { Dosid = "d", Username = "pilot", Company = company };

    private static bool ValidQuery(string query) => LocalClanAction.ValidPageQuery(
        new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)));

    private static string[] Rows(string page) =>
        System.Text.RegularExpressions.Regex.Matches(page, "<tr data-clan=\"([a-z-]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();

    [TestCase("?action=internalClan&q=Mars")]
    [TestCase("?action=internalClan&q=m")]
    [TestCase("?action=internalClan&q=Mars%20Guard")]
    [TestCase("?action=internalClan&q=mmo-guard")]
    [TestCase("?action=internalClan&q=abcdefghijklmnopqrstuvwxyzabcdef")]
    [TestCase("?action=internalClan&filter=recruiting&q=traders&detail=mmo-traders")]
    public void PageQuery_AcceptsWellFormedSearch(string query) => Assert.That(ValidQuery(query), Is.True, query);

    [TestCase("?action=internalClan&q=")]
    [TestCase("?action=internalClan&q=%20Mars")]
    [TestCase("?action=internalClan&q=Mars%20")]
    [TestCase("?action=internalClan&q=Mars&q=Orbit")]
    [TestCase("?action=internalClan&Q=Mars")]
    [TestCase("?action=internalClan&q=Mars.Guard")]
    [TestCase("?action=internalClan&q=M%C3%A4rs")]
    [TestCase("?action=internalClan&q=abcdefghijklmnopqrstuvwxyzabcdefg")]
    public void PageQuery_RejectsMalformedSearch(string query) => Assert.That(ValidQuery(query), Is.False, query);

    [TestCase("guard", new[] { "mmo-guard" })]
    [TestCase("MG", new[] { "mmo-guard" })]
    [TestCase("ELITE", new[] { "mmo-elite" })]
    [TestCase("cargo", new[] { "mmo-traders" })]
    [TestCase("mars", new[] { "mmo-guard", "mmo-orbit", "mmo-traders", "mmo-elite" })]
    public void Search_MatchesTagNameIdAndFocusCaseInsensitively(string search, string[] expected)
    {
        var page = LocalClan.Page(Session(), null, Now, search: search);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.EqualTo(expected));
            Assert.That(page, Does.Contain($"id=\"clan-search-result\">{expected.Length} "));
            Assert.That(page, Does.Contain("id=\"clan-clear-search\" href=\"/indexInternal.es?action=internalClan\""));
        });
    }

    [Test]
    public void Search_StaysWithinCompany()
    {
        var page = LocalClan.Page(Session("eic"), null, Now, search: "Mars");

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.Empty);
            Assert.That(page, Does.Contain("No clans match this search."));
            Assert.That(page, Does.Contain("0 clans match \"Mars\""));
        });
    }

    [Test]
    public void Search_CombinesWithFilterAndCountsTabs()
    {
        var page = LocalClan.Page(Session(), null, Now, filter: "recruiting", search: "Mars");

        Assert.Multiple(() =>
        {
            Assert.That(Rows(page), Is.EqualTo(new[] { "mmo-guard", "mmo-orbit", "mmo-traders" }));
            Assert.That(page, Does.Contain("data-filter=\"all\">All (4)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"closed\">Closed (1)</a>"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalClan&amp;filter=closed&amp;q=Mars\""));
            Assert.That(page, Does.Contain("<input type=\"hidden\" name=\"filter\" value=\"recruiting\">"));
            Assert.That(page, Does.Contain("id=\"clan-clear-search\" href=\"/indexInternal.es?action=internalClan&amp;filter=recruiting\""));
        });
    }

    [Test]
    public void Search_IsKeptInRowAndDetailNavigationLinks()
    {
        var page = LocalClan.Page(Session(), null, Now, detail: "mmo-orbit", search: "Mars T");

        Assert.Multiple(() =>
        {
            // "Mars T" matches only Mars Traders by name; Orbit is not in the results.
            Assert.That(Rows(page), Is.EqualTo(new[] { "mmo-traders" }));
            Assert.That(page, Does.Not.Contain("id=\"clan-detail\""));
            Assert.That(page, Does.Contain("&amp;q=Mars%20T&amp;detail=mmo-traders"));
        });
    }

    [Test]
    public void Detail_PositionAndPrevNextWalkSearchResults()
    {
        var page = LocalClan.Page(Session(), null, Now, detail: "mmo-orbit", search: "Orbit");
        var all = LocalClan.Page(Session(), null, Now, detail: "mmo-orbit");

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Clan 1 of 1"));
            Assert.That(page, Does.Not.Contain("rel=\"prev\""));
            Assert.That(page, Does.Not.Contain("rel=\"next\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalClan&amp;q=Orbit\">Close</a>"));
            // Share and rank still compare against every company clan.
            Assert.That(page, Does.Contain("id=\"clan-detail-rank\">3 of 4"));
            Assert.That(all, Does.Contain("Clan 2 of 4"));

            var traders = LocalClan.Page(Session(), null, Now, detail: "mmo-traders", search: "Mars");
            Assert.That(traders, Does.Contain("Clan 3 of 4"));
            Assert.That(traders, Does.Contain("href=\"/indexInternal.es?action=internalClan&amp;q=Mars&amp;detail=mmo-orbit\" rel=\"prev\""));
            Assert.That(traders, Does.Contain("href=\"/indexInternal.es?action=internalClan&amp;q=Mars&amp;detail=mmo-elite\" rel=\"next\""));
        });
    }

    [Test]
    public void HasDetail_RequiresSearchMatch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalClan.HasDetail("mmo", "mmo-guard", "guard"), Is.True);
            Assert.That(LocalClan.HasDetail("mmo", "mmo-guard", ""), Is.True);
            Assert.That(LocalClan.HasDetail("mmo", "mmo-guard", null), Is.True);
            Assert.That(LocalClan.HasDetail("mmo", "mmo-guard", "Elite"), Is.False);
            Assert.That(LocalClan.HasDetail("eic", "mmo-guard", "guard"), Is.False);
        });
    }

    [Test]
    public void NoSearch_ShowsEmptySearchFormWithoutResultLine()
    {
        var page = LocalClan.Page(Session(), null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<form id=\"clan-search\" method=\"get\" action=\"/indexInternal.es\">"));
            Assert.That(page, Does.Contain("name=\"q\" maxlength=\"32\""));
            Assert.That(page, Does.Contain("required value=\"\""));
            Assert.That(page, Does.Not.Contain("clan-search-result"));
            Assert.That(Rows(page), Has.Length.EqualTo(4));
        });
    }

    [Test]
    public void JoinFormsStillRenderForSearchResults()
    {
        var page = LocalClan.Page(Session(), null, Now, search: "Guard");

        Assert.That(page, Does.Contain("name=\"clan\" value=\"mmo-guard\""));
    }
}
