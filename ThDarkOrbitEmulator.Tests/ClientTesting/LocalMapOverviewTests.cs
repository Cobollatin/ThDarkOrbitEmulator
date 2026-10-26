using System.Xml.Linq;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMapOverviewTests
{
    [Test]
    public void PageAndClientXmlShareStarterMapsAndLocalServer()
    {
        var xml = XDocument.Parse(LocalMapsXml.Xml);
        var wireMaps = xml.Root!.Elements("map").Select(element => (
            Id: (string?)element.Attribute("ID"),
            Server: (string?)element.Attribute("GameServerIP"))).ToArray();

        Assert.That(wireMaps.Select(map => map.Id), Is.EqualTo(new[] { "1", "5", "9", "13" }));
        Assert.That(wireMaps.All(map => map.Server == "127.0.0.1"), Is.True);
        foreach (var company in CompanyCatalog.Companies)
        {
            var page = LocalMapOverview.Page(new GameAuthSession { Company = company.Code });
            Assert.That(page, Does.Contain($"starter map ID {company.StarterMap}"));
            foreach (var map in wireMaps)
                Assert.That(page, Does.Contain($"Map ID {map.Id}"));
            Assert.That(page, Does.Contain("action=internalWebGL&amp;from=internalStart"));
            Assert.That(page, Does.Not.Contain("enter the world"));
        }

        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalMapOverview"));
    }
}
