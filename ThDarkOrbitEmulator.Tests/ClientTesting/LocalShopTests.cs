using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopTests
{
    [Test]
    public async Task Purchase_DebitsAccountAndAddsEquipableInventory_WithoutAffectingAnotherAccount()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 2, CancellationToken.None), Is.True);
        var purchased = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(purchased.Credits, Is.EqualTo(7000));
            Assert.That(purchased.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(6));
        });
        // Menu-134: Pusat Plus has five laser slots, so a sixth unit stays in storage.
        Assert.That(await store.AssignAsync(account, 1, 1, "laser_lf3", 5, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 1, 1, "laser_lf3", 6, CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(other, CancellationToken.None)).Credits, Is.EqualTo(10000));
        Assert.That((await store.GetAsync(other, CancellationToken.None)).Inventory.Single(item => item.Id == "laser_lf3").Quantity,
            Is.EqualTo(4));
    }

    [Test]
    public async Task Purchase_RejectsInvalidAndUnaffordableOrders_WithoutChangingState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.PurchaseAsync(account, "missing", 1, CancellationToken.None), Is.False);
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 0, CancellationToken.None), Is.False);
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 101, CancellationToken.None), Is.False);
        Assert.That(await store.PurchaseAsync(account, "pet_10", 4, CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task ConcurrentPurchases_CannotSpendTheSameCreditsTwice()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var purchases = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            store.PurchaseAsync(account, "pet_10", 2, CancellationToken.None)));
        Assert.That(purchases.Count(result => result), Is.EqualTo(1));
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(4000));
        Assert.That(state.Inventory.Single(item => item.Id == "pet_10").Quantity, Is.EqualTo(3));
    }

    [Test]
    public async Task ShopPost_RequiresSessionAndCompany_AndRedirectsAfterPurchase()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var inventory = new LocalHangarEquipmentStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;

        var anonymous = Request(null, "item=drone_flax&quantity=1");
        await LocalShopPurchase.HandleAsync(anonymous, sessions, inventory, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "item=drone_flax&quantity=1");
        await LocalShopPurchase.HandleAsync(noCompany, sessions, inventory, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var invalid = Request(session.Dosid, "item=drone_flax&quantity=-1");
        await LocalShopPurchase.HandleAsync(invalid, sessions, inventory, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        var valid = Request(session.Dosid, "item=drone_flax&quantity=1");
        await LocalShopPurchase.HandleAsync(valid, sessions, inventory, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
            Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalShop&item=drone_flax"));
        });
    }

    [Test]
    public void ShopPage_ListsCategoriesAndItemDetails_FromSameInventoryIds()
    {
        var session = new GameAuthSession { Username = "pilot", Company = "mmo" };
        var state = new LocalEquipmentState(
            LocalShop.Catalog.Select(item => new LocalEquipmentItem(item.Id, item.Name, item.Category, 1)).ToList(), []);
        var all = LocalShop.Page(session, state, null, null);
        var drones = LocalShop.Page(session, state, "Drones", null);
        var detail = LocalShop.Page(session, state, null, "pet_10");
        Assert.That(all, Does.Contain("LF-3 laser"));
        Assert.That(all, Does.Contain("action=internalShopBuy"));
        Assert.That(drones, Does.Contain("Flax drone"));
        Assert.That(drones, Does.Not.Contain("LF-3 laser"));
        Assert.That(detail, Does.Contain("P.E.T. 10"));
        Assert.That(detail, Does.Not.Contain("Flax drone"));
        Assert.That(LocalShop.IsCategory("unknown"), Is.False);
        Assert.That(LocalShop.Find("unknown"), Is.Null);
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalShop"));
    }

    [Test]
    public void ShopOrder_RejectsTamperingDuplicatesForeignSessionAndExtremeQuantities()
    {
        var key = LocalShopPurchase.CreateFormKey("session-one", "laser_lf3");
        static IFormCollection Form(params (string Name, StringValues Value)[] fields) =>
            new FormCollection(fields.ToDictionary(field => field.Name, field => field.Value));
        var valid = Form(("item", "laser_lf3"), ("quantity", "2"), ("currency", "credits"), ("idempotencyKey", key));
        Assert.That(LocalShopPurchase.TryReadOrder(valid, "session-one", out var item, out var quantity, out var currency, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That((item, quantity, currency), Is.EqualTo(("laser_lf3", 2, "credits")));
            Assert.That(LocalShopPurchase.TryReadOrder(valid, "other-session", out _, out _, out _, out _), Is.False);
            Assert.That(LocalShopPurchase.TryReadOrder(Form(("item", "drone_flax"), ("quantity", "2"), ("currency", "credits"), ("idempotencyKey", key)), "session-one", out _, out _, out _, out _), Is.False);
        });
        foreach (var invalidQuantity in new[] { "-1", "0", "101", "2147483648", "lots" })
            Assert.That(LocalShopPurchase.TryReadOrder(Form(("item", "laser_lf3"), ("quantity", invalidQuantity), ("currency", "credits"), ("idempotencyKey", key)), "session-one", out _, out _, out _, out _), Is.False);
        Assert.That(LocalShopPurchase.TryReadOrder(Form(("item", "laser_lf3"), ("quantity", new StringValues(["1", "2"])), ("currency", "credits"), ("idempotencyKey", key)), "session-one", out _, out _, out _, out _), Is.False);
        Assert.That(LocalShopPurchase.TryReadOrder(Form(("item", "laser_lf3"), ("quantity", "1"), ("currency", "credits"), ("idempotencyKey", key), ("price", "0")), "session-one", out _, out _, out _, out _), Is.False);
        Assert.That(LocalShopPurchase.TryReadOrder(Form(("item", "laser_lf3"), ("quantity", "1"), ("currency", "credits")), "session-one", out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void DatabaseShopPage_ShowsDetailStockLimitAndSignedPurchaseKey()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" };
        var listing = new EconomyListing("laser_lf3", "laser_lf3", "LF-3 laser", "laser", "rare",
            JsonSerializer.Deserialize<JsonElement>("{}"), null, JsonSerializer.Deserialize<JsonElement>("{\"credits\":1500}"), 3, 5, true);
        var page = LocalShop.PageFromDatabase(session, [listing], [new EconomyBalance("credits", "currency", 7000)],
            new Dictionary<string, long> { ["laser_lf3"] = 4 }, null, null,
            new Dictionary<string, long> { ["laser_lf3"] = 2 });
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Low stock"));
            Assert.That(page, Does.Contain("Limit 5 per account; purchased 2"));
            Assert.That(page, Does.Contain("action=internalShop&amp;item=laser_lf3"));
            Assert.That(page, Does.Contain("Owned: 4"));
            Assert.That(page, Does.Contain("rare"));
        });
    }

    [Test]
    public async Task RoutedShopPost_RejectsMissingSessionCompanyAndMalformedFormBeforeDatabaseAccess()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var equipment = new LocalHangarEquipmentStore(cache);
        var economy = new EconomyStore(new ConfigurationBuilder().Build());
        var session = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None)).Session;
        var body = $"item=laser_lf3&quantity=1&currency=credits&idempotencyKey={LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3")}";
        var missing = Request(null, body);
        await LocalShopPurchase.HandleAsync(missing, sessions, equipment, economy, CancellationToken.None);
        Assert.That(missing.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, body);
        await LocalShopPurchase.HandleAsync(noCompany, sessions, equipment, economy, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var tampered = Request(session.Dosid, body + "&price=0");
        await LocalShopPurchase.HandleAsync(tampered, sessions, equipment, economy, CancellationToken.None);
        Assert.That(tampered.Response.StatusCode, Is.EqualTo(400));
        Assert.That(tampered.Response.ContentType, Does.StartWith("text/html"));
    }

    private static DefaultHttpContext Request(string? dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalShopBuy");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
