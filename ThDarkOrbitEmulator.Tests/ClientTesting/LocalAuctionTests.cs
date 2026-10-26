using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionTests
{
    [Test]
    public async Task BidAndClaim_ShareShopCreditsAndHangarInventory()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.That(await store.BidAsync(account, "laser_lf3", 700, now, CancellationToken.None), Is.True);
        Assert.That(await store.BidAsync(account, "laser_lf3", 750, now, CancellationToken.None), Is.False);
        Assert.That(await store.BidAsync(account, "laser_lf3", 770, now, CancellationToken.None), Is.True);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(9230));
        Assert.That(await store.ClaimAuctionAsync(account, "laser_lf3", now.AddMinutes(4), CancellationToken.None), Is.False);
        Assert.That(await store.ClaimAuctionAsync(account, "laser_lf3", now.AddMinutes(5), CancellationToken.None), Is.True);
        Assert.That(await store.ClaimAuctionAsync(account, "laser_lf3", now.AddMinutes(6), CancellationToken.None), Is.False);
        Assert.That(await store.BidAsync(account, "laser_lf3", 1000, now.AddMinutes(6), CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(5));
            Assert.That(state.AuctionLots.Single().Claimed, Is.True);
        });
        Assert.That((await store.GetAsync(other, CancellationToken.None)).Credits, Is.EqualTo(10000));
        Assert.That((await store.GetAsync(other, CancellationToken.None)).Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(4));
    }

    [Test]
    public async Task Bid_RejectsUnknownOrUnaffordableLotsWithoutDebit()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.That(await store.BidAsync(account, "missing", 700, now, CancellationToken.None), Is.False);
        Assert.That(await store.BidAsync(account, "drone_flax", 199, now, CancellationToken.None), Is.False);
        Assert.That(await store.BidAsync(account, "drone_flax", 10001, now, CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(10000));
        Assert.That(await store.ClaimAuctionAsync(account, "drone_flax", now.AddDays(1), CancellationToken.None), Is.False);
    }

    [Test]
    public async Task Action_RequiresSessionAndCompany_AndRedirectsAfterBid()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalAuctionBid", "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(anonymous, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalAuctionBid", "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(noCompany, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var invalid = Request(session.Dosid, "internalAuctionBid", "item=drone_flax&amount=oops");
        await LocalAuctionAction.HandleAsync(invalid, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        var valid = Request(session.Dosid, "internalAuctionBid", "item=drone_flax&amount=200");
        await LocalAuctionAction.HandleAsync(valid, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAuction&placed=drone_flax"));
    }

    [Test]
    public void Page_ShowsLotsBalanceAndEscapesPilotName()
    {
        var page = LocalAuction.Page(new GameAuthSession { Username = "<pilot>", Company = "mmo" },
            new LocalEquipmentState([], []), DateTimeOffset.UtcNow);
        Assert.That(page, Does.Contain("id=\"auction\""));
        Assert.That(page, Does.Contain("action=internalAuctionBid"));
        Assert.That(page, Does.Contain("LF-3 laser"));
        Assert.That(page, Does.Contain("&lt;pilot&gt;"));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalAuction"));
    }

    private static DefaultHttpContext Request(string? dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(AuctionTestForms.WithKey(dosid, action, body));
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
