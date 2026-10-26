using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-208: local Auction lot search and its interaction with status filters.
public class LocalAuctionSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static readonly EconomyAuctionLot[] Lots =
    [
        new("laser_lf3", "laser_lf3", "LF-3 laser", 700, null, null, false, true),
        new("shield_sg3n_b02", "shield_sg3n_b02", "SG3N-B02 shield", 450, 500, Now.AddMinutes(3), false, true),
        new("drone_flax", "drone_flax", "Flax drone", 200, 300, Now.AddMinutes(-1), false, true)
    ];

    private static IQueryCollection Query(string url) => new QueryCollection(QueryHelpers.ParseQuery(url));

    private static string Page(string? search, string? filter = null, string? lot = null) =>
        LocalAuction.PageFromDatabase(Session, Lots, [new EconomyBalance("credits", "currency", 1000)],
            Now, lotId: lot, filter: filter, search: search);

    [TestCase("?action=internalAuction&q=laser", true)]
    [TestCase("?action=internalAuction&filter=open&q=shield", true)]
    [TestCase("?action=internalAuction&q=", true)]
    [TestCase("?action=internalAuction&q=%20%20", true)]
    [TestCase("?action=internalAuction&q=%20LF-3%20", true)]
    [TestCase("?action=internalAuction&q=&lot=laser_lf3", true)]
    [TestCase("?action=internalAuction&q=l", false)]
    [TestCase("?action=internalAuction&q=laser&q=drone", false)]
    [TestCase("?action=internalAuction&Q=laser", false)]
    [TestCase("?action=internalAuction&q=laser%0A", false)]
    [TestCase("?action=internalAuction&q=%3Cscript%3E", false)]
    [TestCase("?action=internalAuction&q=las%C3%A9r", false)]
    [TestCase("?action=internalAuction&q=laser&lot=laser_lf3", false)]
    public void Query_ValidatesSearch(string url, bool expected) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(Query(url)), Is.EqualTo(expected));

    [Test]
    public void Query_EnforcesFortyCharacterLimit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalAuctionAction.ValidPageQuery(Query("?action=internalAuction&q=" + new string('a', 40))), Is.True);
            Assert.That(LocalAuctionAction.ValidPageQuery(Query("?action=internalAuction&q=" + new string('a', 41))), Is.False);
        });
    }

    [Test]
    public void Search_NormalizesBlankAndTrims()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalAuctionAction.Search(Query("?action=internalAuction&q=%20LF-3%20")), Is.EqualTo("LF-3"));
            Assert.That(LocalAuctionAction.Search(Query("?action=internalAuction&q=%20%20")), Is.Null);
            Assert.That(LocalAuctionAction.Search(Query("?action=internalAuction")), Is.Null);
        });
    }

    [Test]
    public void Search_FiltersRowsAndCountsAndPreservesFilterLinks()
    {
        var html = Page("SHIELD", "open");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("data-lot=\"shield_sg3n_b02\""));
            Assert.That(html, Does.Not.Contain("data-lot=\"laser_lf3\""));
            Assert.That(html, Does.Contain("Open (1)"));
            Assert.That(html, Does.Contain("Ready to claim (0)"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;q=SHIELD\" data-filter=\"all\""));
            Assert.That(html, Does.Contain("name=\"filter\" value=\"open\""));
            Assert.That(html, Does.Contain("1 of 2 lots match"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;filter=open\">Clear search"));
            Assert.That(html, Does.Contain("filter=open&amp;lot=shield_sg3n_b02\">Details"));
        });
    }

    [Test]
    public void Search_MatchesIdAndNameAndShowsEmptyResult()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Page("FLAX"), Does.Contain("data-lot=\"drone_flax\""));
            Assert.That(Page("lf-3"), Does.Contain("data-lot=\"laser_lf3\""));
            Assert.That(Page("missing", "claimable"), Does.Contain("No lots match this search."));
        });
    }

    [Test]
    public void Search_DoesNotChangeSavedStateNoticeOrDetailPage()
    {
        var html = LocalAuction.PageFromDatabase(Session, Lots, [new EconomyBalance("credits", "currency", 1000)],
            Now, placed: "shield_sg3n_b02", filter: "claimed", search: "laser");
        var detail = Page("laser", lot: "drone_flax");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"auctionPlaced\""));
            Assert.That(html, Does.Contain("Credits: 1,000"));
            Assert.That(detail, Does.Contain("id=\"auction-lot\""));
            Assert.That(detail, Does.Not.Contain("id=\"auctionSearchSummary\""));
        });
    }
}
