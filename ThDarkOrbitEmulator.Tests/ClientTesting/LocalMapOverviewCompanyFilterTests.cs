using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMapOverviewCompanyFilterTests
{
    [TestCase("company=", 200)]
    [TestCase("company=mmo", 200)]
    [TestCase("company=eic", 200)]
    [TestCase("company=vru", 200)]
    [TestCase("company=tst", 200)]
    [TestCase("COMPANY=eic", 400)]
    [TestCase("company=eic&map=5", 200)]
    [TestCase("company=tst&map=13", 200)]
    [TestCase("company=abc", 404)]
    [TestCase("company=eic&map=1", 404)]
    [TestCase("company=mmo&map=9", 404)]
    [TestCase("company=abc&map=1", 404)]
    [TestCase("company=EIC", 400)]
    [TestCase("company=e1c", 400)]
    [TestCase("company=e-c", 400)]
    [TestCase("company=%20eic", 400)]
    [TestCase("company=abcdefghi", 400)]
    [TestCase("company=%C3%A9ic", 400)]
    [TestCase("company=%3Cscript%3E", 400)]
    [TestCase("company=eic&company=mmo", 400)]
    [TestCase("company=eic&map=01", 400)]
    public void CompanyFilter_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve("action=internalMapOverview&" + query, "mmo");
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.Multiple(() =>
            {
                Assert.That(result.Html, Does.Contain($"Map overview request failed ({status})"));
                Assert.That(result.Html, Does.Not.Contain("<script>"));
                Assert.That(result.Html, Does.Not.Contain("localMapOverview"));
                Assert.That(result.Html, Does.Not.Contain("abcdefghi"));
            });
    }

    [Test]
    public void MalformedCompany_IsRejectedBeforeSessionLookup()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalMapOverview&company=EIC");
        Assert.That(LocalMapOverview.Validate(context.Request.Query)?.Status, Is.EqualTo(400));
        context.Request.QueryString = new QueryString("?action=internalMapOverview&company=zzz");
        Assert.That(LocalMapOverview.Validate(context.Request.Query), Is.Null, "unknown but well-formed codes are 404 after the session");
    }

    [TestCase("mmo", "1", "1-1", "Mars Mining Operations TEST")]
    [TestCase("eic", "5", "2-1", "Earth Industries Corporation TEST")]
    [TestCase("vru", "9", "3-1", "Venus Resources Unlimited TEST")]
    [TestCase("tst", "13", "4-1", "Test Company TEST")]
    public void Filter_ListsOnlyThatCompanysStarterMap(string code, string map, string name, string displayName)
    {
        var html = Resolve($"action=internalMapOverview&company={code}", "vru").Html;
        Assert.Multiple(() =>
        {
            Assert.That(CountOf(html, "data-map=\""), Is.EqualTo(1));
            Assert.That(html, Does.Contain($"Map ID {map} · {name}"));
            Assert.That(html, Does.Contain($"1 map where {displayName} starts"));
            Assert.That(html, Does.Contain($"company={code}\" aria-current=\"page\">{code.ToUpperInvariant()} (1)"));
            Assert.That(CountOf(html, "aria-current"), Is.EqualTo(1));
        });
    }

    [Test]
    public void Overview_ShowsCompanyTabsAndStarterCounts()
    {
        var html = Resolve("action=internalMapOverview", "eic").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("action=internalMapOverview\" aria-current=\"page\">All (4)"));
            foreach (var code in new[] { "mmo", "eic", "vru", "tst" })
                Assert.That(html, Does.Contain($"company={code}\">{code.ToUpperInvariant()} (1)"));
            Assert.That(html, Does.Contain("4 maps in the local catalogue"));
            Assert.That(CountOf(html, "data-starters=\"1\""), Is.EqualTo(4));
        });
    }

    [Test]
    public void FilteredDetail_KeepsSessionStarterHighlightAndHasNoTabs()
    {
        var own = Resolve("action=internalMapOverview&company=eic&map=5", "eic").Html;
        var other = Resolve("action=internalMapOverview&company=tst&map=13", "eic").Html;
        Assert.Multiple(() =>
        {
            Assert.That(own, Does.Contain("Your starter map"));
            Assert.That(own, Does.Contain("action=internalDock\">Open Hangar"));
            Assert.That(own, Does.Not.Contain("companyTabs"));
            Assert.That(other, Does.Not.Contain("Your starter map"));
            Assert.That(other, Does.Contain("data-starters=\"1\""));
            Assert.That(other, Does.Contain("<li>Test Company TEST</li>"));
        });
    }

    [TestCase("mmo", "1")]
    [TestCase("eic", "5")]
    [TestCase("vru", "9")]
    [TestCase("tst", "13")]
    public void FilteredOverview_DetailAndBackStayWithinCompany(string company, string map)
    {
        var overview = Resolve($"action=internalMapOverview&company={company}", "eic").Html;
        var detail = Resolve($"action=internalMapOverview&company={company}&map={map}", "eic").Html;

        Assert.Multiple(() =>
        {
            Assert.That(overview, Does.Contain($"action=internalMapOverview&amp;map={map}&amp;company={company}\">Details"));
            Assert.That(detail, Does.Contain($"action=internalMapOverview&amp;company={company}\">Back to company maps"));
            Assert.That(detail, Does.Not.Contain("rel=\"prev\""));
            Assert.That(detail, Does.Not.Contain("rel=\"next\""));
            Assert.That(detail, Does.Not.Contain("action=internalMapOverview\">All maps"));
        });
    }

    private static int CountOf(string html, string value) =>
        (html.Length - html.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static LocalMapOverview.Result Resolve(string query, string company)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalMapOverview.Resolve(context.Request.Query, new GameAuthSession { Company = company });
    }
}
