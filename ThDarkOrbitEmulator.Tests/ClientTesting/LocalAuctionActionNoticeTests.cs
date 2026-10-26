using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-124: Auction bid/claim confirmations. Emulator-defined; no Live Auction capture exists.
public class LocalAuctionActionNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static EconomyAuctionLot[] Lots() =>
    [
        new("laser_lf3", "laser_lf3", "LF-3 laser", 700, null, null, false, true),
        new("shield_sg3n_b02", "shield_sg3n_b02", "SG3N-B02 shield", 450, 500, Now.AddMinutes(3), false, true),
        new("drone_flax", "drone_flax", "Flax drone", 200, 300, Now.AddMinutes(-1), true, true),
        new("ended_lot", "laser_lf3", "Ended laser", 100, 150, Now.AddMinutes(-2), false, true)
    ];

    private static string Page(string? placed = null, string? claimed = null, string? lot = null,
        long credits = 1000, EconomyAuctionLot[]? lots = null) =>
        LocalAuction.PageFromDatabase(Session, lots ?? Lots(), [new EconomyBalance("credits", "currency", credits)],
            Now, lot, placed, claimed);

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("action=internalAuction&placed=laser_lf3", true)]
    [TestCase("action=internalAuction&claimed=drone_flax", true)]
    [TestCase("action=internalAuction&placed=laser_lf3&lot=laser_lf3", true)]
    [TestCase("action=internalAuction&claimed=drone_flax&from=internalStart&lang=en", true)]
    [TestCase("action=internalAuction&placed=a-b_9", true)]
    [TestCase("action=internalAuction&placed=", false)]
    [TestCase("action=internalAuction&claimed=", false)]
    [TestCase("action=internalAuction&placed=Laser_lf3", false)]
    [TestCase("action=internalAuction&placed=laser%20lf3", false)]
    [TestCase("action=internalAuction&placed=laser.lf3", false)]
    [TestCase("action=internalAuction&placed=laser%0A", false)]
    [TestCase("action=internalAuction&placed=a&placed=b", false)]
    [TestCase("action=internalAuction&claimed=a&claimed=a", false)]
    [TestCase("action=internalAuction&PLACED=laser_lf3", false)]
    [TestCase("action=internalAuction&Claimed=drone_flax", false)]
    [TestCase("action=internalAuction&placed=laser_lf3&claimed=drone_flax", false)]
    [TestCase("action=internalAuction&bid=laser_lf3", false)]
    public void PageQuery_ValidatesNoticeParameters(string query, bool expected) =>
        Assert.That(LocalAuctionAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsNoticeIdLength()
    {
        Assert.That(LocalAuctionAction.ValidPageQuery(Query("action=internalAuction&placed=" + new string('a', 64))), Is.True);
        Assert.That(LocalAuctionAction.ValidPageQuery(Query("action=internalAuction&placed=" + new string('a', 65))), Is.False);
        Assert.That(LocalAuctionAction.ValidPageQuery(Query("action=internalAuction&claimed=" + new string('a', 65))), Is.False);
    }

    [Test]
    public void RedirectTarget_NamesOperationAndLot()
    {
        Assert.That(LocalAuctionAction.RedirectTarget(true, "laser_lf3"),
            Is.EqualTo("/indexInternal.es?action=internalAuction&placed=laser_lf3"));
        Assert.That(LocalAuctionAction.RedirectTarget(false, "drone_flax"),
            Is.EqualTo("/indexInternal.es?action=internalAuction&claimed=drone_flax"));
    }

    [Test]
    public void Placed_ConfirmsHeldBidTimerMinimumAndCredits()
    {
        var html = Page(placed: "shield_sg3n_b02", credits: 1234);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<p id=\"auctionPlaced\" role=\"status\" data-lot=\"shield_sg3n_b02\">"));
            Assert.That(html, Does.Contain("Bid placed on SG3N-B02 shield: 500 Credits held."));
            Assert.That(html, Does.Contain($"Ends {Now.AddMinutes(3):u}."));
            Assert.That(html, Does.Contain("Next minimum bid: 550."));
            Assert.That(html, Does.Contain("Credits left: 1,234."));
            Assert.That(html, Does.Not.Contain("id=\"auctionClaimed\""));
        });
    }

    [Test]
    public void Placed_OnEndedLotPointsToClaim()
    {
        var html = Page(placed: "ended_lot");
        Assert.That(html, Does.Contain("Bid placed on Ended laser: 150 Credits held. The auction has ended; claim the item."));
    }

    [Test]
    public void Placed_WithoutTimerSaysBiddingOpen()
    {
        var lots = Lots();
        lots[0] = lots[0] with { Bid = 700 };
        Assert.That(Page(placed: "laser_lf3", lots: lots), Does.Contain("700 Credits held. Bidding open. Next minimum bid: 770."));
    }

    [TestCase("laser_lf3")]      // no saved bid
    [TestCase("drone_flax")]     // already claimed
    [TestCase("missing_lot")]    // not in the catalog
    public void Placed_IsHiddenWithoutMatchingSavedBid(string lot) =>
        Assert.That(Page(placed: lot), Does.Not.Contain("id=\"auctionPlaced\""));

    [Test]
    public void Placed_ClampsNegativeCreditsToZero() =>
        Assert.That(Page(placed: "shield_sg3n_b02", credits: -5), Does.Contain("Credits left: 0."));

    [Test]
    public void Claimed_ConfirmsPriceAndHangarItem()
    {
        var html = Page(claimed: "drone_flax");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<p id=\"auctionClaimed\" role=\"status\" data-lot=\"drone_flax\">"));
            Assert.That(html, Does.Contain("Claimed Flax drone for 300 Credits. 1 drone_flax moved to your Hangar inventory."));
            Assert.That(html, Does.Not.Contain("id=\"auctionPlaced\""));
        });
    }

    [TestCase("shield_sg3n_b02")] // running bid, not claimed
    [TestCase("ended_lot")]       // ended, not yet claimed
    [TestCase("laser_lf3")]       // never bid
    [TestCase("missing_lot")]
    public void Claimed_IsHiddenUnlessSavedLotIsClaimed(string lot) =>
        Assert.That(Page(claimed: lot), Does.Not.Contain("id=\"auctionClaimed\""));

    [Test]
    public void NoNotice_WithoutParameters()
    {
        var html = Page();
        Assert.That(html, Does.Not.Contain("id=\"auctionPlaced\"").And.Not.Contain("id=\"auctionClaimed\""));
    }

    [Test]
    public void Notice_EncodesNamesAndItemIds()
    {
        var lots = new EconomyAuctionLot[]
        {
            new("x", "<item>", "<b>Evil</b>", 10, 20, Now.AddMinutes(1), false, true),
            new("y", "a&b", "\"Quoted\"", 10, 20, Now.AddMinutes(-1), true, true)
        };
        var placed = Page(placed: "x", lots: lots);
        var claimed = Page(claimed: "y", lots: lots);
        Assert.Multiple(() =>
        {
            Assert.That(placed, Does.Contain("Bid placed on &lt;b&gt;Evil&lt;/b&gt;").And.Not.Contain("<b>Evil</b>"));
            Assert.That(claimed, Does.Contain("Claimed &quot;Quoted&quot;").And.Contain("1 a&amp;b moved"));
        });
    }

    [Test]
    public void Notice_CombinesWithDetailPanel()
    {
        var html = Page(placed: "shield_sg3n_b02", lot: "shield_sg3n_b02");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"auctionPlaced\""));
            Assert.That(html, Does.Contain("id=\"auction-lot\""));
            Assert.That(html.IndexOf("id=\"auctionPlaced\"", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("id=\"auction-lot\"", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task Actions_RedirectWithLotOnSuccess_AndNotOnFailure()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var clock = new ShiftedClock();

        var bid = Request(session.Dosid, "internalAuctionBid", "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(bid, sessions, store, clock, CancellationToken.None);
        Assert.That(bid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(bid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAuction&placed=drone_flax"));

        var low = Request(session.Dosid, "internalAuctionBid", "item=drone_flax&amount=201");
        await LocalAuctionAction.HandleAsync(low, sessions, store, clock, CancellationToken.None);
        Assert.That(low.Response.StatusCode, Is.EqualTo(409));
        Assert.That(low.Response.Headers.Location.ToString(), Is.Empty);

        var early = Request(session.Dosid, "internalAuctionClaim", "item=drone_flax");
        await LocalAuctionAction.HandleAsync(early, sessions, store, clock, CancellationToken.None);
        Assert.That(early.Response.StatusCode, Is.EqualTo(409));
        Assert.That(early.Response.Headers.Location.ToString(), Is.Empty);

        clock.Offset = TimeSpan.FromMinutes(6);
        var claim = Request(session.Dosid, "internalAuctionClaim", "item=drone_flax");
        await LocalAuctionAction.HandleAsync(claim, sessions, store, clock, CancellationToken.None);
        Assert.That(claim.Response.StatusCode, Is.EqualTo(303));
        Assert.That(claim.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAuction&claimed=drone_flax"));

        var replay = Request(session.Dosid, "internalAuctionClaim", "item=drone_flax");
        await LocalAuctionAction.HandleAsync(replay, sessions, store, clock, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        Assert.That(replay.Response.Headers.Location.ToString(), Is.Empty);
    }

    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private static DefaultHttpContext Request(string dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(AuctionTestForms.WithKey(dosid, action, body));
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
