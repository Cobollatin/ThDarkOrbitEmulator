using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMapOverviewNavigationTests
{
    [TestCase("lang=e%00n")]
    [TestCase("from=internal%0AStart")]
    [TestCase("prc=1%0D")]
    [TestCase("map=%091")]
    [TestCase("lang=%7F")]
    public void ControlCharacters_AreRejectedWithoutEcho(string query)
    {
        var result = Resolve("action=internalMapOverview&" + query, "mmo");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400));
            Assert.That(result.Html, Does.Contain("contains control characters"));
            Assert.That(result.Html, Does.Not.Contain("localMapOverview"));
            Assert.That(result.Html, Does.Not.Contain("internal\nStart"));
        });
    }

    [Test]
    public void Overview_ShowsMapCountAndDataMarkers()
    {
        var html = Resolve("action=internalMapOverview", "eic").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<title>Map and server - DarkOrbit local emulator</title>"));
            Assert.That(html, Does.Contain("<p id=\"mapCount\">4 maps in the local catalogue</p>"));
            foreach (var id in new[] { "1", "5", "9", "13" })
                Assert.That(html, Does.Contain($"id=\"map-{id}\" data-map=\"{id}\""));
            Assert.That(html, Does.Not.Contain("class=\"mapNav\""));
            Assert.That(html, Does.Not.Contain("Open Hangar"));
        });
    }

    [TestCase("1", "1-1", null, "2-1", "5")]
    [TestCase("5", "2-1", "1-1", "3-1", "9")]
    [TestCase("9", "3-1", "2-1", "4-1", "13")]
    [TestCase("13", "4-1", "3-1", null, null)]
    public void Detail_HasTitleMarkerAndNeighbourLinks(string id, string name, string? previous, string? next, string? nextId)
    {
        var html = Resolve("action=internalMapOverview&map=" + id, "mmo").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"<title>Map {name} (ID {id}) - DarkOrbit local emulator</title>"));
            Assert.That(html, Does.Contain($"data-map=\"{id}\""));
            Assert.That(CountOf(html, "data-map="), Is.EqualTo(1));
            Assert.That(html, Does.Not.Contain("id=\"mapCount\""));
            if (previous is null)
                Assert.That(html, Does.Not.Contain("rel=\"prev\""));
            else
                Assert.That(html, Does.Contain($"\">Previous: {previous}</a>"));
            if (next is null)
                Assert.That(html, Does.Not.Contain("rel=\"next\""));
            else
                Assert.That(html, Does.Contain(
                    $"rel=\"next\" href=\"/indexInternal.es?action=internalMapOverview&amp;map={nextId}\">Next: {next}</a>"));
        });
    }

    [Test]
    public void NeighbourLinks_WalkTheWholeCatalogueInOrder()
    {
        var visited = new List<string>();
        string? id = "1";
        while (id is not null && visited.Count < 10)
        {
            visited.Add(id);
            var html = Resolve("action=internalMapOverview&map=" + id, "vru").Html;
            var marker = "rel=\"next\" href=\"/indexInternal.es?action=internalMapOverview&amp;map=";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            id = start < 0 ? null : html[(start + marker.Length)..html.IndexOf('"', start + marker.Length)];
        }
        Assert.That(visited, Is.EqualTo(new[] { "1", "5", "9", "13" }));
    }

    [TestCase("mmo", "1", true)]
    [TestCase("tst", "1", false)]
    [TestCase("tst", "13", true)]
    [TestCase("eic", "1", false)]
    [TestCase("eic", "5", true)]
    [TestCase("vru", "5", false)]
    [TestCase("vru", "9", true)]
    public void Detail_LinksToHangarOnlyOnOwnStarterMap(string company, string id, bool expected)
    {
        var html = Resolve("action=internalMapOverview&map=" + id, company).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html.Contains("action=internalDock\">Open Hangar</a>", StringComparison.Ordinal), Is.EqualTo(expected));
            Assert.That(html, Does.Not.Contain("enter the world"));
            Assert.That(html, Does.Not.Contain("internalMapEnter"));
        });
    }

    [Test]
    public void Detail_WithoutCompanyHasNoHangarLinkButKeepsNavigation()
    {
        var html = Resolve("action=internalMapOverview&map=5", "").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("Open Hangar"));
            Assert.That(html, Does.Not.Contain("id=\"currentMap\""));
            Assert.That(html, Does.Contain("Previous: 1-1"));
            Assert.That(html, Does.Contain("Next: 3-1"));
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
