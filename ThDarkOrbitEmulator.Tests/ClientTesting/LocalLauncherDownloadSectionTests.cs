using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexEs;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLauncherDownloadSectionTests
{
    private static IQueryCollection Query(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return context.Request.Query;
    }

    private static LocalLauncherDownload.Result Resolve(string queryString) =>
        LocalLauncherDownload.Resolve(Query(queryString));

    [Test]
    public void SectionIdsAreUniqueLowercaseAndStartWithOverview()
    {
        var ids = LocalLauncherDownload.Sections.Select(section => section.Id).ToList();
        Assert.That(ids[0], Is.EqualTo("overview"));
        Assert.That(ids, Is.Unique);
        Assert.That(ids, Has.All.Matches<string>(id => id.All(c => c is >= 'a' and <= 'z')));
    }

    [Test]
    public void OverviewSectionIsTheDefaultPage()
    {
        var result = Resolve("?action=externalDownload&section=overview");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Is.EqualTo(LocalLauncherDownload.Page));
        Assert.That(result.Html, Does.Contain("data-section=\"overview\""));
    }

    [TestCase("requirements", "id=\"launcherRequirements\"")]
    [TestCase("connection", "id=\"launcherClientPaths\"")]
    [TestCase("troubleshooting", "id=\"launcherTroubleshooting\"")]
    public void EachSectionRendersItsOwnContent(string section, string marker)
    {
        var result = Resolve($"?action=externalDownload&section={section}&from=internalStart");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain($"data-section=\"{section}\""));
        Assert.That(result.Html, Does.Contain(marker));
        Assert.That(result.Html, Does.Not.Contain("Already have the client?"));
        Assert.That(result.Html, Does.Contain("href=\"/\""));
        Assert.That(result.Html, Does.Not.Contain(".exe"));
    }

    [Test]
    public void EverySectionLinksToAllSectionsAndMarksOnlyTheCurrentOne()
    {
        foreach (var current in LocalLauncherDownload.Sections)
        {
            var html = Resolve($"?action=externalDownload&section={current.Id}").Html;
            foreach (var section in LocalLauncherDownload.Sections)
            {
                var href = LocalLauncherDownload.SectionHref(section.Id).Replace("&", "&amp;");
                var link = $"<a href=\"{href}\"" + (section == current ? " aria-current=\"page\"" : string.Empty) + ">";
                Assert.That(html, Does.Contain(link), $"{current.Id} -> {section.Id}");
            }
            Assert.That(html.Split("aria-current=\"page\"").Length - 1, Is.EqualTo(1));
        }
    }

    [Test]
    public void SectionLinksAreCanonicalAndResolve()
    {
        Assert.That(LocalLauncherDownload.SectionHref("overview"), Is.EqualTo("/index.es?action=externalDownload"));
        foreach (var section in LocalLauncherDownload.Sections)
        {
            var href = LocalLauncherDownload.SectionHref(section.Id);
            var query = Query(href[href.IndexOf('?')..]);
            Assert.That(LocalLauncherDownload.Targets(query), Is.True);
            Assert.That(LocalLauncherDownload.Resolve(query).Status, Is.EqualTo(StatusCodes.Status200OK));
        }
    }

    [Test]
    public void ConnectionListsCapturedPathsAndEveryCatalogueServer()
    {
        var html = Resolve("?action=externalDownload&section=connection").Html;
        foreach (var path in LocalLauncherDownload.CapturedClientPaths)
            Assert.That(html, Does.Contain($"<code>{path}</code>"));
        foreach (var map in LocalMapsXml.Maps)
        {
            Assert.That(html, Does.Contain($"data-server=\"{map.GameServerIp}\""));
            Assert.That(html, Does.Contain(map.Id));
        }
        var servers = LocalMapsXml.Maps.Select(map => map.GameServerIp).Distinct().Count();
        Assert.That(html.Split("data-server=").Length - 1, Is.EqualTo(servers));
        Assert.That(html, Does.Contain("action=internalMapOverview"));
    }

    [Test]
    public void TroubleshootingLinksToSupportAndHelp()
    {
        var html = Resolve("?action=externalDownload&section=troubleshooting").Html;
        Assert.That(html, Does.Contain("action=internalSupport"));
        Assert.That(html, Does.Contain("action=internalHelp"));
    }

    [TestCase("Connection")]
    [TestCase("CONNECTION")]
    [TestCase("install")]
    [TestCase("connection ")]
    [TestCase("..%2fconnection")]
    public void UnknownOrNonCanonicalSectionReturns404(string section)
    {
        var result = Resolve($"?action=externalDownload&section={section}");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Contain("id=\"localLauncherDownloadError\""));
        Assert.That(result.Html, Does.Contain("Unknown section"));
    }

    [TestCase("?action=externalDownload&section=", "must not be empty")]
    [TestCase("?action=externalDownload&section=connection&section=requirements", "appear once")]
    [TestCase("?action=externalDownload&Section=connection", "Unknown parameter")]
    [TestCase("?action=externalDownload&section=conn%0aection", "printable")]
    public void MalformedSectionReturns400(string queryString, string message)
    {
        var result = Resolve(queryString);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain(message));
    }

    [Test]
    public void UnknownSectionIsNotEchoedIntoTheErrorPage()
    {
        var result = Resolve("?action=externalDownload&section=%3Cscript%3E");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Not.Contain("<script>"));
        Assert.That(result.Html, Does.Not.Contain("&lt;script&gt;"));
    }

    [Test]
    public void WrongActionStillWinsOverAValidSection()
    {
        var result = Resolve("?action=EXTERNALDOWNLOAD&section=connection");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Invalid download action"));
    }
}
