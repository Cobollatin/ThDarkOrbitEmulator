using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.Operations;

public sealed class MeshWorldControlTests
{
    [Test]
    public async Task Teleport_UpdatesDistributedMapAndPresenceAndRepairsPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = new WorldSessionStore(cache);
        var second = new WorldSessionStore(cache);
        var pilot = new WorldPilot(901, "mmo", 1, 1, "ship", 0, 0)
        {
            X = 2000, Y = 1300,
            Stats = new WorldShipStats(200, 1000, 500, 0, 0, [])
        };
        pilot = await first.EnterAsync(pilot, default);
        first.SetCurrentShield(pilot, 10);
        var moved = await second.TeleportAsync(901, 5, 3400, 2100, true, default);
        Assert.That(moved, Is.Not.Null);
        // The former owner's next heartbeat must not resurrect map 1 presence.
        await first.RefreshLocalPresenceAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(moved!.MapId, Is.EqualTo(5));
            Assert.That(moved.X, Is.EqualTo(3400));
            Assert.That(first.ActivePilotCount, Is.Zero);
        });
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(5, default)).Select(p => p.UserId), Does.Contain(901));
        var reopened = await new WorldSessionStore(cache).EnterAsync(pilot, default);
        Assert.That(reopened.MapId, Is.EqualTo(5));
        var reloaded = new WorldSessionStore(cache);
        await reloaded.EnterAsync(pilot, default);
        Assert.That(reloaded.CurrentShield(reopened), Is.EqualTo(500));
    }
}
