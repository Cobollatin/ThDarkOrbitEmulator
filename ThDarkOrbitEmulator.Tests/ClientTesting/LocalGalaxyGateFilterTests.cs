using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-145: Galaxy Gates status filter (all, activated, ready, incomplete).
public class LocalGalaxyGateFilterTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    // alpha activated, beta ready, gamma/delta/epsilon incomplete.
    private static LocalGalaxyGateState State(int energy = 50) => new(energy, LocalGalaxyGates.Catalog.Select(g => g.Id switch
    {
        "alpha" => g with { PartsOwned = 34, Activated = true },
        "beta" => g with { PartsOwned = 48 },
        "gamma" => g with { PartsOwned = 41 },
        _ => g
    }).ToList());

    private static string[] CardIds(string page) =>
        System.Text.RegularExpressions.Regex.Matches(page, "<article data-gate=\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();

    private static string Detail(string page)
    {
        var start = page.IndexOf("<section id=\"gate-detail\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "detail panel missing");
        return page[start..(page.IndexOf("</section>", start, StringComparison.Ordinal) + 10)];
    }

    [TestCase("?action=internalGalaxyGates&filter=all", true)]
    [TestCase("?action=internalGalaxyGates&filter=ready&gate=beta", true)]
    [TestCase("?action=internalGalaxyGates&filter=unknown", true)]
    [TestCase("?action=internalGalaxyGates&filter=", false)]
    [TestCase("?action=internalGalaxyGates&filter=Ready", false)]
    [TestCase("?action=internalGalaxyGates&filter=no-energy", false)]
    [TestCase("?action=internalGalaxyGates&filter=ready%0A", false)]
    [TestCase("?action=internalGalaxyGates&filter=ready&filter=all", false)]
    [TestCase("?action=internalGalaxyGates&Filter=ready", false)]
    [TestCase("?action=internalGalaxyGates&filter=abcdefghijklmnopq", false)]
    [TestCase("?ACTION=internalGalaxyGates&filter=ready", false)]
    [TestCase("?action=InternalGalaxyGates&filter=ready", false)]
    [TestCase("?action=internalGalaxyGates&From=internalStart", false)]
    public void PageQuery_ValidatesFilterShape(string query, bool valid) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(valid));

    [TestCase(null, true)]
    [TestCase("all", true)]
    [TestCase("activated", true)]
    [TestCase("ready", true)]
    [TestCase("incomplete", true)]
    [TestCase("claimed", false)]
    public void IsFilter_KnowsOnlyDefinedFilters(string? filter, bool known) =>
        Assert.That(LocalGalaxyGates.IsFilter(filter), Is.EqualTo(known));

    [TestCase(null, new[] { "alpha", "beta", "gamma", "delta", "epsilon" })]
    [TestCase("all", new[] { "alpha", "beta", "gamma", "delta", "epsilon" })]
    [TestCase("activated", new[] { "alpha" })]
    [TestCase("ready", new[] { "beta" })]
    [TestCase("incomplete", new[] { "gamma", "delta", "epsilon" })]
    public void Page_ListsOnlyMatchingGates(string? filter, string[] expected) =>
        Assert.That(CardIds(LocalGalaxyGates.Page(Session, State(), filter: filter)), Is.EqualTo(expected));

    [Test]
    public void Incomplete_IncludesGatesWithoutEnergy() =>
        Assert.That(CardIds(LocalGalaxyGates.Page(Session, State(energy: 0), filter: "incomplete")),
            Is.EqualTo(new[] { "gamma", "delta", "epsilon" }));

    [Test]
    public void Tabs_ShowCountsOverAllGatesAndMarkCurrent()
    {
        var page = LocalGalaxyGates.Page(Session, State(), filter: "ready");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-filter=\"all\" data-count=\"5\">All (5)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"activated\" data-count=\"1\">Activated (1)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"ready\" data-count=\"1\" aria-current=\"page\">Ready to activate (1)</a>"));
            Assert.That(page, Does.Contain("data-filter=\"incomplete\" data-count=\"3\">Incomplete (3)</a>"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates\" data-filter=\"all\""));
        });
    }

    [Test]
    public void Page_WithoutFilter_MarksAllCurrentAndKeepsPlainLinks()
    {
        var page = LocalGalaxyGates.Page(Session, State());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-filter=\"all\" data-count=\"5\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates&amp;gate=alpha\""));
            Assert.That(page, Does.Not.Contain("gate-empty"));
        });
    }

    [Test]
    public void EmptyFilter_ShowsNotice()
    {
        var fresh = new LocalGalaxyGateState(250, [.. LocalGalaxyGates.Catalog]);
        var page = LocalGalaxyGates.Page(Session, fresh, filter: "activated");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<p id=\"gate-empty\">No gates match this filter.</p>"));
            Assert.That(CardIds(page), Is.Empty);
            Assert.That(page, Does.Contain("data-filter=\"activated\" data-count=\"0\" aria-current=\"page\""));
        });
    }

    [TestCase("beta", "ready", true)]
    [TestCase("alpha", "ready", false)]
    [TestCase("alpha", "activated", true)]
    [TestCase("gamma", "incomplete", true)]
    [TestCase("beta", "incomplete", false)]
    [TestCase("gamma", "all", true)]
    [TestCase("zeta", null, false)]
    [TestCase(null, "ready", true)]
    public void HasGate_RespectsFilter(string? gate, string? filter, bool exists) =>
        Assert.That(LocalGalaxyGates.HasGate(State(), gate, filter), Is.EqualTo(exists));

    [Test]
    public void Detail_NavigatesWithinFilterAndKeepsIt()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(), "delta", filter: "incomplete"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Gate 2 of 3"));
            Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates&amp;gate=gamma&amp;filter=incomplete\" rel=\"prev\""));
            Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates&amp;gate=epsilon&amp;filter=incomplete\" rel=\"next\""));
            Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates&amp;filter=incomplete\">Close</a>"));
        });
    }

    [Test]
    public void Detail_SingleGateFilter_HasNoPreviousOrNext()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(), "beta", filter: "ready"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Gate 1 of 1"));
            Assert.That(detail, Does.Not.Contain("rel=\"prev\""));
            Assert.That(detail, Does.Not.Contain("rel=\"next\""));
            Assert.That(detail, Does.Contain("internalGalaxyGateActivate"));
        });
    }

    [Test]
    public void FilteredCards_LinkToFilteredDetail() =>
        Assert.That(LocalGalaxyGates.Page(Session, State(), filter: "activated"),
            Does.Contain("<a href=\"/indexInternal.es?action=internalGalaxyGates&amp;gate=alpha&amp;filter=activated\">Alpha</a>"));

    [Test]
    public void AllFilter_KeepsUnfilteredLinks()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(), "alpha", filter: "all"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Gate 1 of 5"));
            Assert.That(detail, Does.Not.Contain("filter="));
        });
    }

    [Test]
    public void FilteredForms_CarryFilterInCardsAndDetail()
    {
        var incomplete = LocalGalaxyGates.Page(Session, State(), filter: "incomplete");
        var readyDetail = Detail(LocalGalaxyGates.Page(Session, State(), "beta", filter: "ready"));
        Assert.Multiple(() =>
        {
            Assert.That(System.Text.RegularExpressions.Regex.Matches(incomplete,
                "name=\"filter\" value=\"incomplete\"").Count, Is.EqualTo(4)); // Three action forms and the search form.
            Assert.That(readyDetail, Does.Contain("name=\"filter\" value=\"ready\""));
            Assert.That(LocalGalaxyGates.Page(Session, State()), Does.Not.Contain("name=\"filter\""));
        });
    }
}
