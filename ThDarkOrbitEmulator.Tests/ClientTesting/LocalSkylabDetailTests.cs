using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSkylabDetailTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid-1" };

    private static LocalSkylabState State(int prometium = 1, int prometiumStock = 500, int endurium = 1, int terbium = 1) =>
        new(Start, [
            new("prometium", "Prometium", prometium, prometiumStock),
            new("endurium", "Endurium", endurium, 500),
            new("terbium", "Terbium", terbium, 500)
        ]);

    private static bool Valid(string extra) =>
        LocalSkylabAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery("?action=internalSkylab" + extra)));

    [TestCase("&detail=prometium")]
    [TestCase("&detail=terbium&lang=es")]
    [TestCase("&detail=unknown")]
    public void PageQuery_AcceptsLowercaseDetail(string extra) => Assert.That(Valid(extra), Is.True);

    [TestCase("&detail=")]
    [TestCase("&DETAIL=prometium")]
    [TestCase("&Detail=prometium")]
    [TestCase("&detail=Prometium")]
    [TestCase("&detail=prometium%20")]
    [TestCase("&detail=prometium1")]
    [TestCase("&detail=prom-ethium")]
    [TestCase("&detail=prometium&detail=endurium")]
    [TestCase("&detail=%00")]
    [TestCase("&collector=prometium")]
    public void PageQuery_RejectsMalformedDetail(string extra) => Assert.That(Valid(extra), Is.False);

    [Test]
    public void PageQuery_RejectsOverlongDetail() =>
        Assert.That(Valid("&detail=" + new string('a', 33)), Is.False);

    [TestCase("prometium", true)]
    [TestCase("endurium", true)]
    [TestCase("terbium", true)]
    [TestCase("seprom", false)]
    [TestCase("Prometium", false)]
    public void IsCollector_MatchesCatalogExactly(string id, bool expected) =>
        Assert.That(LocalSkylab.IsCollector(id), Is.EqualTo(expected));

    [TestCase(1, 4500)]
    [TestCase(5, 3500)]
    [TestCase(9, 900)]
    [TestCase(10, 0)]
    public void CostToMax_SumsRemainingUpgrades(int level, long expected) =>
        Assert.That(LocalSkylab.CostToMax(level), Is.EqualTo(expected));

    [TestCase(1, 500, 0)]
    [TestCase(1, 99, 1)]
    [TestCase(1, 0, 10)]
    [TestCase(3, 0, 10)]
    [TestCase(3, 299, 1)]
    [TestCase(10, 0, 0)]
    public void MinutesUntilAffordable_RoundsUp(int level, int stock, long expected) =>
        Assert.That(LocalSkylab.MinutesUntilAffordable(new("prometium", "Prometium", level, stock)), Is.EqualTo(expected));

    [Test]
    public void Page_ShowsProductionSummary()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 3), Start);
        Assert.That(page, Does.Contain("id=\"skylabSummary\""));
        Assert.That(page, Does.Contain("Total output: 50 units/minute."));
        Assert.That(page, Does.Contain("25 upgrade(s) left, costing 13200 units in total."));
    }

    [Test]
    public void Page_SummaryReportsAllMaxed()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 10, endurium: 10, terbium: 10), Start);
        Assert.That(page, Does.Contain("Total output: 300 units/minute. All collectors are at the maximum level."));
    }

    [Test]
    public void Page_LinksEachCollectorToItsDetails()
    {
        var page = LocalSkylab.Page(Session, State(), Start);
        foreach (var id in new[] { "prometium", "endurium", "terbium" })
            Assert.That(page, Does.Contain($"href=\"/indexInternal.es?action=internalSkylab&amp;detail={id}\""));
        Assert.That(page, Does.Not.Contain("id=\"skylabDetail\""));
    }

    [Test]
    public void Detail_ShowsCollectorFigures()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 2, prometiumStock: 150), Start.AddMinutes(3), "prometium");
        Assert.That(page, Does.Contain("data-collector=\"prometium\""));
        Assert.That(page, Does.Contain("Collector 1 of 3"));
        Assert.That(page, Does.Contain("<li>Level 2/10</li>"));
        Assert.That(page, Does.Contain("<li>Output: 20 units/minute</li>"));
        Assert.That(page, Does.Contain("<li>Stock: 150</li>"));
        Assert.That(page, Does.Contain("<li>Pending: 60 units</li>"));
        Assert.That(page, Does.Contain("Full storage (60 minutes): 1200 units"));
        Assert.That(page, Does.Contain("Cost to maximum level: 4400 units"));
    }

    [Test]
    public void Detail_ExplainsShortfallAndDisablesUpgrade()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 2, prometiumStock: 150), Start, "prometium");
        Assert.That(page, Does.Contain("Next upgrade: level 3 for 200 Prometium, raising output to 30 units/minute."));
        Assert.That(page, Does.Contain("50 more units needed: about 3 minute(s) of production."));
        var section = page[page.IndexOf("id=\"skylabDetail\"", StringComparison.Ordinal)..];
        Assert.That(section, Does.Contain("Upgrade (200 Prometium)</button>"));
        Assert.That(section, Does.Contain(" disabled>Upgrade"));
    }

    [Test]
    public void Detail_FlagsWaitsLongerThanOneCollection()
    {
        var page = LocalSkylab.Page(Session, State(prometium: 9, prometiumStock: 0), Start, "prometium");
        Assert.That(page, Does.Contain("900 more units needed: about 10 minute(s) of production."));
        var slow = LocalSkylab.Page(Session, State(prometium: 1, prometiumStock: 0), Start, "prometium");
        Assert.That(slow, Does.Contain("about 10 minute(s)"));
        Assert.That(LocalSkylab.MinutesUntilAffordable(new("x", "X", 1, -600)), Is.GreaterThan(LocalSkylab.MaxCollectMinutes));
        var negative = LocalSkylab.Page(Session, new LocalSkylabState(Start, [new("prometium", "Prometium", 1, -600)]), Start, "prometium");
        Assert.That(negative, Does.Contain("more than one 60-minute collection."));
    }

    [Test]
    public void Detail_AffordableUpgradeHasSignedEnabledForm()
    {
        var page = LocalSkylab.Page(Session, State(), Start, "endurium");
        var section = page[page.IndexOf("id=\"skylabDetail\"", StringComparison.Ordinal)..];
        Assert.That(section, Does.Contain("Stock covers it now."));
        Assert.That(section, Does.Contain("name=\"collector\" value=\"endurium\""));
        Assert.That(section, Does.Contain("name=\"level\" value=\"1\""));
        Assert.That(section, Does.Contain("<button type=\"submit\">Upgrade (100 Endurium)</button>"));
    }

    [Test]
    public void Detail_MaxLevelHasNoForm()
    {
        var page = LocalSkylab.Page(Session, State(terbium: 10), Start, "terbium");
        var section = page[page.IndexOf("id=\"skylabDetail\"", StringComparison.Ordinal)..];
        Assert.That(section, Does.Contain("Maximum level reached. No further upgrades."));
        Assert.That(section, Does.Contain("Cost to maximum level: 0 units"));
        Assert.That(section[..section.IndexOf("</section>", StringComparison.Ordinal)],
            Does.Not.Contain("internalSkylabUpgrade"));
    }

    [TestCase("prometium", false, true)]
    [TestCase("endurium", true, true)]
    [TestCase("terbium", true, false)]
    public void Detail_NavigatesBetweenCollectors(string id, bool previous, bool next)
    {
        var page = LocalSkylab.Page(Session, State(), Start, id);
        Assert.That(page.Contains("id=\"skylabDetailPrevious\""), Is.EqualTo(previous));
        Assert.That(page.Contains("id=\"skylabDetailNext\""), Is.EqualTo(next));
        Assert.That(page, Does.Contain("id=\"skylabDetailClose\" href=\"/indexInternal.es?action=internalSkylab\""));
    }

    [Test]
    public void Detail_UnknownCollectorRendersNoPanel()
    {
        var page = LocalSkylab.Page(Session, State(), Start, "seprom");
        Assert.That(page, Does.Not.Contain("id=\"skylabDetail\""));
        Assert.That(page, Does.Not.Contain("seprom"));
    }
}
