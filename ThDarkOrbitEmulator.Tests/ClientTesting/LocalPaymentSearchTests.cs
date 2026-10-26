using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPaymentSearchTests
{
    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private static readonly IReadOnlyList<EconomyRewardOffer> Offers =
    [
        new("payment", "starter", "Starter supply", "credits", 1000, "none", 0, true),
        new("payment", "fleet", "Fleet supply", "credits", 5000, "none", 0, true),
        new("payment", "uri_pack", "Uridium pack", "uridium", 50, "none", 0, true),
        new("payment", "old_uri", "Retired Uridium", "uridium", 50, "none", 0, false)
    ];

    private static string[] Cards(string page) =>
        Regex.Matches(page, "<article id=\"package-([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();

    [TestCase("action=internalPayment&q=starter")]
    [TestCase("action=internalPayment&filter=available&q=Uridium&detail=uri_pack")]
    public void Query_AcceptsSearch(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("action=internalPayment&q=")]
    [TestCase("action=internalPayment&q=a")]
    [TestCase("action=internalPayment&q=%20fleet")]
    [TestCase("action=internalPayment&q=fleet%20")]
    [TestCase("action=internalPayment&q=fleet%0A")]
    [TestCase("action=internalPayment&q=%3Cscript%3E")]
    [TestCase("action=internalPayment&q=éé")]
    [TestCase("action=internalPayment&q=12345678901234567890123456789012345678901")]
    [TestCase("action=internalPayment&Q=fleet")]
    [TestCase("action=internalPayment&q=fleet&q=starter")]
    public void Query_RejectsMalformedSearch(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void Search_ComposesWithFilterAndNavigatesWithinResults()
    {
        var state = new LocalEquipmentState([], []);
        var page = LocalPayment.Page(state, offers: Offers, filter: "available", search: "uri");
        Assert.Multiple(() =>
        {
            Assert.That(Cards(page), Is.EqualTo(new[] { "uri_pack" }));
            Assert.That(page, Does.Contain("Available (1)"));
            Assert.That(page, Does.Contain("Unavailable (1)"));
            Assert.That(page, Does.Contain("filter=unavailable&amp;q=uri"));
            Assert.That(page, Does.Contain("filter=available&amp;q=uri&amp;detail=uri_pack"));
            Assert.That(page, Does.Contain("id=\"paymentClearSearch\" href=\"/indexInternal.es?action=internalPayment&amp;filter=available\""));
            Assert.That(LocalPayment.HasDetail(state, LocalPayment.Packages(Offers), "available", "old_uri", "uri"), Is.False);
        });
        var detail = LocalPayment.Page(state, offers: Offers, detail: "uri_pack", search: "uri");
        Assert.That(detail, Does.Contain("rel=\"next\" href=\"/indexInternal.es?action=internalPayment&amp;q=uri&amp;detail=old_uri\""));
        Assert.That(detail, Does.Contain("href=\"/indexInternal.es?action=internalPayment&amp;q=uri\">Close"));
    }

    [Test]
    public void Search_MatchesNameAssetAndId_AndShowsEmptyResult()
    {
        var state = new LocalEquipmentState([], []);
        Assert.That(Cards(LocalPayment.Page(state, offers: Offers, search: "supply")), Is.EqualTo(new[] { "starter", "fleet" }));
        Assert.That(Cards(LocalPayment.Page(state, offers: Offers, search: "uridium")), Is.EqualTo(new[] { "uri_pack", "old_uri" }));
        Assert.That(Cards(LocalPayment.Page(state, offers: Offers, search: "old_uri")), Is.EqualTo(new[] { "old_uri" }));
        var empty = LocalPayment.Page(state, offers: Offers, search: "missing");
        Assert.That(Cards(empty), Is.Empty);
        Assert.That(empty, Does.Contain("No packages match this search."));
        Assert.That(empty, Does.Contain("0 packages match this search."));
    }
}
