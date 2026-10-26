using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-144: emulator-defined Auction status filter with counted tabs.
public class LocalAuctionFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    // One lot per status: open (unopened), open (running), claimable, ended without bid, claimed, unavailable.
    private static EconomyAuctionLot[] Lots() =>
    [
        new("laser_lf3", "laser_lf3", "LF-3 laser", 700, null, null, false, true),
        new("shield_sg3n_b02", "shield_sg3n_b02", "SG3N-B02 shield", 450, 500, Now.AddMinutes(3), false, true),
        new("drone_flax", "drone_flax", "Flax drone", 200, 300, Now.AddMinutes(-1), false, true),
        new("drone_iris", "drone_iris", "Iris drone", 900, 0, Now.AddMinutes(-1), false, true),
        new("engine_g3n", "engine_g3n", "G3N engine", 300, 400, Now.AddMinutes(-9), true, true),
        new("pet_10", "pet_10", "P.E.T. 10", 1500, null, null, false, false)
    ];

    private static string Page(string? filter, string? lot = null, EconomyAuctionLot[]? lots = null) =>
        LocalAuction.PageFromDatabase(Session, lots ?? Lots(), [new EconomyBalance("credits", "currency", 1000)],
            Now, lot, filter: filter);

    [TestCase("laser_lf3", "open")]
    [TestCase("shield_sg3n_b02", "open")]
    [TestCase("drone_flax", "claimable")]
    [TestCase("drone_iris", "ended")]
    [TestCase("engine_g3n", "claimed")]
    [TestCase("pet_10", "unavailable")]
    public void StatusId_ClassifiesEachLot(string lotId, string expected) =>
        Assert.That(LocalAuction.StatusId(Lots().Single(lot => lot.Id == lotId), Now), Is.EqualTo(expected));

    [Test]
    public void DisabledLotIsUnavailableEvenWhenClaimed() =>
        Assert.That(LocalAuction.StatusId(new("x", "x", "X", 1, 5, Now.AddMinutes(-1), true, false), Now),
            Is.EqualTo("unavailable"));

    [Test]
    public void DefaultList_ShowsCountedTabsAndEveryLot()
    {
        var html = Page(null);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<strong data-filter=\"all\" aria-current=\"page\">All (6)</strong>"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;filter=open\" data-filter=\"open\">Open (2)</a>"));
            Assert.That(html, Does.Contain("data-filter=\"claimable\">Ready to claim (1)</a>"));
            Assert.That(html, Does.Contain("data-filter=\"claimed\">Claimed (1)</a>"));
            Assert.That(html, Does.Contain("data-filter=\"ended\">Ended without bid (1)</a>"));
            Assert.That(html, Does.Contain("data-filter=\"unavailable\">Unavailable (1)</a>"));
            Assert.That(CountRows(html), Is.EqualTo(6));
            Assert.That(html, Does.Contain("<tr data-lot=\"drone_flax\" data-status=\"claimable\">"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;lot=laser_lf3\">Details"));
        });
    }

    [Test]
    public void AllFilter_MatchesDefaultList()
    {
        // Form keys carry random nonces, so compare the tabs and row count rather than the whole page.
        var html = Page("all");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<strong data-filter=\"all\" aria-current=\"page\">All (6)</strong>"));
            Assert.That(CountRows(html), Is.EqualTo(6));
            Assert.That(html, Does.Not.Contain("filter=all"));
        });
    }

    [Test]
    public void OpenFilter_ShowsOnlyOpenLotsAndKeepsFilterInLinks()
    {
        var html = Page("open");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<strong data-filter=\"open\" aria-current=\"page\">Open (2)</strong>"));
            Assert.That(html, Does.Contain("action=internalAuction\" data-filter=\"all\">All (6)</a>"));
            Assert.That(CountRows(html), Is.EqualTo(2));
            Assert.That(html, Does.Contain("data-lot=\"laser_lf3\"").And.Contain("data-lot=\"shield_sg3n_b02\""));
            Assert.That(html, Does.Not.Contain("data-lot=\"drone_flax\""));
            Assert.That(html, Does.Contain("filter=open&amp;lot=laser_lf3\">Details"));
        });
    }

    [Test]
    public void ClaimableFilter_ShowsClaimForm()
    {
        var html = Page("claimable");
        Assert.Multiple(() =>
        {
            Assert.That(CountRows(html), Is.EqualTo(1));
            Assert.That(html, Does.Contain("data-lot=\"drone_flax\" data-status=\"claimable\""));
            Assert.That(html, Does.Contain("action=internalAuctionClaim"));
            Assert.That(html, Does.Not.Contain("action=internalAuctionBid"));
        });
    }

    [Test]
    public void EmptyFilter_ShowsNotice()
    {
        var html = Page("claimed", lots: Lots().Where(lot => !lot.Claimed).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Claimed (0)"));
            Assert.That(html, Does.Contain("id=\"auction-empty\">No lots match this filter."));
            Assert.That(CountRows(html), Is.EqualTo(0));
        });
    }

    [Test]
    public void Detail_PositionAndNavigationStayWithinFilter()
    {
        var html = Page("open", "shield_sg3n_b02");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Lot 2 of 2"));
            Assert.That(html, Does.Contain("filter=open&amp;lot=laser_lf3\">Previous"));
            Assert.That(html, Does.Not.Contain(">Next<"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;filter=open\">Close"));
        });
    }

    [Test]
    public void Detail_WithoutFilter_KeepsPlainLinks()
    {
        var html = Page(null, "laser_lf3");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Lot 1 of 6"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;lot=shield_sg3n_b02\">Next"));
            Assert.That(html, Does.Contain("action=internalAuction\">Close"));
        });
    }

    [Test]
    public void HasLot_RejectsLotOutsideFilter()
    {
        var lots = Lots();
        Assert.Multiple(() =>
        {
            Assert.That(LocalAuction.HasLot(lots, "drone_flax", "claimable", Now), Is.True);
            Assert.That(LocalAuction.HasLot(lots, "drone_flax", "open", Now), Is.False);
            Assert.That(LocalAuction.HasLot(lots, "drone_flax", "all", Now), Is.True);
            Assert.That(LocalAuction.HasLot(lots, null, "open", Now), Is.True);
        });
    }

    [Test]
    public void FilterDoesNotHideNotices()
    {
        var html = LocalAuction.PageFromDatabase(Session, Lots(), [new EconomyBalance("credits", "currency", 1000)],
            Now, placed: "shield_sg3n_b02", filter: "claimed");
        Assert.That(html, Does.Contain("id=\"auctionPlaced\""));
    }

    [TestCase("all", true)]
    [TestCase("open", true)]
    [TestCase("unavailable", true)]
    [TestCase("bidding", false)]
    [TestCase("Open", false)]
    public void IsKnownFilter_IsCaseExact(string filter, bool expected) =>
        Assert.That(LocalAuction.IsKnownFilter(filter), Is.EqualTo(expected));

    [TestCase("open", true)]
    [TestCase("no-such-filter", true)]
    [TestCase("", false)]
    [TestCase("Open", false)]
    [TestCase("open_now", false)]
    [TestCase("abcdefghijklmnopq", false)]
    public void IsFilterShape_AcceptsShortLowercaseSlugs(string value, bool expected) =>
        Assert.That(LocalAuction.IsFilterShape(value), Is.EqualTo(expected));

    private static int CountRows(string html) =>
        html.Split("<tr data-lot=").Length - 1;
}
