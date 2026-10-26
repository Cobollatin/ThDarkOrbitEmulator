using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-146: Payment status filters (all, available, claimed, unavailable).
public class LocalPaymentFilterTests
{
    private static QueryCollection Parse(string query) => new(QueryHelpers.ParseQuery(query));

    private static LocalEquipmentState State(params string[] claimed) =>
        new([], []) { ClaimedPaymentPackages = [.. claimed] };

    private static IReadOnlyList<EconomyRewardOffer> Offers() =>
    [
        new("payment", "starter", "Starter supply", "credits", 1000, "none", 0, true),
        new("payment", "uri_pack", "Uridium pack", "uridium", 50, "none", 0, true),
        new("payment", "retired", "Retired pack", "credits", 900, "none", 0, false),
        new("payment", "old_claim", "Old claim", "credits", 400, "none", 0, false)
    ];

    private static string[] Cards(string html) =>
        Regex.Matches(html, "<article id=\"package-([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();

    [TestCase("action=internalPayment&filter=available")]
    [TestCase("action=internalPayment&filter=claimed&detail=starter")]
    [TestCase("action=internalPayment&filter=unknown")]
    [TestCase("action=internalPayment&filter=abcdefghijklmnop")]
    public void PageQuery_AcceptsWellFormedFilter(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.True);

    [TestCase("action=internalPayment&filter=")]
    [TestCase("action=internalPayment&filter=claimed&filter=claimed")]
    [TestCase("action=internalPayment&Filter=claimed")]
    [TestCase("action=internalPayment&FILTER=claimed")]
    [TestCase("action=internalPayment&filter=Claimed")]
    [TestCase("action=internalPayment&filter=not_claimed")]
    [TestCase("action=internalPayment&filter=claimed1")]
    [TestCase("action=internalPayment&filter=%20claimed")]
    [TestCase("action=internalPayment&filter=claimed%0A")]
    [TestCase("action=internalPayment&filter=%3Cscript%3E")]
    [TestCase("action=internalPayment&filter=abcdefghijklmnopq")]
    public void PageQuery_RejectsMalformedFilter(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Parse(query)), Is.False);

    [Test]
    public void Filter_DefaultsToAll()
    {
        Assert.That(LocalPaymentAction.Filter(Parse("action=internalPayment")), Is.EqualTo("all"));
        Assert.That(LocalPaymentAction.Filter(Parse("action=internalPayment&filter=claimed")), Is.EqualTo("claimed"));
    }

    [TestCase("all", true)]
    [TestCase("available", true)]
    [TestCase("claimed", true)]
    [TestCase("unavailable", true)]
    [TestCase("ready", false)]
    [TestCase("All", false)]
    [TestCase(null, false)]
    public void IsKnownFilter_MatchesCatalogExactly(string? filter, bool expected) =>
        Assert.That(LocalPayment.IsKnownFilter(filter), Is.EqualTo(expected));

    [TestCase("all", new[] { "starter", "uri_pack", "retired", "old_claim" })]
    [TestCase("available", new[] { "uri_pack" })]
    [TestCase("claimed", new[] { "starter", "old_claim" })]
    [TestCase("unavailable", new[] { "retired" })]
    public void Page_ListsOnlyPackagesInFilter(string filter, string[] expected)
    {
        var html = LocalPayment.Page(State("starter", "old_claim"), offers: Offers(), filter: filter);

        Assert.That(Cards(html), Is.EqualTo(expected));
        Assert.That(html, Does.Contain($"data-filter=\"{filter}\" aria-current=\"page\""));
        Assert.That(html, Does.Contain($"<main id=\"payment\" data-filter=\"{filter}\">"));
    }

    [Test]
    public void Page_TabsCountEveryFilterAndKeepSummaryUnfiltered()
    {
        var html = LocalPayment.Page(State("starter", "old_claim"), offers: Offers(), filter: "available");

        Assert.That(html, Does.Contain("All (4)"));
        Assert.That(html, Does.Contain("Available (1)"));
        Assert.That(html, Does.Contain("Claimed (2)"));
        Assert.That(html, Does.Contain("Unavailable (1)"));
        Assert.That(html, Does.Contain("Claimed 2 of 4 packages (1,400 credits)."));
        Assert.That(html, Does.Contain("<title>Payment (available) - DarkOrbit</title>"));
    }

    [Test]
    public void Page_AllFilterKeepsPlainLinksAndTitle()
    {
        var html = LocalPayment.Page(State(), detail: "starter");

        Assert.That(html, Does.Contain("<title>Payment - DarkOrbit</title>"));
        Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalPayment&amp;detail=hangar\""));
        Assert.That(html, Does.Contain("<a href=\"/indexInternal.es?action=internalPayment\">Close</a>"));
        Assert.That(html, Does.Not.Contain("paymentEmpty"));
    }

    [Test]
    public void Page_EmptyFilterShowsMessage()
    {
        var html = LocalPayment.Page(State(), filter: "claimed");

        Assert.That(Cards(html), Is.Empty);
        Assert.That(html, Does.Contain("<p id=\"paymentEmpty\">No claimed packages.</p>"));
    }

    [Test]
    public void Page_UnknownFilterFallsBackToAll()
    {
        var html = LocalPayment.Page(State(), filter: "<b>x</b>");

        Assert.That(Cards(html), Has.Length.EqualTo(LocalPayment.Catalog.Count));
        Assert.That(html, Does.Not.Contain("<b>x</b>"));
    }

    [Test]
    public void Detail_NavigatesWithinFilterAndKeepsIt()
    {
        var html = LocalPayment.Page(State("starter", "fleet", "armada"), detail: "fleet", filter: "claimed");

        Assert.That(html, Does.Contain("<dt>Package</dt><dd>2 of 3</dd>"));
        Assert.That(html, Does.Contain("rel=\"prev\" href=\"/indexInternal.es?action=internalPayment&amp;filter=claimed&amp;detail=starter\""));
        Assert.That(html, Does.Contain("rel=\"next\" href=\"/indexInternal.es?action=internalPayment&amp;filter=claimed&amp;detail=armada\""));
        Assert.That(html, Does.Contain("<a href=\"/indexInternal.es?action=internalPayment&amp;filter=claimed\">Close</a>"));
        Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalPayment&amp;filter=claimed&amp;detail=armada\">Armada supply</a>"));
    }

    [Test]
    public void HasDetail_RequiresPackageInFilter()
    {
        var state = State("starter");
        var catalog = LocalPayment.Packages(null);

        Assert.That(LocalPayment.HasDetail(state, catalog, "all", "hangar"), Is.True);
        Assert.That(LocalPayment.HasDetail(state, catalog, "claimed", "starter"), Is.True);
        Assert.That(LocalPayment.HasDetail(state, catalog, "claimed", "hangar"), Is.False);
        Assert.That(LocalPayment.HasDetail(state, catalog, "available", "starter"), Is.False);
        Assert.That(LocalPayment.HasDetail(state, catalog, "available", "missing"), Is.False);
        Assert.That(LocalPayment.HasDetail(state, catalog, "bogus", "hangar"), Is.False);
    }

    [Test]
    public void Proxy_PageDefaultsToAll()
    {
        var html = LocalPayment.Page(State(), proxy: true);

        Assert.That(html, Does.Contain("<main id=\"paymentProxy\" data-filter=\"all\">"));
        Assert.That(Cards(html), Has.Length.EqualTo(LocalPayment.Catalog.Count));
    }
}
