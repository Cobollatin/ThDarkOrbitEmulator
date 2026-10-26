using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMainMenuShellTests
{
    private static readonly string[] SessionMenuActions =
    [
        "internalDock", "internalShop", "internalAssembly", "internalSkylab", "internalTechFactory", "internalAuction",
        "internalGalaxyGates", "internalEvents", "internalSpaceSearch", "internalQuests",
        "internalPilotSheet", "internalClan", "internalMessages", "internalHallofFame", "internalLogbook",
        "internalCommandCenter", "internalMapOverview", "internalForum", "internalHelp", "internalSupport",
        "internalAccount", "internalPayment", "internalVouchers", "externalLogout"
    ];

    [Test]
    public void Shell_LinksEveryLocalMenuExactlyOnce()
    {
        var page = LocalCompanySelection.GamePage("mmo", "pilot", 7);
        Assert.Multiple(() =>
        {
            foreach (var action in SessionMenuActions)
                Assert.That(Regex.Matches(page, Regex.Escape($"href=\"/indexInternal.es?action={action}\"")).Count,
                    Is.EqualTo(1), action);
            Assert.That(Regex.Matches(page, Regex.Escape("href=\"/index.es?action=externalDownload\"")).Count, Is.EqualTo(1));
            var navLinks = Regex.Matches(page, "<nav id=\"mainMenu\".*</nav>", RegexOptions.Singleline).Single().Value;
            Assert.That(Regex.Matches(navLinks, "<a href=").Count, Is.EqualTo(SessionMenuActions.Length + 1));
            Assert.That(LocalCompanySelection.MenuLinkCount, Is.EqualTo(SessionMenuActions.Length + 1));
            Assert.That(page, Does.Contain($"id=\"mainMenu\" data-count=\"{LocalCompanySelection.MenuLinkCount}\""));
        });
    }

    [Test]
    public void Shell_GroupsMenusIntoSectionsWithCounts()
    {
        var page = LocalCompanySelection.GamePage("eic");
        Assert.Multiple(() =>
        {
            Assert.That(LocalCompanySelection.MenuSections.Select(s => s.Id),
                Is.EqualTo(new[] { "hangar", "activities", "pilot", "info", "account" }));
            foreach (var section in LocalCompanySelection.MenuSections)
            {
                Assert.That(page, Does.Contain($"id=\"menu_{section.Id}\" data-count=\"{section.Links.Count}\"><h2>{section.Title}</h2>"));
                Assert.That(section.Links, Is.Not.Empty);
            }
            Assert.That(LocalCompanySelection.MenuSections.SelectMany(s => s.Links).Select(l => l.Href), Is.Unique);
            Assert.That(LocalCompanySelection.MenuSections[^1].Links[^1].Label, Is.EqualTo("Log out"));
        });
    }

    [TestCase("mmo", "MMO", "1-1")]
    [TestCase("eic", "EIC", "2-1")]
    [TestCase("vru", "VRU", "3-1")]
    [TestCase("tst", "TST", "4-1")]
    public void PilotCard_ShowsStoredCompanyAndHomeMap(string company, string code, string mapName)
    {
        var definition = CompanyCatalog.Find(company)!;
        var page = LocalCompanySelection.GamePage(company, "Ace", 42);
        Assert.Multiple(() =>
        {
            Assert.That(mapName, Is.EqualTo($"{definition.NumericFactionId}-1"));
            Assert.That(page, Does.Contain($"id=\"pilotCard\" data-company=\"{company}\""));
            Assert.That(page, Does.Contain("id=\"pilotName\">Ace</h2>"));
            Assert.That(page, Does.Contain("id=\"pilotGameId\">42</dd>"));
            Assert.That(page, Does.Contain($"id=\"pilotCompany\">{code}</dd>"));
            Assert.That(page, Does.Contain(
                $"href=\"/indexInternal.es?action=internalMapOverview&amp;map={definition.StarterMap}\">{mapName} (map {definition.StarterMap})</a>"));
            Assert.That(LocalMapsXml.Maps.Select(m => m.Id), Does.Contain(definition.StarterMap));
        });
    }

    [Test]
    public void PilotCard_EncodesPilotName()
    {
        var page = LocalCompanySelection.GamePage("mmo", "<b>x</b>&\"", 7);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"pilotName\">&lt;b&gt;x&lt;/b&gt;&amp;&quot;</h2>"));
            Assert.That(page, Does.Not.Contain("<b>x</b>"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void PilotCard_FallsBackForBlankName(string? name)
    {
        var page = LocalCompanySelection.GamePage("vru", name, 0);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"pilotName\">Pilot</h2>"));
            Assert.That(page, Does.Not.Contain("pilotGameId"), "a non-positive game ID is not shown");
        });
    }

    [Test]
    public void Shell_KeepsLegacyCompanyNameAndRootIds()
    {
        var page = LocalCompanySelection.GamePage("mmo");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<main id=\"game\">"));
            Assert.That(page, Does.Contain($"id=\"companyName\">{LocalCompanySelection.DisplayName("mmo")}</p>"));
            Assert.That(page, Does.Not.Contain("&from=").And.Not.Contain("internalCompanyConfirm"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Is.EqualTo(page), "rendering is deterministic");
        });
    }
}
