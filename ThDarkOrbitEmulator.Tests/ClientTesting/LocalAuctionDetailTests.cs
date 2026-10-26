using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static EconomyAuctionLot[] Lots() =>
    [
        new("laser_lf3", "laser_lf3", "LF-3 laser", 700, null, null, false, true),
        new("shield_sg3n_b02", "shield_sg3n_b02", "SG3N-B02 shield", 450, 500, Now.AddMinutes(3), false, true),
        new("drone_flax", "drone_flax", "Flax drone", 200, 300, Now.AddMinutes(-1), false, true)
    ];

    private static string Page(string? lot, long credits = 1000, EconomyAuctionLot[]? lots = null) =>
        LocalAuction.PageFromDatabase(Session, lots ?? Lots(), [new EconomyBalance("credits", "currency", credits)], Now, lot);

    [Test]
    public void List_LinksEveryLotToItsDetail()
    {
        var html = Page(null);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("id=\"auction-lot\""));
            Assert.That(html, Does.Contain("action=internalAuction&amp;lot=laser_lf3\">Details"));
            Assert.That(html, Does.Contain("action=internalAuction&amp;lot=drone_flax\">Details"));
        });
    }

    [Test]
    public void Detail_ShowsUnopenedLotFiguresAndBidForm()
    {
        var html = Page("laser_lf3");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<h2>LF-3 laser</h2>").And.Contain("Lot 1 of 3"));
            Assert.That(html, Does.Contain("<dt>Opening bid</dt><dd>700</dd>"));
            Assert.That(html, Does.Contain("<dt>Status</dt><dd>Bidding open</dd>"));
            Assert.That(html, Does.Contain("<dt>Credits after minimum bid</dt><dd>300</dd>"));
            Assert.That(html, Does.Contain("starts its five-minute timer"));
            Assert.That(html, Does.Not.Contain(">Previous<"));
            Assert.That(html, Does.Contain("lot=shield_sg3n_b02\">Next"));
            Assert.That(html, Does.Contain("action=internalAuction\">Close"));
        });
    }

    [Test]
    public void Detail_RaiseOnlyCostsTheDifferenceAboveTheHeldBid()
    {
        // Held bid 500 -> minimum 550, so a raise needs 50 more Credits.
        var html = Page("shield_sg3n_b02", credits: 40);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<dt>Minimum bid</dt><dd>550</dd>"));
            Assert.That(html, Does.Contain("You need 10 more Credits"));
            Assert.That(html, Does.Contain("<dt>Credits after minimum bid</dt><dd>0</dd>"));
            Assert.That(html, Does.Contain("lot=laser_lf3\">Previous").And.Contain("lot=drone_flax\">Next"));
        });
    }

    [Test]
    public void UnaffordableBidHasNoFormInListOrDetail()
    {
        var html = Page("shield_sg3n_b02", credits: 40);
        Assert.That(html, Does.Contain("You need 10 more Credits"));
        Assert.That(html, Does.Not.Contain("action=internalAuctionBid"));

        // The held 500-Credit bid covers all but 50 of the 550-Credit minimum.
        var affordable = Page("shield_sg3n_b02", credits: 50);
        Assert.That(affordable.Split("action=internalAuctionBid").Length - 1, Is.EqualTo(2));
    }

    [Test]
    public void ExpiredLotWithoutBidHasNoClaimFormInListOrDetail()
    {
        EconomyAuctionLot[] lots =
        [
            new("no_bid", "drone_iris", "Iris drone", 900, 0, Now.AddMinutes(-1), false, true)
        ];
        var html = Page("no_bid", lots: lots);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("The timer has ended without a bid."));
            Assert.That(html, Does.Not.Contain("action=internalAuctionClaim"));
            Assert.That(html, Does.Not.Contain("action=internalAuctionBid"));
            Assert.That(html, Does.Contain("data-status=\"ended\""));
        });
    }

    [Test]
    public void FullRewardStackBlocksClaimOnListAndDetailUntilSpaceIsAvailable()
    {
        var balances = new[] { new EconomyBalance("credits", "currency", 1000) };
        var full = LocalAuction.PageFromDatabase(Session, Lots(), balances, Now, "drone_flax",
            inventory: new Dictionary<string, long> { ["drone_flax"] = long.MaxValue });
        Assert.Multiple(() =>
        {
            Assert.That(full, Does.Contain("Hangar inventory stack is full"));
            Assert.That(full, Does.Not.Contain("action=internalAuctionClaim"));
            Assert.That(full, Does.Contain("data-status=\"claimable\""));
        });

        var available = LocalAuction.PageFromDatabase(Session, Lots(), balances, Now, "drone_flax",
            inventory: new Dictionary<string, long>
            {
                ["drone_flax"] = long.MaxValue - 1,
                ["laser_lf3"] = long.MaxValue
            });
        Assert.That(available.Split("action=internalAuctionClaim").Length - 1, Is.EqualTo(2));
        Assert.That(available, Does.Contain("Claim the item to move it to your Hangar"));
    }

    [Test]
    public void Detail_EndedLotOffersClaimOnly()
    {
        var html = Page("drone_flax");
        var panel = html[html.IndexOf("id=\"auction-lot\"", StringComparison.Ordinal)..html.IndexOf("<table>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("Lot 3 of 3").And.Contain("Auction ended"));
            Assert.That(panel, Does.Contain("action=internalAuctionClaim").And.Not.Contain("action=internalAuctionBid"));
            Assert.That(panel, Does.Not.Contain("Credits after minimum bid").And.Not.Contain(">Next<"));
        });
    }

    [Test]
    public void Detail_DisabledAndClaimedLotsHaveNoForm()
    {
        EconomyAuctionLot[] lots =
        [
            new("off", "laser_lf3", "Off lot", 100, null, null, false, false),
            new("done", "drone_flax", "Done lot", 100, 150, Now.AddMinutes(-9), true, true)
        ];
        var off = Page("off", lots: lots);
        var done = Page("done", lots: lots);
        Assert.Multiple(() =>
        {
            Assert.That(off, Does.Contain("disabled by the operator"));
            Assert.That(done, Does.Contain("already claimed"));
            foreach (var html in new[] { off, done })
            {
                var panel = html[html.IndexOf("id=\"auction-lot\"", StringComparison.Ordinal)..html.IndexOf("<table>", StringComparison.Ordinal)];
                Assert.That(panel, Does.Not.Contain("<form"));
            }
        });
    }

    [Test]
    public void Detail_EncodesAdminDefinedNames()
    {
        var html = Page("x", lots: [new("x", "laser_lf3", "<b>Lot</b>", 100, null, null, false, true)]);
        Assert.That(html, Does.Contain("&lt;b&gt;Lot&lt;/b&gt;").And.Not.Contain("<b>Lot</b>"));
    }

    [TestCase(null, true)]
    [TestCase("laser_lf3", true)]
    [TestCase("LASER_LF3", false)]
    [TestCase("laser", false)]
    public void HasLot_MatchesExactIdsOnly(string? lot, bool expected) =>
        Assert.That(LocalAuction.HasLot(Lots(), lot), Is.EqualTo(expected));

    [TestCase("action=internalAuction&lot=laser_lf3", true)]
    [TestCase("action=internalAuction&lot=drone-2&lang=en", true)]
    [TestCase("action=internalAuction&LOT=laser_lf3", false)]
    [TestCase("action=internalAuction&lot=", false)]
    [TestCase("action=internalAuction&lot=Laser", false)]
    [TestCase("action=internalAuction&lot=a%20b", false)]
    [TestCase("action=internalAuction&lot=a&lot=b", false)]
    [TestCase("action=internalAuction&lot=a%0A", false)]
    public void PageQuery_ValidatesLotParameter(string query, bool expected) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery(query))), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsLotLength()
    {
        Assert.That(LocalAuctionAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery("action=internalAuction&lot=" + new string('a', 64)))), Is.True);
        Assert.That(LocalAuctionAction.ValidPageQuery(new QueryCollection(QueryHelpers.ParseQuery("action=internalAuction&lot=" + new string('a', 65)))), Is.False);
    }
}
