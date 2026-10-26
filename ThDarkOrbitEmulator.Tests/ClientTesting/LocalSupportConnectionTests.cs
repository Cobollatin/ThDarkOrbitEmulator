using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-103: the Support connection section checks the signed-in session, company, and starter map server.
public class LocalSupportConnectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 0, 0, TimeSpan.Zero);

    private static GameAuthSession Session(string? company = "eic", TimeSpan? remaining = null) => new()
    {
        AccountId = Guid.NewGuid(),
        Username = "pilot",
        GameUserId = 4711,
        Company = company,
        Dosid = "secret-dosid-value",
        CreatedAt = Now.AddMinutes(-30),
        ExpiresAt = Now + (remaining ?? TimeSpan.FromMinutes(30))
    };

    private static LocalLinksRequest.Result Resolve(string? section, GameAuthSession? session)
    {
        var query = new Dictionary<string, StringValues> { ["action"] = "internalSupport" };
        if (section is not null)
            query["section"] = section;
        return LocalLinksRequest.Resolve(new QueryCollection(query), session, Page.Support, Now);
    }

    private static string Check(string html, string check)
    {
        var start = html.IndexOf($"<li data-check=\"{check}\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), check);
        return html[start..(html.IndexOf("</li>", start, StringComparison.Ordinal) + 5)];
    }

    [Test]
    public void ConnectionSectionListsThreeChecksInOrder()
    {
        var result = Resolve("connection", Session());

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        var html = result.Html;
        Assert.That(html, Does.Contain("<ul id=\"supportConnectionChecks\">"));
        var session = html.IndexOf("data-check=\"session\"", StringComparison.Ordinal);
        var company = html.IndexOf("data-check=\"company\"", StringComparison.Ordinal);
        var server = html.IndexOf("data-check=\"game-server\"", StringComparison.Ordinal);
        Assert.That(session, Is.LessThan(company));
        Assert.That(company, Is.LessThan(server));
    }

    [Test]
    public void ActiveSessionReportsRemainingMinutesAndUtcExpiry()
    {
        var check = Check(Resolve("connection", Session(remaining: TimeSpan.FromMinutes(30))).Html, "session");

        Assert.That(check, Does.Contain("data-state=\"ok\""));
        Assert.That(check, Does.Contain("expires in 30 minutes (2026-10-24 14:30 UTC)"));
        Assert.That(check, Does.Not.Contain("Sign in again soon"));
    }

    [TestCase(10 * 60, "expires in 10 minutes")]
    [TestCase(61, "expires in 2 minutes")]
    [TestCase(30, "expires in 1 minute (")]
    public void SessionsNearExpiryWarnToSignInAgain(int seconds, string expected)
    {
        var check = Check(Resolve("connection", Session(remaining: TimeSpan.FromSeconds(seconds))).Html, "session");

        Assert.That(check, Does.Contain("data-state=\"warning\""));
        Assert.That(check, Does.Contain(expected));
        Assert.That(check, Does.Contain("Sign in again soon."));
    }

    [Test]
    public void SessionJustOverTheWarningWindowIsOk()
    {
        var check = Check(Resolve("connection", Session(remaining: LocalSupport.ExpiryWarning + TimeSpan.FromSeconds(1))).Html, "session");

        Assert.That(check, Does.Contain("data-state=\"ok\""));
        Assert.That(check, Does.Contain("expires in 11 minutes"));
    }

    [TestCase(0)]
    [TestCase(-120)]
    public void ExpiredSessionAsksToSignInAgain(int seconds)
    {
        var check = Check(Resolve("connection", Session(remaining: TimeSpan.FromSeconds(seconds))).Html, "session");

        Assert.That(check, Does.Contain("data-state=\"action\""));
        Assert.That(check, Does.Contain("<a href=\"/\">Sign in again</a>"));
        Assert.That(check, Does.Not.Contain("expires in"));
    }

    [TestCaseSource(typeof(CompanyCatalog), nameof(CompanyCatalog.Companies))]
    public void EveryCompanyGetsItsStarterMapAndCatalogueServer(CompanyDefinition company)
    {
        var html = Resolve("connection", Session(company.Code)).Html;
        var map = LocalMapsXml.Maps.Single(entry => entry.Id == company.StarterMap);

        var companyCheck = Check(html, "company");
        Assert.That(companyCheck, Does.Contain("data-state=\"ok\""));
        Assert.That(companyCheck, Does.Contain($"Company: {company.DisplayName}"));
        var server = Check(html, "game-server");
        Assert.That(server, Does.Contain("data-state=\"ok\""));
        Assert.That(server, Does.Contain($"Starter map {LocalMapOverview.MapName(map.Id)} (ID {map.Id}) on local game server {map.GameServerIp}"));
        Assert.That(server, Does.Contain($"href=\"/indexInternal.es?action=internalMapOverview&amp;map={map.Id}\""));
    }

    [Test]
    public void MapDetailsLinkResolvesOnTheMapMenu()
    {
        var session = Session("vru");
        var map = CompanyCatalog.Find("vru")!.StarterMap;
        var result = LocalMapOverview.Resolve(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["action"] = "internalMapOverview",
            ["map"] = map
        }), session);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public void PilotWithoutCompanyIsAskedToChooseOne()
    {
        var html = Resolve("connection", Session(null)).Html;

        var company = Check(html, "company");
        Assert.That(company, Does.Contain("data-state=\"action\""));
        Assert.That(company, Does.Contain("href=\"/indexInternal.es?action=internalCompanyChoose\""));
        var server = Check(html, "game-server");
        Assert.That(server, Does.Contain("data-state=\"action\""));
        Assert.That(server, Does.Not.Contain("internalMapOverview"));
    }

    [Test]
    public void UnknownCompanyIsRejectedBeforeAnyChecksRender()
    {
        var result = Resolve("connection", Session("xyz"));

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(result.Html, Does.Not.Contain("supportConnectionChecks"));
        Assert.That(result.Html, Does.Not.Contain("xyz"));
    }

    [Test]
    public void MissingSessionIsUnauthorizedWithoutChecks()
    {
        var result = Resolve("connection", null);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(result.Html, Does.Not.Contain("supportConnectionChecks"));
    }

    [TestCase(null)]
    [TestCase("getting-started")]
    [TestCase("account")]
    [TestCase("security")]
    [TestCase("client-links")]
    public void ChecksOnlyAppearOnTheConnectionSection(string? section)
    {
        var result = Resolve(section, Session());

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Not.Contain("supportConnectionChecks"));
    }

    [Test]
    public void ChecksNeverExposeTheDosidOrAccountIdAndAreDeterministic()
    {
        var session = Session();
        var first = Resolve("connection", session).Html;

        Assert.That(first, Does.Not.Contain(session.Dosid));
        Assert.That(first, Does.Not.Contain(session.AccountId.ToString()));
        Assert.That(Resolve("connection", session).Html, Is.EqualTo(first));
    }

    [Test]
    public void PageWithoutSessionKeepsStaticConnectionGuidance()
    {
        var html = LocalSupport.Page("connection");

        Assert.That(html, Does.Contain("Connection help"));
        Assert.That(html, Does.Not.Contain("supportConnectionChecks"));
    }
}
