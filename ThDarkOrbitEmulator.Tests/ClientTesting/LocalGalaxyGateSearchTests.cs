using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalGalaxyGateSearchTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static LocalGalaxyGateState State() => new(50, LocalGalaxyGates.Catalog.Select(g =>
        g.Id == "alpha" ? g with { PartsOwned = g.PartsRequired, Activated = true } : g).ToList());

    private static IQueryCollection Query(string url) => new QueryCollection(QueryHelpers.ParseQuery(url));

    [TestCase("?action=internalGalaxyGates&q=ALPHA", true)]
    [TestCase("?action=internalGalaxyGates&filter=activated&q=alpha", true)]
    [TestCase("?action=internalGalaxyGates&q=", true)]
    [TestCase("?action=internalGalaxyGates&q=%20%20", true)]
    [TestCase("?action=internalGalaxyGates&q=a", false)]
    [TestCase("?action=internalGalaxyGates&q=alpha&q=beta", false)]
    [TestCase("?action=internalGalaxyGates&Q=alpha", false)]
    [TestCase("?action=internalGalaxyGates&q=alpha%0A", false)]
    [TestCase("?action=internalGalaxyGates&q=%3Cscript%3E", false)]
    [TestCase("?action=internalGalaxyGates&q=alpha&gate=alpha", false)]
    public void Query_ValidatesSearch(string url, bool expected) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query(url)), Is.EqualTo(expected));

    [Test]
    public void Search_FiltersCardsAndKeepsTermAcrossTabs()
    {
        var page = LocalGalaxyGates.Page(Session, State(), filter: "incomplete", search: "BeTa");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<article data-gate=\"beta\""));
            Assert.That(page, Does.Not.Contain("<article data-gate=\"gamma\""));
            Assert.That(page, Does.Contain("data-filter=\"incomplete\" data-count=\"1\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("data-filter=\"activated\" data-count=\"0\""));
            Assert.That(page, Does.Contain("&amp;filter=activated&amp;q=BeTa"));
            Assert.That(page, Does.Contain("name=\"filter\" value=\"incomplete\""));
            Assert.That(page, Does.Contain("value=\"BeTa\""));
            Assert.That(page, Does.Contain("1 of 4 gates match"));
            Assert.That(page, Does.Contain("&amp;gate=beta&amp;filter=incomplete"));
            Assert.That(page, Does.Not.Contain("&amp;gate=beta&amp;filter=incomplete&amp;q="));
        });
    }

    [Test]
    public void NoMatches_ShowsEmptyStateAndClearSearch()
    {
        var page = LocalGalaxyGates.Page(Session, State(), filter: "activated", search: "beta");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("No gates match this search."));
            Assert.That(page, Does.Contain("0 of 1 gates match"));
            Assert.That(page, Does.Contain("action=internalGalaxyGates&amp;filter=activated\">Clear search"));
            Assert.That(page, Does.Not.Contain("<article data-gate="));
        });
    }

    [Test]
    public void BlankSearch_UsesFullList() =>
        Assert.That(LocalGalaxyGateAction.Search(Query("?action=internalGalaxyGates&q=%20%20")), Is.Null);
}
