using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSupportSessionTests
{
    private static GameAuthSession Session(string? company = "eic", string username = "pilot") => new()
    {
        AccountId = Guid.NewGuid(),
        Username = username,
        GameUserId = 4711,
        Company = company,
        Dosid = "secret-dosid-value",
        CreatedAt = new DateTimeOffset(2026, 10, 24, 9, 5, 0, TimeSpan.FromHours(-5)),
        ExpiresAt = new DateTimeOffset(2026, 10, 24, 10, 5, 0, TimeSpan.FromHours(-5))
    };

    private static LocalLinksRequest.Result Resolve(string section, GameAuthSession session) =>
        LocalLinksRequest.Resolve(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["action"] = "internalSupport",
            ["section"] = section
        }), session, Page.Support);

    [Test]
    public void AccountSectionShowsPilotAndCompanyWithoutSessionTimes()
    {
        var html = Resolve("account", Session("vru")).Html;
        Assert.That(html, Does.Contain("id=\"supportSession\""));
        Assert.That(html, Does.Contain("<dt>Pilot</dt><dd>pilot</dd>"));
        Assert.That(html, Does.Contain("<dt>Pilot ID</dt><dd>4711</dd>"));
        Assert.That(html, Does.Not.Contain("Session expires"));
        Assert.That(html, Does.Not.Contain("Not chosen yet"));
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    public void CompanyUsesTheCatalogDisplayName(string company)
    {
        var name = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts
            .LocalCompanySelection.DisplayName(company);
        Assert.That(name, Is.Not.Null.And.Not.Empty);
        Assert.That(Resolve("account", Session(company)).Html,
            Does.Contain($"<dt>Company</dt><dd>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(name!)}</dd>"));
    }

    [Test]
    public void SecuritySectionShowsUtcSessionTimes()
    {
        var html = Resolve("security", Session()).Html;
        Assert.That(html, Does.Contain("<dt>Signed in since</dt><dd>2026-10-24 14:05 UTC</dd>"));
        Assert.That(html, Does.Contain("<dt>Session expires</dt><dd>2026-10-24 15:05 UTC</dd>"));
        Assert.That(html, Does.Contain("action=externalLogout"));
    }

    [TestCase("account")]
    [TestCase("security")]
    public void SessionDetailsNeverExposeTheDosidOrAccountId(string section)
    {
        var session = Session();
        var html = Resolve(section, session).Html;
        Assert.That(html, Does.Not.Contain(session.Dosid));
        Assert.That(html, Does.Not.Contain(session.AccountId.ToString()));
        Assert.That(html, Does.Not.Contain("dosid").IgnoreCase);
    }

    [Test]
    public void UsernameIsHtmlEncoded()
    {
        var html = Resolve("account", Session(username: "<b>pilot&co</b>")).Html;
        Assert.That(html, Does.Not.Contain("<b>pilot"));
        Assert.That(html, Does.Contain("&lt;b&gt;pilot&amp;co&lt;/b&gt;"));
    }

    [Test]
    public void PilotWithoutCompanyIsOfferedTheCompanyChoice()
    {
        var html = Resolve("account", Session(null)).Html;
        Assert.That(html, Does.Contain("Not chosen yet"));
        Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalCompanyChoose\""));
    }

    [Test]
    public void OtherSectionsAndTheIndexHaveNoSessionDetails()
    {
        foreach (var section in LocalSupport.Sections.Where(item => item.Id is not ("account" or "security")))
            Assert.That(Resolve(section.Id, Session()).Html, Does.Not.Contain("supportSession"), section.Id);
        Assert.That(LocalSupport.Page(null, Session()), Does.Not.Contain("supportSession"));
        Assert.That(LocalSupport.Page("account"), Does.Not.Contain("supportSession"));
    }

    [Test]
    public void InvalidCompanyIsStillRejectedBeforeSessionDetailsRender()
    {
        var result = Resolve("account", Session("xyz"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(result.Html, Does.Not.Contain("supportSession"));
        Assert.That(result.Html, Does.Not.Contain("4711"));
    }
}
