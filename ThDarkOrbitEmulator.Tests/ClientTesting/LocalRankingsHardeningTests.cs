using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalRankingsHardeningTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", GameUserId = 7, Company = "eic", Dosid = "d" };

    // The dispatcher routes `action` case-insensitively; key casing and action spelling must be exact here.
    [TestCase("ACTION=internalHallofFame")]
    [TestCase("Action=internalHallofFame")]
    [TestCase("action=internalhalloffame")]
    [TestCase("action=INTERNALHALLOFFAME")]
    [TestCase("action=internalHallofFame&ACTION=internalHallofFame")]
    [TestCase("action=internalHallofFame&CATEGORY=honour")]
    [TestCase("action=internalHallofFame&Company=eic")]
    [TestCase("action=internalHallofFame&PAGE=2")]
    [TestCase("action=internalHallofFame&Prc=1")]
    public void NonCanonicalKeysOrAction_Return400(string query) => AssertStatus(query, 400);

    [TestCase("page=01")]
    [TestCase("page=0002")]
    [TestCase("page=00")]
    [TestCase("page=+1")]
    [TestCase("page=%201")]
    [TestCase("page=")]
    public void NonCanonicalPage_Returns400(string query) => AssertStatus("action=internalHallofFame&" + query, 400);

    [Test]
    public void EmptyCategory_Returns400_WhileOmittedCategoryKeepsDefault()
    {
        AssertStatus("action=internalHallofFame&category=", 400);
        AssertStatus("action=internalHallofFame", 200);
    }

    [TestCase("from=%0Aevil")]
    [TestCase("prc=a%00b")]
    [TestCase("category=honour%09")]
    public void ControlCharacters_Return400(string query) => AssertStatus("action=internalHallofFame&" + query, 400);

    [Test]
    public void ValueLength_IsBounded()
    {
        var max = new string('a', LocalRankings.MaxValueLength);
        AssertStatus($"action=internalHallofFame&from={max}&prc={max}", 200);
        AssertStatus($"action=internalHallofFame&from={max}a", 400);
        AssertStatus($"action=internalHallofFame&prc={max}a", 400);
        AssertStatus($"action=internalHallofFame&company={max}a", 400);
    }

    [TestCase("action=internalHallofFame&from=internalStart&prc=100")]
    [TestCase("action=internalHallofFame&category=aliens&company=vru&page=2&from=x&prc=y")]
    [TestCase("action=internalHallofFame&page=1")]
    public void CanonicalNavigation_Returns200(string query) => AssertStatus(query, 200);

    [Test]
    public void UnknownParameterName_IsNotReflected()
    {
        var result = Resolve("action=internalHallofFame&%3Cscript%3E=1");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400));
            Assert.That(result.Html, Does.Not.Contain("<script>"));
            Assert.That(result.Html, Does.Not.Contain("script"));
        });
    }

    [Test]
    public void Unauthorized_IsHtml401WithReturnLink()
    {
        var result = LocalRankings.Unauthorized();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(401));
            Assert.That(result.Html, Does.Contain("Hall of Fame request failed (401)"));
            Assert.That(result.Html, Does.Contain("session is missing or expired"));
            Assert.That(result.Html, Does.Contain("action=internalHallofFame"));
        });
    }

    [Test]
    public void CompanyFilter_MarksSelectedLinkCurrent()
    {
        var all = Resolve("action=internalHallofFame&category=honour").Html;
        var vru = Resolve("action=internalHallofFame&category=honour&company=vru").Html;
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("company=all&amp;page=1\" data-count=\"36\" aria-current=\"page\">All companies (36)</a>"));
            Assert.That(all, Does.Not.Contain("company=vru&amp;page=1\" data-count=\"12\" aria-current"));
            Assert.That(vru, Does.Contain("company=vru&amp;page=1\" data-count=\"12\" aria-current=\"page\">"));
            Assert.That(vru, Does.Not.Contain("aria-current=\"page\">All companies"));
            Assert.That(vru, Does.Contain("Honour points · "));
        });
    }

    [Test]
    public void ErrorsAndPages_NeverChangeBoards()
    {
        var category = LocalRankings.Categories[0];
        var before = LocalRankings.Board(category, null);
        Resolve("action=internalHallofFame&page=99");
        Resolve("ACTION=internalHallofFame");
        Assert.That(LocalRankings.Board(category, null), Is.EqualTo(before));
    }

    private static void AssertStatus(string query, int status)
    {
        var result = Resolve(query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.That(result.Html, Does.Contain($"Hall of Fame request failed ({status})"));
    }

    private static LocalRankings.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalRankings.Resolve(context.Request.Query, Session);
    }
}
