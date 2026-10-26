using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMapOverviewValidationTests
{
    [TestCase("", 200)]
    [TestCase("map=", 200)]
    [TestCase("map=1", 200)]
    [TestCase("map=5", 200)]
    [TestCase("map=9", 200)]
    [TestCase("from=internalStart&prc=100&lang=en", 200)]
    [TestCase("MAP=5", 400)]
    [TestCase("map=2", 404)]
    [TestCase("map=9999", 404)]
    [TestCase("map=01", 400)]
    [TestCase("map=0", 400)]
    [TestCase("map=-1", 400)]
    [TestCase("map=1.0", 400)]
    [TestCase("map=10000", 400)]
    [TestCase("map=%EF%BC%91", 400)]
    [TestCase("map=1%20", 400)]
    [TestCase("map=1-1", 400)]
    [TestCase("map=%3Cscript%3E", 400)]
    [TestCase("map=1&map=5", 400)]
    [TestCase("lang=en&lang=es", 400)]
    [TestCase("page=2", 400)]
    [TestCase("server=127.0.0.1", 400)]
    public void Query_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve("action=internalMapOverview&" + query, "eic");
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.Multiple(() =>
            {
                Assert.That(result.Html, Does.Contain($"Map overview request failed ({status})"));
                Assert.That(result.Html, Does.Not.Contain("<script>"));
                Assert.That(result.Html, Does.Contain("action=internalMapOverview"));
                Assert.That(result.Html, Does.Not.Contain("localMapOverview"));
            });
    }

    [Test]
    public void OverlongAndUnknownValues_AreRejectedWithoutEcho()
    {
        var value = new string('7', 65);
        var overlong = Resolve("action=internalMapOverview&lang=" + value, "mmo");
        Assert.Multiple(() =>
        {
            Assert.That(overlong.Status, Is.EqualTo(400));
            Assert.That(overlong.Html, Does.Not.Contain(value));
            Assert.That(Resolve("action=internalMapOverview&secret=x", "mmo").Html, Does.Not.Contain("secret"));
            Assert.That(Resolve("action=internalMapOverview&map=12345", "mmo").Html, Does.Not.Contain("12345"));
        });
    }

    [Test]
    public void Unauthorized_LinksToSignInWithoutMapData()
    {
        var result = LocalMapOverview.Unauthorized();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(401));
            Assert.That(result.Html, Does.Contain("<a href=\"/\">Sign in</a>"));
            Assert.That(result.Html, Does.Not.Contain("127.0.0.1"));
            Assert.That(result.Html, Does.Not.Contain("Map ID"));
        });
    }

    [TestCase("mmo", "1", "1-1")]
    [TestCase("eic", "5", "2-1")]
    [TestCase("vru", "9", "3-1")]
    [TestCase("tst", "13", "4-1")]
    public void Overview_MarksOnlyTheCompanyStarterMap(string company, string starterMap, string name)
    {
        var html = Resolve("action=internalMapOverview", company).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"starter map ID {starterMap} ({name})"));
            Assert.That(html, Does.Contain($"class=\"card current\" id=\"map-{starterMap}\""));
            Assert.That(CountOf(html, "card current"), Is.EqualTo(1));
            Assert.That(CountOf(html, "Your starter map"), Is.EqualTo(1));
            foreach (var (id, mapName) in new[] { ("1", "1-1"), ("5", "2-1"), ("9", "3-1"), ("13", "4-1") })
            {
                Assert.That(html, Does.Contain($"Map ID {id} · {mapName}"));
                Assert.That(html, Does.Contain($"action=internalMapOverview&amp;map={id}\">Details"));
            }
        });
    }

    [Test]
    public void Detail_ShowsOnlySelectedMapWithHomeAndStarterCompanies()
    {
        var html = Resolve("action=internalMapOverview&map=1", "vru").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Map ID 1 · 1-1"));
            Assert.That(html, Does.Not.Contain("Map ID 5"));
            Assert.That(html, Does.Not.Contain("Map ID 9"));
            Assert.That(html, Does.Contain("Home company: Mars Mining Operations TEST"));
            Assert.That(html, Does.Contain("<li>Mars Mining Operations TEST</li>"));
            Assert.That(html, Does.Not.Contain("<li>Test Company TEST</li>"));
            Assert.That(html, Does.Not.Contain("<li>Venus Resources Unlimited TEST</li>"));
            Assert.That(html, Does.Not.Contain("Your starter map"));
            Assert.That(html, Does.Contain("action=internalMapOverview\">All maps"));
            Assert.That(html, Does.Contain("Local game server: 127.0.0.1"));
            Assert.That(html, Does.Not.Contain("enter the world"));
        });

        var own = Resolve("action=internalMapOverview&map=9", "vru").Html;
        Assert.That(own, Does.Contain("Your starter map"));
        Assert.That(own, Does.Contain("Home company: Venus Resources Unlimited TEST"));
    }

    [Test]
    public void MapName_FallsBackForMapsWithoutHomeCompany()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalMapOverview.MapName("5"), Is.EqualTo("2-1"));
            Assert.That(LocalMapOverview.MapName("42"), Is.EqualTo("Map 42"));
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
