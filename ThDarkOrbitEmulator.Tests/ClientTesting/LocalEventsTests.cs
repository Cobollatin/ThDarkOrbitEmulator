using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalEventsTests
{
    [Test]
    public async Task BattlePass_RequiresProgress_AndClaimsEachRewardOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.ClaimBattlePassAsync(account, "supplier", CancellationToken.None), Is.False);
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimBattlePassAsync(account, "supplier", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimBattlePassAsync(account, "supplier", CancellationToken.None), Is.False);
        Assert.That(await store.CraftAsync(account, "flax", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimBattlePassAsync(account, "engineer", CancellationToken.None), Is.True);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.ClaimedBattlePassTiers, Is.EquivalentTo(new[] { "supplier", "engineer" }));
        Assert.That(state.Credits, Is.EqualTo(10000 - LocalShop.Catalog.Single(x => x.Id == "laser_lf3").Price - 600 + 500));
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).ClaimedBattlePassTiers, Is.Empty);
    }

    [Test]
    public async Task Expedition_DebitsOnce_Waits_AndGrantsOneShield()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.That(await store.StartExpeditionAsync(account, now, CancellationToken.None), Is.True);
        Assert.That(await store.StartExpeditionAsync(account, now, CancellationToken.None), Is.False);
        Assert.That(await store.ClaimExpeditionAsync(account, now.AddMinutes(1), CancellationToken.None), Is.False);
        Assert.That(await store.ClaimExpeditionAsync(account, now.AddMinutes(2), CancellationToken.None), Is.True);
        Assert.That(await store.ClaimExpeditionAsync(account, now.AddMinutes(3), CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(9900));
        Assert.That(state.Inventory.Single(x => x.Id == "shield_sg3n_b02").Quantity, Is.EqualTo(5));
        Assert.That(LocalEvents.Page(state, now.AddMinutes(3)), Does.Contain("Reward claimed"));
    }

    [Test]
    public async Task Expedition_FullRewardStack_DoesNotClaimOrChangeInventory()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalHangarEquipmentStore(cache);
        var account = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var state = await store.GetAsync(account, CancellationToken.None);
        state.ExpeditionReadyAt = now;
        var index = state.Inventory.FindIndex(item => item.Id == LocalEvents.ExpeditionRewardItemId);
        state.Inventory[index] = state.Inventory[index] with { Quantity = int.MaxValue };
        await cache.SetStringAsync($"localhangar:equipment:{account:N}", JsonSerializer.Serialize(state), CancellationToken.None);

        Assert.That(await store.ClaimExpeditionResultAsync(account, now, CancellationToken.None),
            Is.EqualTo(LocalEventResult.InventoryFull));
        var after = await store.GetAsync(account, CancellationToken.None);
        Assert.That(after.ExpeditionClaimed, Is.False);
        Assert.That(after.Inventory[index].Quantity, Is.EqualTo(int.MaxValue));
        Assert.That(LocalEvents.Page(after, now), Does.Not.Contain("name=\"claim\""));
    }

    [Test]
    public async Task EventPost_RequiresSessionAndCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null);
        await LocalEventsAction.HandleAsync(anonymous, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid);
        await LocalEventsAction.HandleAsync(noCompany, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var valid = Request(session.Dosid);
        await LocalEventsAction.HandleAsync(valid, sessions, store, TimeProvider.System, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalEvents&expedition=started"));
    }

    private static DefaultHttpContext Request(string? dosid)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalExpeditionStart");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes("start=1");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
