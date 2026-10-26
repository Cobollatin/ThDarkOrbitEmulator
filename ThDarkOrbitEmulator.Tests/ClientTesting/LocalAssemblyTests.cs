using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAssemblyTests
{
    [Test]
    public async Task Craft_DebitsLocalResourcesAndAddsSharedHangarInventory()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.CraftAsync(account, "missing", CancellationToken.None), Is.False);
        Assert.That(await store.CraftAsync(account, "lf3", CancellationToken.None), Is.True);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.AssemblyComponents, Is.EqualTo(850));
            Assert.That(state.Credits, Is.EqualTo(9500));
            Assert.That(state.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(5));
        });
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).AssemblyComponents, Is.EqualTo(1000));
    }

    [Test]
    public async Task Craft_RejectsWhenResourcesRunOutWithoutPartialDebit()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        for (var i = 0; i < 5; i++) Assert.That(await store.CraftAsync(account, "flax", CancellationToken.None), Is.True);
        Assert.That(await store.CraftAsync(account, "flax", CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.AssemblyComponents, Is.Zero);
            Assert.That(state.Credits, Is.EqualTo(7000));
            Assert.That(state.Inventory.Single(item => item.Id == "drone_flax").Quantity, Is.EqualTo(6));
        });
    }

    [Test]
    public async Task Action_RequiresSessionAndCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "recipe=lf3");
        await LocalAssemblyAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "recipe=lf3");
        await LocalAssemblyAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var invalid = Request(session.Dosid, "recipe=missing");
        await LocalAssemblyAction.HandleAsync(invalid, sessions, store, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        var valid = Request(session.Dosid, "recipe=lf3");
        await LocalAssemblyAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAssembly"));
    }

    [Test]
    public void Page_ShowsRecipesAndEscapesPilot()
    {
        var page = LocalAssembly.Page(new GameAuthSession { Username = "<pilot>" },
            new LocalEquipmentState(
            [new("laser_lf3", "LF-3 laser", "Equipment", 4),
             new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 4),
             new("drone_flax", "Flax drone", "Drones", 1)], []));
        Assert.That(page, Does.Contain("id=\"assembly\""));
        Assert.That(page, Does.Contain("action=internalAssemblyCraft"));
        Assert.That(page, Does.Contain("&lt;pilot&gt;"));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalAssembly"));
    }

    private static DefaultHttpContext Request(string? dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalAssemblyCraft");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
