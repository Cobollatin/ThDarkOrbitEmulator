using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarEquipmentTests
{
    [Test]
    public async Task Equipment_PersistsPerAccount_AndCannotBeDuplicatedAcrossShips()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.That(await store.AssignAsync(account, 1, 1, "laser_lf3", 3, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 1, 2, "laser_lf3", 4, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 2, 1, "laser_lf3", 1, CancellationToken.None), Is.False);
        Assert.That(await store.AssignAsync(account, 1, 1, "laser_lf3", 5, CancellationToken.None), Is.False);
        Assert.That(await store.AssignAsync(account, 1, 1, "unknown", 1, CancellationToken.None), Is.False);
        Assert.That(await store.AssignAsync(account, 1, 3, "laser_lf3", 1, CancellationToken.None), Is.False);
        Assert.That(await store.AssignAsync(account, 1, 2, "laser_lf3", 0, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 2, 1, "laser_lf3", 1, CancellationToken.None), Is.True);

        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Assignments, Has.Count.EqualTo(2));
        Assert.That((await store.GetAsync(other, CancellationToken.None)).Assignments, Is.Empty);
        Assert.That(state.Inventory.Select(item => item.Category), Does.Contain("Drones"));
        Assert.That(state.Inventory.Select(item => item.Category), Does.Contain("PET"));
        Assert.That(state.Inventory.Select(item => item.Name), Does.Contain("Iris drone"));
        Assert.That(state.Inventory.Select(item => item.Name), Does.Contain("LF-2 laser"));
    }

    [Test]
    public async Task Selection_RequiresSessionAndCompany_AndRendersSavedConfiguration()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var equipment = new LocalHangarEquipmentStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;

        var anonymous = Request(null, "slot=1&configuration=1&item=drone_flax&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(anonymous, sessions, equipment, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));

        var noCompany = Request(session.Dosid, "slot=1&configuration=1&item=drone_flax&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(noCompany, sessions, equipment, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        var invalid = Request(session.Dosid, "slot=3&configuration=1&item=drone_flax&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(invalid, sessions, equipment, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));

        var valid = Request(session.Dosid, "slot=1&configuration=1&item=drone_flax&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(valid, sessions, equipment, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Does.Contain("slot=1&configuration=1"));
        var page = LocalHangarEquipment.Page((await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!, 1, 1,
            await equipment.GetAsync(account.Id, CancellationToken.None));
        Assert.That(page, Does.Contain("Pusat Plus equipment"));
        Assert.That(page, Does.Contain("Flax drone"));
        Assert.That(page, Does.Contain("P.E.T. 10"));
        Assert.That(page, Does.Contain("Configuration 2"));
        Assert.That(LocalHangars.Page(new GameAuthSession { GameUserId = 7, Company = "mmo" }, 1),
            Does.Contain("action=internalDockEquipment&amp;slot=1"));
    }

    [TestCase("slot=1&configuration=1&item=laser_lf3&quantity=-1")]
    [TestCase("slot=1&configuration=1&item=laser_lf3&quantity=2147483648")]
    [TestCase("slot=1&configuration=1&item=laser_lf3&quantity=5")]
    [TestCase("slot=1&configuration=1&item=unknown&quantity=1")]
    [TestCase("slot=1&configuration=1&item=laser_lf3&quantity=1&quantity=2")]
    [TestCase("slot=1&configuration=1&item=laser_lf3&quantity=1&account=other")]
    [TestCase("slot=1&configuration=1&item=laser_lf3")]
    public async Task EquipmentPost_RejectsMalformedOrTamperedForm(string body)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var equipment = new LocalHangarEquipmentStore(cache);
        var request = Request(session.Dosid, body);
        await LocalHangarEquipmentSelection.HandleAsync(request, sessions, equipment, CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(400));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.Empty);
    }

    [Test]
    public async Task EquipmentPost_RejectsReplayAndExpiredSession()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var equipment = new LocalHangarEquipmentStore(cache);
        const string body = "slot=1&configuration=1&item=laser_lf3&quantity=2";
        var first = Request(session.Dosid, body);
        await LocalHangarEquipmentSelection.HandleAsync(first, sessions, equipment, CancellationToken.None);
        var replay = Request(session.Dosid, body);
        await LocalHangarEquipmentSelection.HandleAsync(replay, sessions, equipment, CancellationToken.None);
        await sessions.InvalidateAsync(session.Dosid, CancellationToken.None);
        var expired = Request(session.Dosid, body);
        await LocalHangarEquipmentSelection.HandleAsync(expired, sessions, equipment, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        });
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentStores_KeepSharedCreditsAndAssignmentsConsistent()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var stores = Enumerable.Range(0, 12).Select(_ => new LocalHangarEquipmentStore(cache)).ToArray();
        var account = Guid.NewGuid();
        var purchases = await Task.WhenAll(stores.Select(store =>
            store.PurchaseAsync(account, "laser_lf3", 1, CancellationToken.None)));
        Assert.That(purchases.Count(success => success), Is.EqualTo(6));
        // Menu-134: the stock race uses shields (4 owned), since each ship's laser slots are below the bought stock.
        var assignments = await Task.WhenAll(stores.Take(2).Select((store, i) =>
            store.SetAssignmentAsync(account, i + 1, 1, "shield_sg3n_b02", i == 0 ? 4 : 1, CancellationToken.None)));
        var state = await stores[0].GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(1000));
            Assert.That(state.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(10));
            Assert.That(assignments.Count(result => result == LocalHangarChange.Applied), Is.EqualTo(1));
            Assert.That(state.Assignments.Sum(item => item.Quantity), Is.AnyOf(1, 4));
        });
    }

    [Test]
    public async Task EquipmentCacheExpiry_ResetsLocalInventoryAndAssignmentsForOnlyThatAccount()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalHangarEquipmentStore(cache);
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.That(await store.PurchaseAsync(account, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 1, 1, "drone_flax", 2, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(other, 1, 1, "drone_flax", 1, CancellationToken.None), Is.True);
        await cache.RemoveAsync($"localhangar:equipment:{account:N}", CancellationToken.None);
        var reset = await new LocalHangarEquipmentStore(cache).GetAsync(account, CancellationToken.None);
        var preserved = await store.GetAsync(other, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(reset.Credits, Is.EqualTo(10000));
            Assert.That(reset.Inventory.Single(item => item.Id == "drone_flax").Quantity, Is.EqualTo(1));
            Assert.That(reset.Assignments, Is.Empty);
            Assert.That(preserved.Assignments, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task SavedAssignments_AreCappedWhenSharedInventoryShrinks()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalHangarEquipmentStore(cache);
        var account = Guid.NewGuid();
        Assert.That(await store.AssignAsync(account, 1, 1, "laser_lf3", 3, CancellationToken.None), Is.True);
        Assert.That(await store.AssignAsync(account, 2, 1, "laser_lf3", 1, CancellationToken.None), Is.True);
        var key = $"localhangar:equipment:{account:N}";
        var saved = JsonSerializer.Deserialize<LocalEquipmentState>((await cache.GetStringAsync(key))!)!;
        var inventory = saved.Inventory.Select(item => item.Id == "laser_lf3" ? item with { Quantity = 2 } : item).ToList();
        await cache.SetStringAsync(key, JsonSerializer.Serialize(saved with { Inventory = inventory }));

        var reconciled = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(reconciled.Assignments.Single(a => a.HangarSlot == 1).Quantity, Is.EqualTo(2));
            Assert.That(reconciled.Assignments.Any(a => a.HangarSlot == 2), Is.False);
            Assert.That(LocalHangarEquipment.Page(new GameAuthSession { Company = "mmo", GameUserId = 7 }, 1, 1,
                reconciled), Does.Contain("value=\"2\""));
        });
        Assert.That(await store.SetAssignmentAsync(account, 2, 1, "laser_lf3", 1, CancellationToken.None),
            Is.EqualTo(LocalHangarChange.Conflict));
    }

    [Test]
    public async Task ConcurrentEquipmentSubmissions_ApplyOnlyOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var requests = Enumerable.Range(0, 8).Select(_ => Request(session.Dosid,
            "slot=1&configuration=1&item=drone_flax&quantity=1")).ToArray();
        await Task.WhenAll(requests.Select(context => LocalHangarEquipmentSelection.HandleAsync(
            context, sessions, new LocalHangarEquipmentStore(cache), CancellationToken.None)));
        Assert.That(requests.Count(context => context.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That(requests.Count(context => context.Response.StatusCode == 409), Is.EqualTo(7));
        Assert.That((await new LocalHangarEquipmentStore(cache).GetAsync(account.Id, CancellationToken.None))
            .Assignments, Has.Count.EqualTo(1));
    }

    private static DefaultHttpContext Request(string? dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalDockEquipmentSet");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null)
            context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
