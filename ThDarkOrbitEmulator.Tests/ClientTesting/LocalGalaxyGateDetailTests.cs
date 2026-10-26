using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalGalaxyGateDetailTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static LocalGalaxyGateState State(int energy = 50) => new(energy, LocalGalaxyGates.Catalog.Select(g => g.Id switch
    {
        "alpha" => g with { PartsOwned = 34, Activated = true },
        "beta" => g with { PartsOwned = 48 },
        "gamma" => g with { PartsOwned = 41 },
        _ => g
    }).ToList());

    private static string Detail(string page)
    {
        var start = page.IndexOf("<section id=\"gate-detail\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "detail panel missing");
        return page[start..(page.IndexOf("</section>", start, StringComparison.Ordinal) + 10)];
    }

    [TestCase("?action=internalGalaxyGates&gate=alpha", true)]
    [TestCase("?action=internalGalaxyGates&gate=epsilon&lang=en", true)]
    [TestCase("?action=internalGalaxyGates&gate=unknown", true)]
    [TestCase("?action=internalGalaxyGates&gate=", false)]
    [TestCase("?action=internalGalaxyGates&gate=Alpha", false)]
    [TestCase("?action=internalGalaxyGates&gate=alpha1", false)]
    [TestCase("?action=internalGalaxyGates&gate=al%20pha", false)]
    [TestCase("?action=internalGalaxyGates&gate=alpha%0A", false)]
    [TestCase("?action=internalGalaxyGates&gate=%C3%A1lpha", false)]
    [TestCase("?action=internalGalaxyGates&Gate=alpha", false)]
    [TestCase("?action=internalGalaxyGates&gate=alpha&gate=beta", false)]
    public void PageQuery_ValidatesGateShape(string query, bool valid) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(valid));

    [Test]
    public void PageQuery_RejectsGateIdOverThirtyTwoLetters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalGalaxyGates&gate=" + new string('a', 32)))), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(
                "?action=internalGalaxyGates&gate=" + new string('a', 33)))), Is.False);
        });
    }

    [TestCase(null, true)]
    [TestCase("alpha", true)]
    [TestCase("epsilon", true)]
    [TestCase("zeta", false)]
    public void HasGate_MatchesCatalog(string? gate, bool exists) =>
        Assert.That(LocalGalaxyGates.HasGate(State(), gate), Is.EqualTo(exists));

    [Test]
    public void ListPage_LinksEveryGateWithoutDetailPanel()
    {
        var page = LocalGalaxyGates.Page(Session, State());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Not.Contain("id=\"gate-detail\""));
            foreach (var gate in LocalGalaxyGates.Catalog)
                Assert.That(page, Does.Contain($"href=\"/indexInternal.es?action=internalGalaxyGates&amp;gate={gate.Id}\""));
        });
    }

    [Test]
    public void SpinDetail_ShowsProgressForecastFormAndNavigation()
    {
        var page = LocalGalaxyGates.Page(Session, State(), "gamma");
        var detail = Detail(page);
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("<h2>Gamma gate</h2>"));
            Assert.That(detail, Does.Contain("Gate 3 of 5 · Id gamma"));
            Assert.That(detail, Does.Contain("Parts: 41 / 82 (50%) · Missing: 41"));
            Assert.That(detail, Does.Contain("Status: 41 parts missing"));
            Assert.That(detail, Does.Contain("Completing needs 41 energy; 9 energy would remain."));
            Assert.That(detail, Does.Contain("action=internalGalaxyGateSpin"));
            Assert.That(detail, Does.Contain("max=\"10\""));
            Assert.That(detail, Does.Contain("gate=beta\" rel=\"prev\">Previous gate"));
            Assert.That(detail, Does.Contain("gate=delta\" rel=\"next\">Next gate"));
            Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalGalaxyGates\">Close"));
            Assert.That(page, Does.Contain("data-gate=\"gamma\" class=\"selected\""));
            // The selected card does not repeat the form, so the page has one spin form per unfinished gate.
            Assert.That(page.Split("action=internalGalaxyGateSpin\"").Length - 1, Is.EqualTo(3));
        });
    }

    [Test]
    public void Detail_ReportsShortfallWhenEnergyIsInsufficient()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(energy: 100), "epsilon"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Gate 5 of 5"));
            Assert.That(detail, Does.Contain("Parts: 0 / 120 (0%) · Missing: 120"));
            Assert.That(detail, Does.Contain("Completing needs 120 energy; 20 more energy required."));
            Assert.That(detail, Does.Contain("rel=\"prev\""));
            Assert.That(detail, Does.Not.Contain("rel=\"next\""));
        });
        var empty = Detail(LocalGalaxyGates.Page(Session, State(energy: 0), "delta"));
        Assert.That(empty, Does.Contain("Status: No energy (96 parts missing)"));
        Assert.That(empty, Does.Contain("disabled>Spin"));
    }

    [Test]
    public void ReadyDetail_OffersActivationOnly()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(), "beta"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Parts: 48 / 48 (100%) · Missing: 0"));
            Assert.That(detail, Does.Contain("Status: Ready to activate"));
            Assert.That(detail, Does.Contain("No energy needed."));
            Assert.That(detail, Does.Contain("action=internalGalaxyGateActivate"));
            Assert.That(detail, Does.Not.Contain("internalGalaxyGateSpin"));
        });
    }

    [Test]
    public void ActivatedDetail_HasNoFormAndNoPrevious()
    {
        var detail = Detail(LocalGalaxyGates.Page(Session, State(), "alpha"));
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Gate 1 of 5"));
            Assert.That(detail, Does.Contain("Status: Activated"));
            Assert.That(detail, Does.Contain("Gate activated and ready for the game world."));
            Assert.That(detail, Does.Not.Contain("<form"));
            Assert.That(detail, Does.Not.Contain("rel=\"prev\""));
            Assert.That(detail, Does.Contain("gate=beta\" rel=\"next\""));
        });
    }

    [Test]
    public void UnknownGate_RendersListWithoutDetail() =>
        Assert.That(LocalGalaxyGates.Page(Session, State(), "zeta"), Does.Not.Contain("id=\"gate-detail\""));
}
