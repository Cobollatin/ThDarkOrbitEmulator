using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class WorldTransportTests
{
    [Test]
    public async Task Loopback_OwnSameMapAdminTeleport_SnapsIdleSocketOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var socketStore = new WorldSessionStore(cache);
        var adminStore = new WorldSessionStore(cache);
        var pilot = new WorldPilot(852, "mmo", 1, 1, "ship", 0, 0)
        { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), socketStore);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(852, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, pilot.X); WorldPayload.WriteInt32(w, 2100);
                WorldPayload.WriteInt32(w, 9000); WorldPayload.WriteInt32(w, pilot.Y);
            }));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.MoveCommand));

            var changed = await adminStore.TeleportAsync(852, 1, 3400, 2100, false, CancellationToken.None);
            Assert.That(changed, Is.Not.Null);
            var packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var move = WorldPayload.Decode(packet.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
            Assert.That(move, Is.EqualTo((852L, 3400, 2100, 0)));
            Assert.That(socketStore.Find(852), Has.Property("X").EqualTo(3400));
            var safe = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(safe.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
            Assert.That(safe.Payload.ToArray(), Is.EqualTo(new byte[] { 0 }));
            await Task.Delay(1200);
            Assert.That(client.Available, Is.Zero, "A stationary teleport must be announced only once.");
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 3400); WorldPayload.WriteInt32(w, 2100);
                WorldPayload.WriteInt32(w, 3700); WorldPayload.WriteInt32(w, 2100);
            }));
            var resumed = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(resumed.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var resumedMove = WorldPayload.Decode(resumed.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
            Assert.Multiple(() =>
            {
                Assert.That(resumedMove.Id, Is.EqualTo(852));
                Assert.That(resumedMove.X, Is.EqualTo(3700));
                Assert.That(resumedMove.Y, Is.EqualTo(2100));
                Assert.That(resumedMove.Duration, Is.InRange(1, 1500));
            });
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_RemoteSameMapTeleport_SnapsVisiblePilotOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var nodeA = new WorldSessionStore(cache);
        var nodeB = new WorldSessionStore(cache);
        var observer = new WorldPilot(850, "mmo", 1, 1, "ship", 0, 0)
        { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var remote = observer with { UserId = 851, X = 1000, Y = 1000, UserName = "remote851" };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((observer, default)), nodeA);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(850, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var entered = await nodeB.EnterAsync(remote, CancellationToken.None);
            var spawn = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(spawn.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
            Assert.That(WorldPayload.Decode(spawn.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))).Item1, Is.EqualTo(851));

            await nodeB.MoveAsync(entered, 4000, 2500, null, CancellationToken.None);
            var travel = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            if (travel.Command == WorldCommandId.CloakStateCommand)
                travel = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(travel.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            await nodeB.TeleportAsync(851, 1, 3400, 2100, false, CancellationToken.None);
            var snap = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(snap.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var move = WorldPayload.Decode(snap.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
            Assert.That(move, Is.EqualTo((851L, 3400, 2100, 0)));
            await Task.Delay(1200);
            Assert.That(client.Available, Is.Zero, "The same saved position must not be announced twice.");
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_RemoteReplicaBrowserMove_ReachesIdleTcpObserver()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var socketStore = new WorldSessionStore(cache);
        var browserStore = new WorldSessionStore(cache);
        var pilot = new WorldPilot(805, "mmo", 1, 1, "ship", 0, 0)
        { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), socketStore);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(805, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await browserStore.MoveAsync(pilot, 5000, 1300, null, CancellationToken.None);
            var first = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(first.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var result = WorldPayload.Decode(first.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
            Assert.Multiple(() =>
            {
                Assert.That(result.Id, Is.EqualTo(805));
                Assert.That(result.X, Is.EqualTo(5000));
                Assert.That(result.Y, Is.EqualTo(1300));
                Assert.That(result.Duration, Is.GreaterThan(0));
            });
            await Task.Delay(1200);
            Assert.That(client.Available, Is.Zero, "The same saved trajectory should be announced only once.");
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ExternalMove_FailedOwningSocketWrite_IsRetriedOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var socketStore = new WorldSessionStore(cache);
        var browserStore = new WorldSessionStore(cache);
        var pilot = await socketStore.EnterAsync(new WorldPilot(983, "pilot", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        using var wire = new FailNextWriteStream();
        socketStore.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        await socketStore.PrimeObservedTravelAsync(pilot, default);

        await browserStore.MoveAsync(pilot, 4000, 2500, null, default);
        wire.FailNext();
        var first = await socketStore.ObserveExternalMoveAsync(pilot, default);
        Assert.That(first, Is.Not.Null);
        await socketStore.BroadcastMoveAsync(pilot, first!, default, localMutation: false);

        var retry = await socketStore.ObserveExternalMoveAsync(pilot, default);
        Assert.That(retry, Is.Not.Null);
        await socketStore.BroadcastMoveAsync(pilot, retry!, default, localMutation: false);
        Assert.That(await socketStore.ObserveExternalMoveAsync(pilot, default), Is.Null);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var move = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.That(move.Id, Is.EqualTo(983));
        Assert.That((move.X, move.Y), Is.EqualTo((4000, 2500)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task LocalMove_FailedOwningSocketWrite_IsRetriedOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = await store.EnterAsync(new WorldPilot(984, "pilot", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        using var wire = new FailNextWriteStream();
        store.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        await store.PrimeObservedTravelAsync(pilot, default);

        var move = await store.MoveAsync(pilot, 4000, 2500, null, default);
        wire.FailNext();
        await store.BroadcastMoveAsync(pilot, move, default);
        var retry = await store.ObserveExternalMoveAsync(pilot, default);
        Assert.That(retry, Is.Not.Null);
        await store.BroadcastMoveAsync(pilot, retry!, default, localMutation: false);
        Assert.That(await store.ObserveExternalMoveAsync(pilot, default), Is.Null);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var delivered = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.That(delivered.Id, Is.EqualTo(984));
        Assert.That((delivered.X, delivered.Y), Is.EqualTo((4000, 2500)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task Loopback_PilotMovingOnAnotherNode_ReachesLocalTcpObserverOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var nodeA = new WorldSessionStore(cache);
        var nodeB = new WorldSessionStore(cache);
        var observer = new WorldPilot(806, "mmo", 1, 1, "ship", 0, 0)
        { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var remote = new WorldPilot(807, "mmo", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, UserName = "remote807", FactionId = 1,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((observer, default)), nodeA);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(806, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var entered = await nodeB.EnterAsync(remote, CancellationToken.None);
            var spawn = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(spawn.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
            var created = WorldPayload.Decode(spawn.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), Name: WorldPayload.ReadString(r),
                    Ship: WorldPayload.ReadString(r), Faction: WorldPayload.ReadInt32(r),
                    Map: WorldPayload.ReadInt32(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r)));
            Assert.Multiple(() =>
            {
                Assert.That(created.Id, Is.EqualTo(807));
                Assert.That(created.Name, Is.EqualTo("remote807"));
                Assert.That(created.Ship, Is.EqualTo("ship"));
                Assert.That(created.Faction, Is.EqualTo(1));
                Assert.That(created.Map, Is.EqualTo(1));
                Assert.That(created.X, Is.EqualTo(1000));
                Assert.That(created.Y, Is.EqualTo(1000));
            });
            await nodeB.MoveAsync(entered, 4000, 2500, null, CancellationToken.None);
            var packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            if (packet.Command == WorldCommandId.CloakStateCommand)
                packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var move = WorldPayload.Decode(packet.Payload, r =>
                (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                    Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
            Assert.Multiple(() =>
            {
                Assert.That(move.Id, Is.EqualTo(807));
                Assert.That(move.X, Is.EqualTo(4000));
                Assert.That(move.Y, Is.EqualTo(2500));
                Assert.That(move.Duration, Is.GreaterThan(0));
            });
            await Task.Delay(1200);
            Assert.That(client.Available, Is.Zero, "A remote trajectory should be announced once per observer.");
            await nodeB.LeaveAsync(entered, null, CancellationToken.None);
            var removed = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(removed.Command, Is.EqualTo(WorldCommandId.PilotRemoveCommand));
            Assert.That(WorldPayload.Decode(removed.Payload, WorldPayload.ReadInt64), Is.EqualTo(807));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task RemoteObservers_OnSameMap_SharePresenceReadAndValidateEachMove()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var counting = new CountingCache(services.GetRequiredService<IDistributedCache>());
        var nodeA = new WorldSessionStore(counting);
        var nodeB = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var remote = new WorldPilot(811, "mmo", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var entered = await nodeB.EnterAsync(remote, CancellationToken.None);
        await nodeB.MoveAsync(entered, 4000, 2500, null, CancellationToken.None);
        var observers = Enumerable.Range(0, 3).Select(i => new WorldPilot(820 + i, "mmo", 1, 1, "ship", 0, 0)
        { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }).ToArray();
        var sent = new List<WorldCommandId>();
        counting.Reads.Clear();

        foreach (var observer in observers)
        {
            using var wire = new MemoryStream();
            var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
            await nodeA.ObserveRemotePilotsAsync(observer, connection, CancellationToken.None);
            foreach (var move in await nodeA.ObserveRemoteMovesAsync(observer, CancellationToken.None))
                await nodeA.SendMoveAsync(connection, move, CancellationToken.None);
            wire.Position = 0;
            var reader = new WorldPacketReader(wire);
            while (await reader.ReadAsync() is { } packet) sent.Add(packet.Command);
        }

        Assert.Multiple(() =>
        {
            Assert.That(counting.Reads.Count(k => k == "world:presence:map:1"), Is.EqualTo(7),
                "One shared snapshot read plus current-membership checks before creation and movement.");
            Assert.That(counting.Reads.Count(k => k == "world:position:811"), Is.EqualTo(4),
                "One snapshot read plus one current-travel check per delivered move.");
            Assert.That(counting.Reads.Count(k => k == "world:runtime:811"), Is.EqualTo(4),
                "Each first visibility poll reads current heal history after the shared snapshot.");
            Assert.That(sent.Count(s => s == WorldCommandId.PilotCreateCommand), Is.EqualTo(3));
            Assert.That(sent.Count(s => s == WorldCommandId.MoveCommand), Is.EqualTo(3));
        });

        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await nodeA.ObserveRemoteMovesAsync(observers[0], CancellationToken.None);
        Assert.That(counting.Reads.Count(k => k == "world:presence:map:1"), Is.EqualTo(8),
            "An expired snapshot must re-read shared presence.");
    }

    [Test]
    public async Task RemotePilot_LeftAfterSnapshot_IsNotCreated()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(967, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 968 };

        await observerNode.ObserveRemoteMovesAsync(observer, default);
        await owner.LeaveAsync(remote, null, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        Assert.That(wire.Length, Is.Zero);
    }

    [Test]
    public async Task RemotePilot_ReturnedAfterSnapshot_IsCreatedAtCurrentPosition()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(975, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 976 };
        await observerNode.ObserveRemoteMovesAsync(observer, default);
        await owner.LeaveAsync(remote, null, default);
        var returned = remote with { X = 2300, Y = 1800 };
        await owner.PersistAsync(returned, default);
        await owner.EnterAsync(returned, default);

        using var wire = new MemoryStream();
        await observerNode.ObserveRemotePilotsAsync(observer,
            new WorldConnection(observer, new WorldPacketWriter(wire)), default);
        wire.Position = 0;
        var packet = await new WorldPacketReader(wire).ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        var created = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), Name: WorldPayload.ReadString(r),
                Ship: WorldPayload.ReadString(r), Faction: WorldPayload.ReadInt32(r),
                Map: WorldPayload.ReadInt32(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r)));
        Assert.That((created.X, created.Y), Is.EqualTo((2300, 1800)));
    }

    [Test]
    public async Task RemotePilot_CloakedAfterSnapshot_UpdatesVisibleObserver()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(977, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 978 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        remote = await owner.SetCloakAsync(remote, true, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        remote = await owner.SetCloakAsync(remote, false, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        var initial = await reader.ReadAsync();
        Assert.That(initial?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(WorldPayload.Decode(initial!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((977L, false)));
        var cloak = await reader.ReadAsync();
        Assert.That(cloak?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(WorldPayload.Decode(cloak!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((977L, true)));
        var revealed = await reader.ReadAsync();
        Assert.That(revealed?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(WorldPayload.Decode(revealed!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((977L, false)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemotePilot_LeftWhileCreationWaitsForSocket_IsNotCreated()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(969, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 970 };
        await observerNode.ObserveRemoteMovesAsync(observer, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        var gate = (SemaphoreSlim)typeof(WorldConnection)
            .GetProperty("RemoteMoveDeliveryGate", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(connection)!;

        await gate.WaitAsync();
        var creation = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        Assert.That(creation.IsCompleted, Is.False);
        await owner.LeaveAsync(remote, null, default);
        gate.Release();
        await creation;

        Assert.That(wire.Length, Is.Zero);
    }

    [Test]
    public async Task RemoteMove_WaitsForPilotCreation_ThenDeliversOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(963, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 964 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        var selected = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        await observerNode.SendMoveAsync(connection, selected, default);
        Assert.That(wire.Length, Is.Zero);

        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteMove_DuringFailedPilotCreation_IsNotDelivered()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(965, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 966 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        var selected = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        using var wire = new PauseFirstWriteStream(failFirstOnResume: true);
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        var creation = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await wire.FirstWriteStarted;
        var movement = observerNode.SendMoveAsync(connection, selected, default);
        wire.Resume();
        Assert.ThrowsAsync<IOException>(async () => await creation);
        await movement;

        Assert.That(wire.Length, Is.Zero);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.SendMoveAsync(connection, selected, default);
        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    private static async Task AnnounceRemotePilotAsync(WorldSessionStore node, WorldPilot observer)
    {
        using var wire = new MemoryStream();
        await node.ObserveRemotePilotsAsync(observer,
            new WorldConnection(observer, new WorldPacketWriter(wire)), default);
    }

    [Test]
    public async Task RemoteMovement_UsesCurrentTravelBeforeMapSnapshotExpires()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(961, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 962 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        await owner.MoveAsync(remote, 4000, 2500, null, default);
        var first = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await owner.MoveAsync(remote, 5000, 3000, null, default);
        await observerNode.SendMoveAsync(connection, first, default);
        var latest = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await observerNode.SendMoveAsync(connection, latest, default);
        Assert.That(await observerNode.ObserveRemoteMovesAsync(observer, default), Is.Empty);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var destination = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.Multiple(() =>
        {
            Assert.That(destination.Id, Is.EqualTo(961));
            Assert.That(destination.X, Is.EqualTo(5000));
            Assert.That(destination.Y, Is.EqualTo(3000));
            Assert.That(destination.Duration, Is.GreaterThan(0));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteMovement_CompletedBetweenPolls_SendsFinalPositionOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(981, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 982 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        await owner.MoveAsync(remote, 1010, 1000, null, default,
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);
        Assert.That(await observerNode.ObserveRemoteMovesAsync(observer, default), Is.Empty);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var position = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.That(position, Is.EqualTo((981L, 1010, 1000, 0)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FailedRemoteMoveWrite_IsRetriedOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(953, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 954 };
        using var wire = new FailNextWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));

        wire.FailNext();
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);
        foreach (var move in await observerNode.ObserveRemoteMovesAsync(observer, default))
            await observerNode.SendMoveAsync(connection, move, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        var delivered = await reader.ReadAsync();
        Assert.That(delivered?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var payload = WorldPayload.Decode(delivered!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.Multiple(() =>
        {
            Assert.That(payload.Id, Is.EqualTo(953L));
            Assert.That(payload.X, Is.EqualTo(4000));
            Assert.That(payload.Y, Is.EqualTo(2500));
            Assert.That(payload.Duration, Is.GreaterThan(0));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConcurrentRemoteMoveDelivery_SendsTrajectoryOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(955, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 956 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        await AnnounceRemotePilotAsync(observerNode, observer);
        var move = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        using var wire = new PauseFirstWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        var first = observerNode.SendMoveAsync(connection, move, default);
        await wire.FirstWriteStarted;
        var second = observerNode.SendMoveAsync(connection, move, default);
        wire.Resume();
        await Task.WhenAll(first, second);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
        Assert.That(await observerNode.ObserveRemoteMovesAsync(observer, default), Is.Empty);
    }

    [Test]
    public async Task StaleRemoteMove_IsSkippedAfterOwnerChangesCourse()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(957, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 958 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        await AnnounceRemotePilotAsync(observerNode, observer);
        var stale = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await owner.MoveAsync(remote, 5000, 3000, null, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        await observerNode.SendMoveAsync(connection, stale, default);
        await observerNode.SendMoveAsync(connection,
            new WorldRemoteMove(new WorldMoveResult(957, 1000, 1000, 0, false), null), default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        var latest = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await observerNode.SendMoveAsync(connection, latest, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var destination = WorldPayload.Decode(packet!.Value.Payload, r =>
            (Id: WorldPayload.ReadInt64(r), X: WorldPayload.ReadInt32(r),
                Y: WorldPayload.ReadInt32(r), Duration: WorldPayload.ReadInt32(r)));
        Assert.Multiple(() =>
        {
            Assert.That(destination.Id, Is.EqualTo(957));
            Assert.That(destination.X, Is.EqualTo(5000));
            Assert.That(destination.Y, Is.EqualTo(3000));
            Assert.That(destination.Duration, Is.GreaterThan(0));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteMove_SelectedBeforePilotLeaves_IsNotDelivered()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(959, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 960 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        await AnnounceRemotePilotAsync(observerNode, observer);
        var selected = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await owner.LeaveAsync(remote, null, default);

        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.SendMoveAsync(connection, selected, default);

        Assert.That(wire.Length, Is.Zero);
    }

    [Test]
    public async Task RemoteMove_DeliveryUsesRemainingTravelTime()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var remote = await owner.EnterAsync(new WorldPilot(961, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = remote with { UserId = 962 };
        await owner.MoveAsync(remote, 4000, 2500, null, default);
        await AnnounceRemotePilotAsync(observerNode, observer);
        var selected = (await observerNode.ObserveRemoteMovesAsync(observer, default)).Single();
        await Task.Delay(200);

        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.SendMoveAsync(connection, selected, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveCommand));
        var duration = WorldPayload.Decode(packet!.Value.Payload, r =>
        {
            WorldPayload.ReadInt64(r);
            WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt32(r);
            return WorldPayload.ReadInt32(r);
        });
        Assert.That(duration, Is.InRange(1, selected.Move.TimeToTargetMs - 100));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteCloakChanges_ReachObservers_AndHeartbeatKeepsSharedState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var observerNode = new WorldSessionStore(cache, clock: clock);
        var pilotNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await pilotNode.EnterAsync(new WorldPilot(912, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = new WorldPilot(913, "observer", 1, 1, "ship", 0, 0);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        pilot = await observerNode.SetCloakAsync(pilot, true, default);
        clock.Advance(WorldSessionStore.PresenceHeartbeatInterval + TimeSpan.FromSeconds(1));
        await pilotNode.RefreshLocalPresenceAsync(default);
        Assert.That((await observerNode.ConnectedAsync(1, default)).Single().IsInvisible, Is.True);
        Assert.That((await pilotNode.ConnectedAsync(1, default)).Single().IsInvisible, Is.True);

        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await pilotNode.MarkCombatAsync(pilot, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        foreach (var active in new[] { false, true, false })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((912L, active)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FailedRemoteCloakWrite_IsRetriedOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var pilot = await owner.EnterAsync(new WorldPilot(949, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = pilot with { UserId = 950 };
        using var wire = new FailNextWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        pilot = await owner.SetCloakAsync(pilot, true, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        wire.FailNext();
        Assert.ThrowsAsync<IOException>(async () =>
            await observerNode.ObserveRemotePilotsAsync(observer, connection, default));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        foreach (var active in new[] { false, true })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((949L, active)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FailedRemotePilotRemoval_IsRetriedOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var pilot = await owner.EnterAsync(new WorldPilot(951, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = pilot with { UserId = 952 };
        using var wire = new FailNextWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        await owner.LeaveAsync(pilot, null, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        wire.FailNext();
        Assert.ThrowsAsync<IOException>(async () =>
            await observerNode.ObserveRemotePilotsAsync(observer, connection, default));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        var removal = await reader.ReadAsync();
        Assert.That(removal?.Command, Is.EqualTo(WorldCommandId.PilotRemoveCommand));
        Assert.That(WorldPayload.Decode(removal!.Value.Payload, WorldPayload.ReadInt64), Is.EqualTo(951));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConcurrentRemotePilotRemoval_IsSentOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var pilot = await owner.EnterAsync(new WorldPilot(953, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = pilot with { UserId = 954 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await owner.LeaveAsync(pilot, null, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));

        var gate = (SemaphoreSlim)typeof(WorldConnection)
            .GetProperty("RemoteMoveDeliveryGate", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(connection)!;
        await gate.WaitAsync();
        var first = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        var second = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        Assert.Multiple(() =>
        {
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);
        });
        gate.Release();
        await Task.WhenAll(first, second);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotRemoveCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemotePilotReturn_WhileRemovalWaits_KeepsAnnouncedShip()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var pilot = await owner.EnterAsync(new WorldPilot(957, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = pilot with { UserId = 958 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await owner.LeaveAsync(pilot, null, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));

        var gate = (SemaphoreSlim)typeof(WorldConnection)
            .GetProperty("RemoteMoveDeliveryGate", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(connection)!;
        await gate.WaitAsync();
        try
        {
            var removal = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
            Assert.That(removal.IsCompleted, Is.False);
            await owner.EnterAsync(pilot, default);
            gate.Release();
            await removal;
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemotePilotRemoval_WaitsForCreationFollowup()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var owner = new WorldSessionStore(cache);
        var observerNode = new WorldSessionStore(cache);
        var pilot = await owner.EnterAsync(new WorldPilot(955, "remote", 1, 1, "ship", 0, 0)
        { X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) }, default);
        var observer = pilot with { UserId = 956 };
        using var wire = new PauseSecondWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        var creation = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await wire.SecondWriteStarted;
        try
        {
            Assert.That(creation.IsCompleted, Is.False);
            await owner.LeaveAsync(pilot, null, default);
            await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
            var removal = observerNode.ObserveRemotePilotsAsync(observer, connection, default);
            Assert.That(removal.IsCompleted, Is.False);
            wire.Resume();
            await Task.WhenAll(creation, removal);
        }
        finally
        {
            wire.Resume();
        }

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotRemoveCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteStationHeal_ReachesVisibleObserverOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var observerNode = new WorldSessionStore(cache, clock: clock);
        var pilotNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await pilotNode.EnterAsync(new WorldPilot(920, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await pilotNode.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = pilot with { UserId = 921 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await pilotNode.RepairAtBaseAsync(pilot, clock.GetUtcNow(), default), Is.True);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((920L, 100L, 1000L)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteRepairBotChange_ReachesOwningPilotOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(922, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        socketNode.Attach(connection);

        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        Assert.That(wire.Length, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        await mapOwner.MarkCombatAsync(pilot, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        foreach (var active in new[] { true, false })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.RepairBotStateCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, active)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteRepairBotChanges_ReplayActivationAndDeactivationBetweenPolls()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(927, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        await socketNode.PrimeOwnRepairBotAsync(pilot, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        socketNode.Attach(connection);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await mapOwner.MarkCombatAsync(pilot, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        await socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        foreach (var active in new[] { true, false })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.RepairBotStateCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, active)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task LaterLocalRepairBotChange_ReplaysEarlierFailedChange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(936, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await sessions.PrimeOwnRepairBotAsync(pilot, default);
        using var wire = new FailFirstHealWriteStream();
        sessions.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));

        clock.Advance(TimeSpan.FromSeconds(10));
        await sessions.RecoverHullAsync(pilot.MapId, default);
        await sessions.MarkCombatAsync(pilot, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        foreach (var active in new[] { true, false })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.RepairBotStateCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, active)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConcurrentRepairBotObservations_SendTransitionOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(937, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        await socketNode.PrimeOwnRepairBotAsync(pilot, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);

        using var wire = new PauseFirstWriteStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        var first = socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        await wire.FirstWriteStarted;
        var second = socketNode.ObserveOwnRepairBotAsync(pilot, connection, default);
        wire.Resume();
        await Task.WhenAll(first, second);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.RepairBotStateCommand));
        Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, true)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteHullHeal_ReachesOwningPilotOnceAfterEntry()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(923, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        await socketNode.PrimeOwnHealAsync(pilot, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        socketNode.Attach(connection);

        await socketNode.ObserveOwnHealAsync(pilot, connection, default);
        Assert.That(wire.Length, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await mapOwner.RepairAtBaseAsync(pilot, clock.GetUtcNow(), default), Is.True);
        await socketNode.ObserveOwnHealAsync(pilot, connection, default);
        await socketNode.ObserveOwnHealAsync(pilot, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((923L, 100L, 1000L)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConcurrentHullHealObservations_SendHealOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(938, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        await socketNode.PrimeOwnHealAsync(pilot, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);

        using var wire = new PauseFirstWriteStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        var first = socketNode.ObserveOwnHealAsync(pilot, connection, default);
        await wire.FirstWriteStarted;
        var second = socketNode.ObserveOwnHealAsync(pilot, connection, default);
        wire.Resume();
        await Task.WhenAll(first, second);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var packet = await reader.ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((pilot.UserId, 25L, 925L)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConcurrentConnectedObserverPolls_SendRemoteOwnerHealOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(939, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        var observer = await socketNode.EnterAsync(pilot with { UserId = 940 }, default);
        using var pilotWire = new MemoryStream();
        using var observerWire = new PauseFirstWriteStream();
        socketNode.Attach(new WorldConnection(pilot, new WorldPacketWriter(pilotWire)));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        socketNode.Attach(observerConnection);
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        var first = socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        await observerWire.FirstWriteStarted;
        var second = socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        observerWire.Resume();
        await Task.WhenAll(first, second);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((pilot.UserId, 25L, 925L)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteHullHeals_ReplayEachRecoveryBetweenPolls()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var observerNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(925, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        await socketNode.PrimeOwnHealAsync(pilot, default);
        var observer = pilot with { UserId = 926 };
        using var ownWire = new MemoryStream();
        using var observerWire = new MemoryStream();
        var ownConnection = new WorldConnection(pilot, new WorldPacketWriter(ownWire));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        socketNode.Attach(ownConnection);
        await observerNode.ObserveRemotePilotsAsync(observer, observerConnection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await socketNode.ObserveOwnHealAsync(pilot, ownConnection, default);
        await socketNode.ObserveOwnHealAsync(pilot, ownConnection, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await observerNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        await observerNode.ObserveRemotePilotsAsync(observer, observerConnection, default);

        ownWire.Position = 0;
        var ownReader = new WorldPacketReader(ownWire);
        observerWire.Position = 0;
        var observerReader = new WorldPacketReader(observerWire);
        Assert.That((await observerReader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await observerReader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        foreach (var reader in new[] { ownReader, observerReader })
        {
            foreach (var hull in new long[] { 925, 950 })
            {
                var packet = await reader.ReadAsync();
                Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
                Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                    (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                    Is.EqualTo((925L, 25L, hull)));
            }
            Assert.That(await reader.ReadAsync(), Is.Null);
        }
    }

    [Test]
    public async Task OwningPilot_ReplaysRetainedHealsAfterDeliveryMarkerFallsOutOfHistory()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(942, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 500, default, shieldAbsorption: 0);
        clock.Advance(TimeSpan.FromSeconds(10));
        await sessions.RecoverHullAsync(pilot.MapId, default);
        await sessions.PrimeOwnHealAsync(pilot, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        for (var tick = 0; tick < 17; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await sessions.RecoverHullAsync(pilot.MapId, default);
        }
        sessions.Attach(connection);
        await sessions.ObserveOwnHealAsync(pilot, connection, default);
        await sessions.ObserveOwnHealAsync(pilot, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        for (var index = 0; index < 16; index++)
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((942L, 25L, 575L + index * 25)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteObserver_ReceivesNewHealBeforeMapSnapshotExpires()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var observerNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(938, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = pilot with { UserId = 939 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await observerNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        foreach (var hull in new long[] { 925, 950 })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((938L, 25L, hull)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FirstRemoteObserverPoll_ReplaysHealNewerThanCachedMapSnapshot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(944, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var firstObserver = pilot with { UserId = 945 };
        using var firstWire = new MemoryStream();
        await socketNode.ObserveRemotePilotsAsync(firstObserver,
            new WorldConnection(firstObserver, new WorldPacketWriter(firstWire)), default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        var secondObserver = pilot with { UserId = 946 };
        using var wire = new MemoryStream();
        var connection = new WorldConnection(secondObserver, new WorldPacketWriter(wire));
        await socketNode.ObserveRemotePilotsAsync(secondObserver, connection, default);
        await socketNode.ObserveRemotePilotsAsync(secondObserver, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((944L, 25L, 925L)));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RemoteMapOwnerHeals_ReachObserverWithLocalPilotSocketOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(928, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        var observer = await socketNode.EnterAsync(pilot with { UserId = 929 }, default);
        using var pilotWire = new MemoryStream();
        using var observerWire = new MemoryStream();
        socketNode.Attach(new WorldConnection(pilot, new WorldPacketWriter(pilotWire)));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        socketNode.Attach(observerConnection);
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        Assert.That(observerWire.Length, Is.Zero);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        foreach (var hull in new long[] { 925, 950 })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((928L, 25L, hull)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FirstConnectedObserverPoll_DeliversAllHealsAfterMapEntryOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(940, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await socketNode.EnterAsync(pilot, default);
        var observer = await socketNode.EnterAsync(pilot with { UserId = 941 }, default);
        using var pilotWire = new MemoryStream();
        using var observerWire = new MemoryStream();
        socketNode.Attach(new WorldConnection(pilot, new WorldPacketWriter(pilotWire)));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        socketNode.Attach(observerConnection);

        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);
        await socketNode.ObserveRemotePilotsAsync(observer, observerConnection, default);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((940L, 25L, 925L)));
        var second = await reader.ReadAsync();
        Assert.That(second?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(second!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((940L, 25L, 950L)));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FailedRemotePilotCreation_IsRetriedBeforeHealDelivery()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingWorldClock();
        var mapOwner = new WorldSessionStore(cache, clock: clock);
        var socketNode = new WorldSessionStore(cache, clock: clock);
        var pilot = await mapOwner.EnterAsync(new WorldPilot(947, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await mapOwner.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = pilot with { UserId = 948 };
        using var wire = new FailFirstHealWriteStream();
        var connection = new WorldConnection(observer, new WorldPacketWriter(wire));

        Assert.ThrowsAsync<IOException>(async () =>
            await socketNode.ObserveRemotePilotsAsync(observer, connection, default));
        clock.Advance(TimeSpan.FromSeconds(10));
        await mapOwner.RecoverHullAsync(pilot.MapId, default);
        await socketNode.ObserveRemotePilotsAsync(observer, connection, default);
        await socketNode.ObserveRemotePilotsAsync(observer, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.PilotCreateCommand));
        var heal = await reader.ReadAsync();
        Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
            Is.EqualTo((947L, 25L, 925L)));
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FailedLocalHealWrites_ReplayToPilotAndObserver()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(932, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = await sessions.EnterAsync(pilot with { UserId = 933 }, default);
        using var pilotWire = new FailFirstHealWriteStream();
        using var observerWire = new FailFirstHealWriteStream();
        var pilotConnection = new WorldConnection(pilot, new WorldPacketWriter(pilotWire));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        sessions.Attach(pilotConnection);
        sessions.Attach(observerConnection);
        await sessions.PrimeOwnHealAsync(pilot, default);
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await sessions.RepairAtBaseAsync(pilot, clock.GetUtcNow(), default), Is.True);
        Assert.That(pilotWire.Length, Is.Zero);
        Assert.That(observerWire.Length, Is.Zero);
        await sessions.ObserveOwnHealAsync(pilot, pilotConnection, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);
        await sessions.ObserveOwnHealAsync(pilot, pilotConnection, default);
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);

        foreach (var wire in new MemoryStream[] { pilotWire, observerWire })
        {
            wire.Position = 0;
            var reader = new WorldPacketReader(wire);
            var heal = await reader.ReadAsync();
            Assert.That(heal?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(heal!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((932L, 100L, 1000L)));
            Assert.That(await reader.ReadAsync(), Is.Null);
        }
    }

    [Test]
    public async Task LaterLocalHeal_ReplaysEarlierFailedHealBeforeCurrentHeal()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(934, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = await sessions.EnterAsync(pilot with { UserId = 935 }, default);
        using var pilotWire = new MemoryStream();
        using var observerWire = new FailFirstHealWriteStream();
        sessions.Attach(new WorldConnection(pilot, new WorldPacketWriter(pilotWire)));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        sessions.Attach(observerConnection);
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await sessions.RecoverHullAsync(pilot.MapId, default);
        Assert.That(observerWire.Length, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(1));
        await sessions.RecoverHullAsync(pilot.MapId, default);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        foreach (var hull in new long[] { 925, 950 })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((934L, 25L, hull)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task LocalHullHeal_DoesNotReplayToConnectedObserver()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(930, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = await sessions.EnterAsync(pilot with { UserId = 931 }, default);
        using var pilotWire = new MemoryStream();
        using var observerWire = new MemoryStream();
        sessions.Attach(new WorldConnection(pilot, new WorldPacketWriter(pilotWire)));
        var observerConnection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        sessions.Attach(observerConnection);
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await sessions.RepairAtBaseAsync(pilot, clock.GetUtcNow(), default), Is.True);
        await sessions.ObserveRemotePilotsAsync(observer, observerConnection, default);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConnectedObserverPoll_DoesNotReplayOlderSnapshotAfterNewLocalHeal()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(936, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        var observer = await sessions.EnterAsync(pilot with { UserId = 937 }, default);
        using var observerWire = new MemoryStream();
        sessions.Attach(new WorldConnection(pilot, new WorldPacketWriter(new MemoryStream())));
        var connection = new WorldConnection(observer, new WorldPacketWriter(observerWire));
        sessions.Attach(connection);
        await sessions.ObserveRemotePilotsAsync(observer, connection, default);

        clock.Advance(TimeSpan.FromSeconds(10));
        await sessions.RecoverHullAsync(pilot.MapId, default);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await sessions.ObserveRemotePilotsAsync(observer, connection, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await sessions.RecoverHullAsync(pilot.MapId, default);
        await sessions.ObserveRemotePilotsAsync(observer, connection, default);

        observerWire.Position = 0;
        var reader = new WorldPacketReader(observerWire);
        foreach (var hull in new long[] { 925, 950 })
        {
            var packet = await reader.ReadAsync();
            Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
            Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((936L, 25L, hull)));
        }
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task LocalHullHeal_DoesNotReplayToOwningPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await sessions.EnterAsync(new WorldPilot(924, "pilot", 1, 1, "ship", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await sessions.ApplyNpcDamageAsync(pilot, 1, 100, default, shieldAbsorption: 0);
        await sessions.PrimeOwnHealAsync(pilot, default);
        using var wire = new MemoryStream();
        var connection = new WorldConnection(pilot, new WorldPacketWriter(wire));
        sessions.Attach(connection);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await sessions.RepairAtBaseAsync(pilot, clock.GetUtcNow(), default), Is.True);
        await sessions.ObserveOwnHealAsync(pilot, connection, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        Assert.That((await reader.ReadAsync())?.Command, Is.EqualTo(WorldCommandId.ShipHealCommand));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    private sealed class CountingCache(IDistributedCache inner) : IDistributedCache
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Reads = new();
        public byte[]? Get(string key) { Reads.Enqueue(key); return inner.Get(key); }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        { Reads.Enqueue(key); return inner.GetAsync(key, token); }
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default) => inner.SetAsync(key, value, options, token);
    }

    [Test]
    public async Task NpcObservers_OnSameMap_ShareOneReadAndSeeRemoteDamageAfterSnapshotExpiry()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var shared = services.GetRequiredService<IDistributedCache>();
        var counting = new CountingCache(shared);
        var mutex = new NpcMutex();
        var options = Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 2, 100000, 20000, 400, 1, 400, 2, 0)] });
        var local = new WorldNpcs(counting, mutex, new NpcRewards(), options, TimeProvider.System);
        var remote = new WorldNpcs(shared, mutex, new NpcRewards(), options, TimeProvider.System);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => local.VisibleForObserversAsync(1, CancellationToken.None)));
        Assert.That(snapshots.All(s => s.Count == 2), Is.True);
        Assert.That(counting.Reads.Count(k => k.StartsWith("world:npc:v2:1:", StringComparison.Ordinal)), Is.EqualTo(2));

        var target = snapshots[0][0];
        var pilot = new WorldPilot(128, "mmo", 1, 1, "ship", 0, 0)
            { AccountId = Guid.NewGuid(), X = target.X, Y = target.Y,
                Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var sessions = new WorldSessionStore(shared, mutex);
        var hit = await remote.AttackAsync(pilot, target.Id, sessions, CancellationToken.None);
        Assert.That(hit.Status, Is.EqualTo("hit"));
        await Task.Delay(WorldNpcs.ObserverSnapshotTtl + TimeSpan.FromMilliseconds(100));
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => local.VisibleForObserversAsync(1, CancellationToken.None)));
        Assert.Multiple(() =>
        {
            Assert.That(refreshed.All(s => s.Single(n => n.Id == target.Id).Shield == hit.Target!.Shield), Is.True);
            Assert.That(counting.Reads.Count(k => k.StartsWith("world:npc:v2:1:", StringComparison.Ordinal)), Is.EqualTo(4));
        });
    }

    private sealed class RecordingAdmin : IAdminCommandDispatcher
    {
        public AdminCommand? Command;
        public AdminCommandActor? Actor;
        public Action<string>? OnExecute;
        public Task<AdminCommandResult> ExecuteAsync(AdminCommand command, AdminCommandActor actor,
            CancellationToken ct = default)
        {
            Command = command; Actor = actor;
            OnExecute?.Invoke(command.Text ?? string.Empty);
            return Task.FromResult(new AdminCommandResult("var set", new { applied = true }, 12));
        }
    }

    private sealed class LiveVariables : IRuntimeVariableRegistry
    {
        public double Speed = 1, Laser = 1, JumpRadius = 500, PeaceRadius = 1000, StreunerCount = 1;
        public double CreditsReward = 1, UridiumReward = 1, ExperienceReward = 1, HonorReward = 1;
        public IReadOnlyList<RuntimeVariable> Catalog => [];
        public Task<double> NumberAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(key switch
            {
                "world.movement.speed_multiplier" => Speed,
                "world.combat.laser_damage_multiplier" => Laser,
                "world.portal.jump_radius" => JumpRadius,
                "world.station.peace_radius" => PeaceRadius,
                "world.spawn.streuner_count" => StreunerCount,
                "economy.credits_reward_multiplier" => CreditsReward,
                "economy.uridium_reward_multiplier" => UridiumReward,
                "economy.xp_reward_multiplier" => ExperienceReward,
                "economy.honor_reward_multiplier" => HonorReward,
                "world.ship.pusat_plus_base_speed" => 300,
                "world.ship.phoenix_base_speed" => 300,
                _ => 1
            });
        public Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100, long? beforeChangeId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task Loopback_ExternalVariableChangeAndRollback_RefreshIdleSocketAttributes()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var variables = new LiveVariables();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), variables: variables);
        var pilot = new WorldPilot(803, "mmo", 1, 1, "ship", 0, 0)
        { BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            variables: variables);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(803, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            async Task AssertAttributesAsync(int speed, int laser)
            {
                var packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.That(packet.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
                var stats = WorldPayload.Decode(packet.Payload, r =>
                {
                    WorldPayload.ReadInt64(r); r.ReadByte();
                    var actualSpeed = WorldPayload.ReadInt32(r);
                    WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r);
                    return (actualSpeed, WorldPayload.ReadInt32(r));
                });
                Assert.That(stats, Is.EqualTo((speed, laser)));
            }

            // One variable per step, like one admin `var set`/rollback each: changing two fields
            // back-to-back let the idle poll observe the half-applied pair and emit (600, 30).
            variables.Speed = 2;
            await AssertAttributesAsync(600, 30);
            variables.Laser = 2;
            await AssertAttributesAsync(600, 60);
            variables.Speed = 1;
            await AssertAttributesAsync(300, 60);
            variables.Laser = 1;
            await AssertAttributesAsync(300, 30);
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_MovementAndRuntimeRadius_RefreshSafeZoneOnOpenSocket()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var variables = new LiveVariables();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), variables: variables);
        var pilot = new WorldPilot(853, "mmo", 1, 1, "ship", 0, 0)
        { X = 2800, Y = 1300, BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            variables: variables);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(853, "opaque", "local-1"));
            var entry = new List<WorldPacket>();
            for (var i = 0; i < 12; i++) entry.Add((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value);
            Assert.That(entry.Single(p => p.Command == WorldCommandId.SafeZoneCommand).Payload.ToArray(), Is.EqualTo(new byte[] { 1 }));
            await Task.Delay(1200);
            Assert.That(client.Available, Is.Zero);

            variables.PeaceRadius = 500;
            var station = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(station.Command, Is.EqualTo(WorldCommandId.SpaceStationCommand));
            Assert.That(WorldPayload.Decode(station.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                Is.EqualTo((100, 2000, 1300, 500)));
            var outside = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(outside.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
            Assert.That(outside.Payload.ToArray(), Is.EqualTo(new byte[] { 0 }));

            variables.PeaceRadius = 1000;
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.SpaceStationCommand));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Payload.ToArray(),
                Is.EqualTo(new byte[] { 1 }));

            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2800); WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, 3400); WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.MoveCommand));
            var left = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(left.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
            Assert.That(left.Payload.ToArray(), Is.EqualTo(new byte[] { 0 }));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_ExternalJumpRadiusChange_GatesConnectedSocketPortalJump()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var variables = new LiveVariables();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), variables: variables);
        var pilot = new WorldPilot(804, "mmo", 1, 1, "ship", 0, 0)
        { X = 18800, Y = 1300, BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            variables: variables);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(804, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var jump = WorldPayload.Encode(w => WorldPayload.WriteInt32(w, 101));

            // Portal 101 is 700 units away: outside the default 500 radius.
            await output.WriteAsync(WorldCommandId.JumpRequest, jump);
            var rejected = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(rejected.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));

            // The same open socket reads the next registry value without reconnecting.
            variables.JumpRadius = 800;
            await output.WriteAsync(WorldCommandId.JumpRequest, jump);
            var changed = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(changed.Command, Is.EqualTo(WorldCommandId.MapChangeCommand));
            Assert.That(WorldPayload.Decode(changed.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((2, string.Empty, string.Empty)));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.MapInitializationCommand));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_ExternalSpeedChangeAndRollback_RetimesMovingShip()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var variables = new LiveVariables();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache, variables: variables);
        var pilot = new WorldPilot(804, "mmo", 1, 1, "ship", 0, 0)
        { BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            variables: variables);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(804, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, 5000); WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.MoveCommand));

            async Task AssertRetimedAsync(int speed, int maximumDuration)
            {
                var move = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.That(move.Command, Is.EqualTo(WorldCommandId.MoveCommand));
                var destination = WorldPayload.Decode(move.Payload, r =>
                {
                    WorldPayload.ReadInt64(r);
                    return (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r));
                });
                Assert.That(destination.Item1, Is.EqualTo(5000));
                Assert.That(destination.Item2, Is.EqualTo(1300));
                Assert.That(destination.Item3, Is.InRange(1, maximumDuration));
                var attributes = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.That(attributes.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
                var actualSpeed = WorldPayload.Decode(attributes.Payload, r =>
                {
                    WorldPayload.ReadInt64(r); r.ReadByte();
                    var value = WorldPayload.ReadInt32(r);
                    WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r);
                    WorldPayload.ReadInt32(r);
                    return value;
                });
                Assert.That(actualSpeed, Is.EqualTo(speed));
                var saved = await cache.GetStringAsync("world:position:804");
                Assert.That(saved, Does.Contain($"\"Speed\":{speed}"));
            }

            variables.Speed = 2;
            await AssertRetimedAsync(600, 5000);
            variables.Speed = 1;
            await AssertRetimedAsync(300, 10000);
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase("world.movement.speed_multiplier", 600, 30)]
    [TestCase("world.combat.laser_damage_multiplier", 300, 60)]
    public async Task Loopback_AdminVariableChange_UpdatesShipAttributesImmediately(string key,
        int expectedSpeed, int expectedLaser)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var variables = new LiveVariables();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), variables: variables);
        var dispatcher = new RecordingAdmin { OnExecute = _ =>
        { if (key.Contains("speed")) variables.Speed = 2; else variables.Laser = 2; } };
        var pilot = new WorldPilot(802, "mmo", 1, 1, "ship", 0, 0)
        { IsAdmin = true, UserName = "Pilot", BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            adminCommands: dispatcher, variables: variables);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(802, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.AdminCommandRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, $"/var set {key} 2")));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.AdminCommandResponse));
            var attributes = (await input.ReadAsync())!.Value;
            Assert.That(attributes.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
            var stats = WorldPayload.Decode(attributes.Payload, r =>
            {
                WorldPayload.ReadInt64(r); r.ReadByte();
                var speed = WorldPayload.ReadInt32(r);
                WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r);
                return (speed, laser: WorldPayload.ReadInt32(r));
            });
            Assert.That(stats, Is.EqualTo((expectedSpeed, expectedLaser)));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(false)] [TestCase(true)]
    public async Task Loopback_AdminCommand_EnforcesPilotRoleAndResponds(bool isAdmin)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var dispatcher = new RecordingAdmin();
        var pilot = new WorldPilot(801, "mmo", 1, 1, "ship", 0, 0)
        { IsAdmin = isAdmin, UserName = "Pilot", Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            adminCommands: dispatcher);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(801, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.AdminCommandRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "/var set world.movement.speed_multiplier 2")));
            var reply = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            if (!isAdmin)
            {
                Assert.That(reply.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(reply.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.Unauthorized }));
                Assert.That(dispatcher.Command, Is.Null);
            }
            else
            {
                Assert.That(reply.Command, Is.EqualTo(WorldCommandId.AdminCommandResponse));
                Assert.That(reply.Payload.Span[0], Is.EqualTo(1));
                var response = WorldPayload.Decode(reply.Payload[1..], WorldPayload.ReadAdminResult);
                using var json = JsonDocument.Parse(response);
                Assert.That(json.RootElement.GetProperty("AuditId").GetInt64(), Is.EqualTo(12));
                Assert.That(dispatcher.Command?.Text, Is.EqualTo("var set world.movement.speed_multiplier 2"));
                Assert.That(dispatcher.Actor?.Surface, Is.EqualTo("InGameAdmin"));
                Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
                Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.MapInitializationCommand));
            }
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(new byte[] { 0, 1, 0xff })]
    [TestCase(new byte[] { 0, 2, (byte)'a' })]
    [TestCase(new byte[] { 0, 1, (byte)'a', 0 })]
    public async Task Loopback_MalformedAuthenticatedAdminPayload_ReturnsProtocolErrorAndCloses(byte[] payload)
    {
        var dispatcher = new RecordingAdmin();
        var pilot = new WorldPilot(801, "mmo", 1, 1, "ship", 0, 0)
        { IsAdmin = true, UserName = "Pilot", Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)),
            adminCommands: dispatcher);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(801, "opaque", "local-1"));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.LoginResponse));
            for (var i = 0; i < 11; i++) await input.ReadAsync();

            await output.WriteAsync(WorldCommandId.AdminCommandRequest, payload);
            var response = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(response.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.Malformed }));
                Assert.That(dispatcher.Command, Is.Null);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public async Task Loopback_UnauthorizedAdminPayload_RejectsBeforeDecodingAndKeepsSessionOpen()
    {
        var dispatcher = new RecordingAdmin();
        var pilot = new WorldPilot(802, "mmo", 1, 1, "ship", 0, 0)
        { IsAdmin = false, UserName = "Pilot", Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)),
            adminCommands: dispatcher);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(802, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();

            foreach (var payload in new[] { new byte[] { 0, 1, 0xff },
                         WorldPayload.Encode(w => WorldPayload.WriteString(w, "/var list")) })
            {
                await output.WriteAsync(WorldCommandId.AdminCommandRequest, payload);
                var response = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.Multiple(() =>
                {
                    Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                    Assert.That(response.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.Unauthorized }));
                });
            }
            Assert.That(dispatcher.Command, Is.Null);
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Test]
    public async Task Loopback_AdminSpawnAndAnnouncement_ArriveAtTcpClient()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var events = new WorldAdminEvents(cache, mutex);
        var spawned = (WorldNpc)await events.SpawnAsync("streuner", 1, 3200, 2600, default);
        await events.AnnounceAsync("Maintenance soon",
            new AdminCommandActor("ServerManagement", "operator", "node-a"), default);
        var npcs = new WorldNpcs(cache, mutex, new NpcRewards(), Options.Create(new WorldNpcOptions
            { Templates = [] }), TimeProvider.System, adminEvents: events);
        var pilot = new WorldPilot(744, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)),
            new WorldSessionStore(cache), npcs: npcs, adminEvents: events);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(744, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var spawn = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(spawn.Command, Is.EqualTo(WorldCommandId.ShipCreateCommand));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(spawn.Payload.Span[..4]), Is.EqualTo(spawned.Id));
            var announcement = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(announcement.Command, Is.EqualTo(WorldCommandId.AnnouncementCommand));
            Assert.That(WorldPayload.Decode(announcement.Payload, WorldPayload.ReadString),
                Is.EqualTo("Maintenance soon"));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private sealed class NpcMutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Handle(gate); }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }
    private sealed class NpcRewards : IWorldNpcRewards
    {
        public int Calls;
        public WorldNpc? LastCredited;
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        { Calls++; LastCredited = npc; return Task.CompletedTask; }
    }

    [Test]
    public async Task NpcKillRetry_KeepsCreditedAmountsAfterMultiplierChangesOnAnotherNode()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var variables = new LiveVariables { CreditsReward = 2, UridiumReward = 3,
            ExperienceReward = 1.5, HonorReward = 4 };
        var options = Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] });
        var nodeA = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
        var nodeB = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
        var npc = (await nodeA.VisibleAsync(1, default)).Single();
        var pilot = new WorldPilot(912, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var sessions = new WorldSessionStore(cache, mutex);
        await sessions.EnterAsync(pilot, default);
        var hit = await nodeA.AttackAsync(pilot, npc.Id, sessions, default);
        Assert.That(hit.Destroyed, Is.True);
        Assert.That(hit.Reward, Is.EqualTo(new WorldNpcReward(800, 3, 600, 8)));

        variables.CreditsReward = 5;
        variables.UridiumReward = 5;
        variables.ExperienceReward = 5;
        variables.HonorReward = 5;
        variables.StreunerCount = 0;
        Assert.That(await sessions.TeleportAsync(pilot.UserId, 1, 0, 0, false, default), Is.Not.Null);
        Assert.That((long)npc.X * npc.X + (long)npc.Y * npc.Y, Is.GreaterThan(1_000_000));
        var retry = await nodeB.AttackAsync(pilot, npc.Id, sessions, default);
        Assert.That(retry.Status, Is.EqualTo("destroyed"));
        Assert.That(rewards.Calls, Is.EqualTo(2));
        Assert.That((rewards.LastCredited!.Credits, rewards.LastCredited.Uridium,
            rewards.LastCredited.Experience, rewards.LastCredited.Honor),
            Is.EqualTo((800, 3, 600, 8)));
    }

    [Test]
    public async Task LeaseEpoch_FencesTcpStateWritesInsideCoordinatorMutex()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ThDarkOrbitEmulator.Cluster.Infrastructure.ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        static ServerMeshNode Node(string id, DateTimeOffset at) => new(id, id, $"http://{id}",
            "127.0.0.1", 61001, ServerMeshNodeStatus.Active, at, at, 0, [], 0, 0);
        var initial = await registry.HeartbeatAsync(Node("a", now));
        var original = initial.Leases.Single(l => l.MapId == 1);
        var pilot = new WorldPilot(903, "mmo", 1, 1, "ship", 0, 0)
        { X = 19500, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        var stale = new WorldSessionStore(cache, mutex);
        await stale.EnterAsync(pilot, default);
        await registry.HeartbeatAsync(Node("b", now));
        var reassigned = await registry.SetDrainingAsync("a");
        var replacement = reassigned.Leases.Single(l => l.MapId == 1);
        Assert.That(replacement.NodeId, Is.EqualTo("b"));
        Assert.That(replacement.Epoch, Is.GreaterThan(original.Epoch));
        var oldFence = new WorldLeaseFence(registry, "a", original.Epoch);
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await stale.MoveAsync(pilot, 18000, 1300, null, default, fence: oldFence));
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await stale.SwitchAsync(pilot, 2, default, fence: oldFence));
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await stale.JumpAsync(pilot, 101, registry, default, oldFence));
        var reopened = new WorldSessionStore(cache, mutex);
        var restored = await reopened.RestoreAsync(pilot, default);
        Assert.That((restored.MapId, restored.X, restored.Y), Is.EqualTo((1, 19500, 1300)));
        Assert.That(reopened.Configuration(pilot.UserId), Is.EqualTo(1));
        var newFence = new WorldLeaseFence(registry, "b", replacement.Epoch);
        var moved = await reopened.MoveAsync(restored, 18000, 1300, null, default, fence: newFence);
        Assert.That(moved.TimeToTargetMs, Is.GreaterThan(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TcpLeaseChange_RejectsWrongNodeAndFencesConnectedMovement(bool sendMovement)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ThDarkOrbitEmulator.Cluster.Infrastructure.ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        static ServerMeshNode Node(string id, DateTimeOffset at) => new(id, id, $"http://{id}",
            "127.0.0.1", id == "a" ? 61001 : 61002, ServerMeshNodeStatus.Active,
            at, at, 0, [], 0, 0);
        await registry.HeartbeatAsync(Node("a", now));
        Assert.That((await registry.GetSnapshotAsync()).Leases.Single(l => l.MapId == 1).NodeId,
            Is.EqualTo("a"));
        var pilot = new WorldPilot(901, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        var store = new WorldSessionStore(cache, mutex);

        var wrong = await ConnectAsync(new FakeResolver((pilot, default)), store, registry, "b");
        using (wrong.Client) using (wrong.Server)
        {
            var input = new WorldPacketReader(wrong.Client.GetStream());
            var output = new WorldPacketWriter(wrong.Client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(901, "opaque", "local-1"));
            var route = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(route.Command, Is.EqualTo(WorldCommandId.MapChangeCommand));
            Assert.That(WorldPayload.Decode(route.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((1, "a", "127.0.0.1:61001")));
            await wrong.Worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(store.Find(901), Is.Null);
        }

        var right = await ConnectAsync(new FakeResolver((pilot, default)), store, registry, "a");
        using (right.Client) using (right.Server)
        {
            var input = new WorldPacketReader(right.Client.GetStream());
            var output = new WorldPacketWriter(right.Client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(901, "opaque", "local-1"));
            for (var i = 0; i < 12; i++)
                await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await registry.HeartbeatAsync(Node("b", now));
            await registry.SetDrainingAsync("a");
            if (sendMovement)
                await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 3000);
                    WorldPayload.WriteInt32(w, 4000); WorldPayload.WriteInt32(w, 1300);
                }));
            var route = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(route.Command, Is.EqualTo(WorldCommandId.MapChangeCommand));
            Assert.That(WorldPayload.Decode(route.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((1, "b", "127.0.0.1:61002")));
            await right.Worker.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = await new WorldSessionStore(cache, mutex).RestoreAsync(pilot, default);
            Assert.That((restored.X, restored.Y), Is.EqualTo((2000, 1300)));
        }


        var destination = await ConnectAsync(new FakeResolver((pilot, default)),
            new WorldSessionStore(cache, mutex), registry, "b");
        using (destination.Client) using (destination.Server)
        {
            var input = new WorldPacketReader(destination.Client.GetStream());
            var output = new WorldPacketWriter(destination.Client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(901, "opaque", "local-1"));
            for (var i = 0; i < 12; i++)
                await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 3000);
                WorldPayload.WriteInt32(w, 4000); WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.MoveCommand));
            destination.Client.Client.Shutdown(SocketShutdown.Send);
            await destination.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TcpRemoteTeleport_RoutesOldSocketBeforeMovement(bool sendMovement)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ThDarkOrbitEmulator.Cluster.Infrastructure.ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        await registry.HeartbeatAsync(new ServerMeshNode("a", "api-game", "http://a",
            "127.0.0.1", 61001, ServerMeshNodeStatus.Active,
            now, now, 0, [], 0, 0));
        var pilot = new WorldPilot(902, "mmo", 1, 1, "ship", 0, 0)
        { X = 2000, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        var store = new WorldSessionStore(cache, mutex);
        var socket = await ConnectAsync(new FakeResolver((pilot, default)), store, registry, "a");
        using (socket.Client) using (socket.Server)
        {
            var input = new WorldPacketReader(socket.Client.GetStream());
            var output = new WorldPacketWriter(socket.Client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(902, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var moved = await new WorldSessionStore(cache, mutex)
                .TeleportAsync(902, 5, 3400, 2100, true, default);
            Assert.That(moved?.MapId, Is.EqualTo(5));
            var mapOneLease = (await registry.GetSnapshotAsync()).Leases.Single(lease => lease.MapId == 1);
            Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
                await store.MoveAsync(pilot, 4000, 3000, null, default,
                    fence: new WorldLeaseFence(registry, "a", mapOneLease.Epoch)));
            if (sendMovement)
                await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 3000);
                    WorldPayload.WriteInt32(w, 4000); WorldPayload.WriteInt32(w, 1300);
                }));
            var route = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(route.Command, Is.EqualTo(WorldCommandId.MapChangeCommand));
            Assert.That(WorldPayload.Decode(route.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((5, "a", "127.0.0.1:61001")));
            await socket.Worker.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = await new WorldSessionStore(cache, mutex).RestoreAsync(pilot, default);
            Assert.That((restored.MapId, restored.X, restored.Y), Is.EqualTo((5, 3400, 2100)));
        }
    }

    [Test]
    public async Task Loopback_NpcSpawnLockLaserAndRewardPackets()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var variables = new LiveVariables { CreditsReward = 2, UridiumReward = 3,
            ExperienceReward = 1.5, HonorReward = 4 };
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 150, 20, 400, 1, 400, 2, 0)] }), TimeProvider.System, variables);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var pilot = new WorldPilot(122, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var sessions = new WorldSessionStore(cache, mutex);
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), sessions,
            npcs: npcs);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(122, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            var spawn = (await input.ReadAsync())!.Value;
            Assert.That(spawn.Command, Is.EqualTo(WorldCommandId.ShipCreateCommand));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(spawn.Payload.Span[..4]), Is.EqualTo(npc.Id));
            await output.WriteAsync(WorldCommandId.SelectShipRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            var locked = (await input.ReadAsync())!.Value;
            Assert.That(locked.Command, Is.EqualTo(WorldCommandId.TargetLockCommand));
            Assert.That(WorldPayload.Decode(locked.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                Is.EqualTo((npc.Id, 150, 20, 150, 20)));
            await output.WriteAsync(WorldCommandId.LaserFireRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.LaserHitCommand));
            var firstResult = (await input.ReadAsync())!.Value;
            Assert.That(firstResult.Command, Is.EqualTo(WorldCommandId.LaserFireResultCommand));
            Assert.That(WorldPayload.Decode(firstResult.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r))), Is.EqualTo((npc.Id, "hit")));
            await output.WriteAsync(WorldCommandId.LaserStopRequest, ReadOnlyMemory<byte>.Empty);
            await output.WriteAsync(WorldCommandId.LaserFireRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.LaserHitCommand));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.ShipDestroyCommand));
            var rewardPacket = (await input.ReadAsync())!.Value;
            Assert.That(rewardPacket.Command, Is.EqualTo(WorldCommandId.NpcRewardCommand));
            Assert.That(WorldPayload.Decode(rewardPacket.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                Is.EqualTo((npc.Id, 800, 3, 600, 8)));
            var finalResult = (await input.ReadAsync())!.Value;
            Assert.That(finalResult.Command, Is.EqualTo(WorldCommandId.LaserFireResultCommand));
            Assert.That(WorldPayload.Decode(finalResult.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r))), Is.EqualTo((npc.Id, "hit")));
            Assert.That(rewards.Calls, Is.EqualTo(1));
            Assert.That((rewards.LastCredited!.Credits, rewards.LastCredited.Uridium,
                rewards.LastCredited.Experience, rewards.LastCredited.Honor), Is.EqualTo((800, 3, 600, 8)));
            Assert.That(await npcs.CanRetryRewardAsync(pilot with { AccountId = Guid.NewGuid() },
                npc.Id, default), Is.False);
            await output.WriteAsync(WorldCommandId.LaserFireRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            var retryResult = (await input.ReadAsync())!.Value;
            Assert.That(retryResult.Command, Is.EqualTo(WorldCommandId.LaserFireResultCommand));
            Assert.That(WorldPayload.Decode(retryResult.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r))), Is.EqualTo((npc.Id, "destroyed")));
            Assert.That(rewards.Calls, Is.EqualTo(2));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_RejectedLaserFire_SendsResultStatusAndKeepsSocketOpen()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] }), TimeProvider.System);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var offset = npc.X > 10000 ? -2000 : 2000;
        var pilot = new WorldPilot(123, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = npc.X + offset, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var sessions = new WorldSessionStore(cache, mutex);
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), sessions,
            npcs: npcs);
        using (client) using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(123, "opaque", "local-1"));
            async Task<WorldPacket> NextAsync(WorldCommandId command)
            {
                while (true)
                {
                    var packet = (await input.ReadAsync())!.Value;
                    Assert.That(packet.Command, Is.Not.EqualTo(WorldCommandId.LoginErrorCommand));
                    if (packet.Command == command) return packet;
                }
            }
            (int, string) Result(WorldPacket packet) => WorldPayload.Decode(packet.Payload,
                r => (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r)));
            await NextAsync(WorldCommandId.ShipCreateCommand);
            await output.WriteAsync(WorldCommandId.LaserFireRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            Assert.That(Result(await NextAsync(WorldCommandId.LaserFireResultCommand)),
                Is.EqualTo((npc.Id, "unselected")));
            await output.WriteAsync(WorldCommandId.SelectShipRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
            await NextAsync(WorldCommandId.TargetLockCommand);
            for (var shot = 0; shot < 2; shot++)
            {
                await output.WriteAsync(WorldCommandId.LaserFireRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)));
                Assert.That(Result(await NextAsync(WorldCommandId.LaserFireResultCommand)),
                    Is.EqualTo((npc.Id, "distant")));
            }
            Assert.That(rewards.Calls, Is.Zero);
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_IdleSocket_SyncsNpcRosterFromVariablesAndRemoteKills()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var variables = new LiveVariables();
        var options = Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] });
        var npcs = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
        var pilot = new WorldPilot(124, "mmo", 1, 1, "ship", 0, 0)
            { AccountId = Guid.NewGuid(), Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)),
            new WorldSessionStore(cache, mutex, variables: variables), npcs: npcs, variables: variables);
        using (client) using (server)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(124, "opaque", "local-1"));
            async Task<int> NextIdAsync(WorldCommandId command)
            {
                while (true)
                {
                    var packet = (await input.ReadAsync(timeout.Token))!.Value;
                    Assert.That(packet.Command, Is.Not.EqualTo(WorldCommandId.LoginErrorCommand));
                    // ShipCreate and ShipDestroy both lead with the int32 NPC id.
                    if (packet.Command == command)
                        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet.Payload.Span);
                }
            }
            var first = await NextIdAsync(WorldCommandId.ShipCreateCommand);

            // Server Management / CMS raise the shared spawn count: the idle socket gains the NPC.
            variables.StreunerCount = 2;
            var second = await NextIdAsync(WorldCommandId.ShipCreateCommand);
            Assert.That(second, Is.Not.EqualTo(first));

            // A pilot on another node (own NPC/session stores, same shared cache) kills it.
            var remoteNpcs = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
            var remoteSessions = new WorldSessionStore(cache, mutex, variables: variables);
            var target = (await remoteNpcs.VisibleAsync(1, default)).Single(n => n.Id == second);
            var killer = pilot with { UserId = 125, AccountId = Guid.NewGuid(), X = target.X, Y = target.Y };
            Assert.That(await remoteNpcs.SelectAsync(killer, second, default, null, remoteSessions), Is.Not.Null);
            WorldNpcAttack hit;
            do hit = await remoteNpcs.AttackAsync(killer, second, remoteSessions, default);
            while (hit.Status == "hit" && !hit.Destroyed);
            Assert.That(hit.Destroyed, Is.True, hit.Status);
            Assert.That(await NextIdAsync(WorldCommandId.ShipDestroyCommand), Is.EqualTo(second));

            // Rollback to zero removes the remaining NPC from the open socket.
            variables.StreunerCount = 0;
            Assert.That(await NextIdAsync(WorldCommandId.ShipDestroyCommand), Is.EqualTo(first));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_IdleSocket_SyncsNpcVitalsFromRemoteDamage()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var variables = new LiveVariables();
        var options = Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 100000, 20000, 400, 1, 400, 2, 0)] });
        var npcs = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
        var pilot = new WorldPilot(126, "mmo", 1, 1, "ship", 0, 0)
            { AccountId = Guid.NewGuid(), Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)),
            new WorldSessionStore(cache, mutex, variables: variables), npcs: npcs, variables: variables);
        using (client) using (server)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(126, "opaque", "local-1"));
            async Task<ReadOnlyMemory<byte>> NextAsync(WorldCommandId command)
            {
                while (true)
                {
                    var packet = (await input.ReadAsync(timeout.Token))!.Value;
                    Assert.That(packet.Command, Is.Not.EqualTo(WorldCommandId.LoginErrorCommand));
                    if (packet.Command == command) return packet.Payload;
                }
            }
            static int Int(ReadOnlyMemory<byte> payload, int index) =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.Span[(index * 4)..]);
            var id = Int(await NextAsync(WorldCommandId.ShipCreateCommand), 0);

            // A pilot on another node damages the NPC without killing it; the idle socket's
            // broadcast never fires, so only the idle vitals sync can report the new HP/shield.
            var remoteNpcs = new WorldNpcs(cache, mutex, rewards, options, TimeProvider.System, variables);
            var remoteSessions = new WorldSessionStore(cache, mutex, variables: variables);
            var target = (await remoteNpcs.VisibleAsync(1, default)).Single(n => n.Id == id);
            var shooter = pilot with { UserId = 127, AccountId = Guid.NewGuid(), X = target.X, Y = target.Y };
            Assert.That(await remoteNpcs.SelectAsync(shooter, id, default, null, remoteSessions), Is.Not.Null);
            var hit = await remoteNpcs.AttackAsync(shooter, id, remoteSessions, default);
            Assert.That(hit.Status, Is.EqualTo("hit"));
            Assert.That(hit.Destroyed, Is.False);
            var health = await NextAsync(WorldCommandId.NpcHealthCommand);
            Assert.That(Int(health, 0), Is.EqualTo(id));
            Assert.That(Int(health, 1), Is.EqualTo(hit.Target!.Hp));
            Assert.That(Int(health, 2), Is.EqualTo(target.MaxHp));
            Assert.That(Int(health, 3), Is.EqualTo(hit.Target.Shield));
            Assert.That(Int(health, 4), Is.EqualTo(target.MaxShield));
            Assert.That((Int(health, 1), Int(health, 3)), Is.Not.EqualTo((target.Hp, target.Shield)));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }
    [Test]
    public async Task Loopback_CrossNodeJump_ClosesSourceAndPreservesDestinationPresence()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var now = DateTimeOffset.UtcNow;
        var owner = new ServerMeshNode("node-b", "api-game", "http://b", "127.0.0.1", 61002,
            ServerMeshNodeStatus.Active, now, now, 0, [2], 0, 0);
        var source = owner with { NodeId = "node-a", WorldTcpPort = 61001, LeasedMapIds = [1] };
        var registry = new JumpRegistry(new ServerMeshSnapshot([source, owner],
            [new ServerMapLease(1, "node-a", 1, now.AddMinutes(1)),
                new ServerMapLease(2, "node-b", 1, now.AddMinutes(1))]));
        var pilot = new WorldPilot(121, "mmo", 1, 1, "ship", 0, 0)
            { X = 19500, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store,
            registry, "node-a");
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(121, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.JumpRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, 101)));
            var changed = (await input.ReadAsync())!.Value;
            Assert.That(WorldPayload.Decode(changed.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((2, "node-b", "127.0.0.1:61002")));
            await worker;
            Assert.That(await store.ConnectedAsync(2, default), Has.Count.EqualTo(1));
        }
    }

    private sealed class JumpRegistry(ServerMeshSnapshot snapshot) : IServerMeshRegistry
    {
        public Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
        public Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
        public Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }
    [Test]
    public async Task Loopback_JumpRequest_EmitsMapChangeAndDestinationHero()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(119, "mmo", 1, 1, "ship", 0, 0)
        {
            X = 19500, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(119, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.JumpRequest,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, 101)));
            var changed = (await input.ReadAsync())!.Value;
            Assert.That(changed.Command, Is.EqualTo(WorldCommandId.MapChangeCommand));
            Assert.That(WorldPayload.Decode(changed.Payload, r =>
                (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r))),
                Is.EqualTo((2, string.Empty, string.Empty)));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.MapInitializationCommand));
            for (var i = 0; i < 3; i++) await input.ReadAsync();
            var hero = (await input.ReadAsync())!.Value;
            Assert.That(hero.Command, Is.EqualTo(WorldCommandId.HeroInitCommand));
            using var heroStream = new MemoryStream(hero.Payload.ToArray());
            using var heroReader = new BinaryReader(heroStream);
            var arrival = ((Func<BinaryReader, (int, int, int)>)(r =>
            {
                WorldPayload.ReadInt64(r); WorldPayload.ReadString(r); WorldPayload.ReadString(r);
                WorldPayload.ReadInt32(r); return (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r));
            }))(heroReader);
            Assert.That(arrival, Is.EqualTo((2, 2500, 1300)));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task RadiationDamagesHullAndStationRepairsWhenStopped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingWorldClock();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = new WorldPilot(120, "mmo", 1, 1, "ship", 0, 0)
            { FactionId = 1, X = 0, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        await store.EnterAsync(pilot, default);
        var now = clock.GetUtcNow();
        await store.MoveAsync(pilot, -1, 1300, null, default, now);
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));
        clock.Advance(TimeSpan.FromSeconds(2));
        now = clock.GetUtcNow();
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(980));
        await store.MoveAsync(pilot, 2000, 1300, null, default, now);
        await store.RepairAtBaseAsync(pilot, now.AddHours(1), default);
        Assert.That(store.CurrentHitPoints(pilot), Is.GreaterThan(980));
    }
    [Test]
    public async Task PacketReader_CombinesPartialHeaderAndBody_AndReadsAdjacentPackets()
    {
        using var bytes = new MemoryStream();
        var writer = new WorldPacketWriter(bytes);
        await writer.WriteAsync(WorldCommandId.VersionRequest, new byte[] { 7, 8 });
        await writer.WriteAsync(WorldCommandId.LoginRequest, new byte[] { 9 });
        var encoded = bytes.ToArray();
        Assert.That(encoded[..6], Is.EqualTo(new byte[] { 0, 0, 0, 4, 0, 1 }));
        using var source = new ChunkedStream(encoded, 1);
        var reader = new WorldPacketReader(source);
        Assert.That((await reader.ReadAsync())!.Value.Payload.ToArray(), Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That((await reader.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.LoginRequest));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(65)]
    public void PacketReader_RejectsInvalidLengths(int size)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        using var stream = new MemoryStream(header);
        Assert.ThrowsAsync<InvalidDataException>(async () => await new WorldPacketReader(stream, 64).ReadAsync());
    }

    [Test]
    public void PacketReader_RejectsTruncatedFrame()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 3, 0 });
        Assert.ThrowsAsync<EndOfStreamException>(async () => await new WorldPacketReader(stream).ReadAsync());
    }

    [Test]
    public async Task Loopback_VersionAndAuthenticatedLogin()
    {
        var pilot = new WorldPilot(42, "eic", 5, 2, "phoenix", 100, 5)
        {
            FactionId = 2, UserName = "pilot42",
            Stats = new WorldShipStats(320, 4000, 5000, 100, 150,
                [new LocalEquipmentAssignment(2, 1, "laser_lf3", 1)]),
            Experience = 120, Honor = 30, Level = 2
        };
        var resolver = new FakeResolver((pilot, default));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            var version = (await input.ReadAsync())!.Value;
            Assert.That(version.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(WorldPayload.Decode(version.Payload, r =>
                (WorldPayload.ReadString(r), r.ReadBoolean())), Is.EqualTo(("local-1", true)));
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "local-1"));
            var response = (await input.ReadAsync())!.Value;
            Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginResponse));
            var received = WorldPayload.Decode(response.Payload, r =>
                (Status: r.ReadByte(), User: WorldPayload.ReadInt64(r), Company: WorldPayload.ReadString(r),
                    Map: BinaryPrimitives.ReverseEndianness(r.ReadInt32()), Slot: r.ReadByte(),
                    Ship: WorldPayload.ReadString(r), Credits: WorldPayload.ReadInt64(r),
                    Uridium: WorldPayload.ReadInt64(r)));
            Assert.That(received, Is.EqualTo(((byte)1, 42L, "eic", 5, (byte)2, "phoenix", 100L, 5L)));
            Assert.That(resolver.Calls, Is.EqualTo(1));
            var mapPacket = (await input.ReadAsync())!.Value;
            Assert.That(mapPacket.Command, Is.EqualTo(WorldCommandId.MapInitializationCommand));
            Assert.That(WorldPayload.Decode(mapPacket.Payload, r => (WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                Is.EqualTo((5, 21000, 13100, 2)));
            var station = (await input.ReadAsync())!.Value;
            Assert.That(station.Command, Is.EqualTo(WorldCommandId.SpaceStationCommand));
            Assert.That(WorldPayload.Decode(station.Payload, r => (WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                Is.EqualTo((500, 2000, 1300, 1000)));
            foreach (var destination in new[] { 6, 7, 106, 107, 108, 109, 110 })
            {
                var gate = (await input.ReadAsync())!.Value;
                Assert.That(gate.Command, Is.EqualTo(WorldCommandId.JumpgateCreateCommand));
                Assert.That(WorldPayload.Decode(gate.Payload, r =>
                {
                    WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
                    return WorldPayload.ReadInt32(r);
                }), Is.EqualTo(destination));
            }
            var safeZone = (await input.ReadAsync())!.Value;
            Assert.That(safeZone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
            Assert.That(safeZone.Payload.ToArray(), Is.EqualTo(new byte[] { 1 }));
            var hero = (await input.ReadAsync())!.Value;
            Assert.That(hero.Command, Is.EqualTo(WorldCommandId.HeroInitCommand));
            var heroState = WorldPayload.Decode(hero.Payload, r =>
            {
                var id = WorldPayload.ReadInt64(r);
                var name = WorldPayload.ReadString(r);
                var ship = WorldPayload.ReadString(r);
                var faction = WorldPayload.ReadInt32(r);
                var map = WorldPayload.ReadInt32(r);
                var x = WorldPayload.ReadInt32(r);
                var y = WorldPayload.ReadInt32(r);
                var speed = WorldPayload.ReadInt32(r);
                var hp = WorldPayload.ReadInt64(r);
                var hpMax = WorldPayload.ReadInt64(r);
                var shield = WorldPayload.ReadInt64(r);
                var shieldMax = WorldPayload.ReadInt64(r);
                var cargoLoad = WorldPayload.ReadInt32(r);
                var cargoMax = WorldPayload.ReadInt32(r);
                var laser = WorldPayload.ReadInt32(r);
                var xp = WorldPayload.ReadInt64(r);
                var honor = WorldPayload.ReadInt64(r);
                var level = WorldPayload.ReadInt32(r);
                var credits = WorldPayload.ReadInt64(r);
                var uridium = WorldPayload.ReadInt64(r);
                var slot = r.ReadByte();
                var count = WorldPayload.ReadInt32(r);
                var equipped = new List<(string, int)>();
                for (var i = 0; i < count; i++) equipped.Add((WorldPayload.ReadString(r), WorldPayload.ReadInt32(r)));
                return (id, name, ship, faction, map, x, y, speed, hp, hpMax, shield, shieldMax,
                    cargoLoad, cargoMax, laser, xp, honor, level, credits, uridium, slot, equipped);
            });
            Assert.Multiple(() =>
            {
                Assert.That((heroState.id, heroState.name, heroState.ship, heroState.faction, heroState.map,
                    heroState.x, heroState.y), Is.EqualTo((42L, "pilot42", "phoenix", 2, 5, 2000, 1300)));
                Assert.That((heroState.speed, heroState.hp, heroState.hpMax, heroState.shield,
                    heroState.shieldMax, heroState.cargoLoad, heroState.cargoMax, heroState.laser),
                    Is.EqualTo((320, 4000L, 4000L, 5000L, 5000L, 0, 100, 150)));
                Assert.That((heroState.xp, heroState.honor, heroState.level, heroState.credits,
                    heroState.uridium, heroState.slot), Is.EqualTo((120L, 30L, 2, 100L, 5L, (byte)2)));
                Assert.That(heroState.equipped, Is.EqualTo(new[] { ("laser_lf3", 1) }));
            });
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task SavedHangarAssignmentsAndPilotSkills_ChangeHeroStats()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var account = Guid.NewGuid();
        var equipment = new LocalHangarEquipmentStore(cache);
        Assert.That(await equipment.AssignAsync(account, 1, 1, "laser_lf3", 4, CancellationToken.None), Is.True);
        Assert.That(await equipment.AssignAsync(account, 1, 1, "shield_sg3n_b02", 2, CancellationToken.None), Is.True);
        var skills = new LocalPilotSkillStore(cache);
        Assert.That(await skills.AllocateAsync(account, "hull", CancellationToken.None), Is.True);
        Assert.That(await skills.AllocateAsync(account, "shields", CancellationToken.None), Is.True);
        Assert.That(await skills.AllocateAsync(account, "lasers", CancellationToken.None), Is.True);
        var stored = await equipment.GetAsync(account, CancellationToken.None);
        var trained = await skills.GetAsync(account, CancellationToken.None);
        var pusat = WorldShipStatCalculator.Calculate(1, stored, trained);
        Assert.Multiple(() =>
        {
            Assert.That(pusat.Speed, Is.EqualTo(370));
            Assert.That(pusat.MaxHitPoints, Is.EqualTo(130560));
            Assert.That(pusat.MaxShield, Is.EqualTo(10200));
            Assert.That(pusat.CargoCapacity, Is.EqualTo(1500));
            Assert.That(pusat.LaserDamage, Is.EqualTo(612));
            Assert.That(pusat.Equipment.Count, Is.EqualTo(2));
        });
        var phoenix = WorldShipStatCalculator.Calculate(2, stored, trained);
        Assert.That(phoenix.Speed, Is.EqualTo(320));
        Assert.That(phoenix.MaxShield, Is.Zero);
        Assert.That(phoenix.LaserDamage, Is.Zero);
    }

    [Test]
    public void EquippedSpeedGenerator_UsesGeneratorSlotsAndRaisesSpeed()
    {
        var item = new LocalEquipmentItem("generator_g3n_3310", "G3N-3310", "Equipment", 2, "generator");
        Assert.That(LocalHangarEquipmentStore.CapacityKind(item),
            Is.EqualTo(LocalHangarEquipmentStore.GeneratorKind));
        var equipment = new LocalEquipmentState([item],
            [new LocalEquipmentAssignment(1, 1, item.Id, 2)]);
        var stats = WorldShipStatCalculator.Calculate(1, equipment,
            new LocalPilotSkillState(new Dictionary<string, int>()));
        Assert.That(stats.Speed, Is.EqualTo(450));
        var alternate = new LocalEquipmentState([item],
            [new LocalEquipmentAssignment(1, 2, item.Id, 2)]);
        Assert.That(WorldShipStatCalculator.Calculate(1, alternate,
            new LocalPilotSkillState(new Dictionary<string, int>()), configuration: 2).Speed,
            Is.EqualTo(450));
        Assert.That(WorldShipStatCalculator.Calculate(1, alternate,
            new LocalPilotSkillState(new Dictionary<string, int>()), configuration: 1).Speed,
            Is.EqualTo(370));
    }

    [Test]
    public void SavedCatalogStats_OverrideLocalEquipmentDefaults()
    {
        var item = new LocalEquipmentItem("laser_lf3", "LF-3", "Equipment", 1, "laser");
        var equipment = new LocalEquipmentState([item], [new LocalEquipmentAssignment(1, 1, item.Id, 1)]);
        var listing = new EconomyListing("lf3", item.Id, item.Name, "laser", "common",
            JsonSerializer.Deserialize<JsonElement>("{\"damage\":200}"), null,
            JsonSerializer.Deserialize<JsonElement>("{}"), null, null, true);
        var stats = WorldShipStatCalculator.Calculate(1, equipment,
            new LocalPilotSkillState(new Dictionary<string, int>()), [listing]);
        Assert.That(stats.LaserDamage, Is.EqualTo(200));
    }

    [Test]
    public async Task WorldSessionStore_TracksConnectionAndReloadsSavedPosition()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache);
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship_pusat-plus", 10, 2)
            { X = 3000, Y = 4000 };
        var entered = await store.EnterAsync(pilot, CancellationToken.None);
        Assert.That(store.Connected(1), Has.Count.EqualTo(1));
        store.Leave(entered);
        Assert.That(store.Connected(1), Is.Empty);
        var reopened = await new WorldSessionStore(cache).EnterAsync(pilot with { X = 2000, Y = 1300 },
            CancellationToken.None);
        Assert.That((reopened.X, reopened.Y), Is.EqualTo((3000, 4000)));
    }

    [Test]
    public async Task Movement_InterpolatesCourseChanges_AndRestoresMidFlightPosition()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache);
        var pilot = await store.EnterAsync(new WorldPilot(87, "mmo", 1, 1, "ship", 0, 0)
            { X = 1000, Y = 1000, Stats = new WorldShipStats(100, 1, 0, 0, 0, []) }, CancellationToken.None);
        var start = DateTimeOffset.UtcNow;
        var first = await store.MoveAsync(pilot, 2000, 1000, null, CancellationToken.None, start);
        Assert.That(first.TimeToTargetMs, Is.EqualTo(10000));
        Assert.That(store.CurrentPosition(pilot, start.AddSeconds(5)), Is.EqualTo((1500, 1000)));
        var second = await store.MoveAsync(pilot, 1500, 2000, null, CancellationToken.None, start.AddSeconds(5));
        Assert.That(second.TimeToTargetMs, Is.EqualTo(10000));
        Assert.That(store.CurrentPosition(pilot, start.AddSeconds(10)), Is.EqualTo((1500, 1500)));
        var reopened = await new WorldSessionStore(cache).EnterAsync(pilot with { X = 2000, Y = 1300 },
            CancellationToken.None);
        Assert.That(reopened.X, Is.EqualTo(1500));
        Assert.That(reopened.Y, Is.InRange(1000, 2000));
    }

    [Test]
    public async Task Movement_ClampsBounds_AndUsesSelectedConfigurationSpeed()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = await store.EnterAsync(new WorldPilot(88, "mmo", 1, 1, "ship", 0, 0)
            { X = 0, Y = 0, Stats = new WorldShipStats(100, 1000, 0, 0, 0, []),
                ConfigurationTwoSpeed = 200 }, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var edge = await store.MoveAsync(pilot, -100, 15000, null, CancellationToken.None, now);
        Assert.That((edge.X, edge.Y, edge.RadiationWarning), Is.EqualTo((0, 13100, true)));
        var switched = await store.MoveAsync(pilot, 0, 13100, 2, CancellationToken.None, now);
        Assert.That(switched.TimeToTargetMs, Is.EqualTo(65500));
        Assert.That((await store.MoveAsync(pilot, 21001, 0, null, CancellationToken.None, now))
            .RadiationWarning, Is.True);
        Assert.That((await store.MoveAsync(pilot, 21000, 13100, null, CancellationToken.None, now))
            .RadiationWarning, Is.True);
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.MoveAsync(pilot, 0, 0, 3, CancellationToken.None, now));
    }

    [Test]
    public async Task Loopback_MoveRequest_BroadcastsClampedDestinationWithoutPrematureWarning()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(89, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(100, 1000, 0, 0, 0, []), ConfigurationTwoSpeed = 200 };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(89, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 9999); // untrusted reported X
                WorldPayload.WriteInt32(w, 1300); // target Y
                WorldPayload.WriteInt32(w, 22000); // target X
                WorldPayload.WriteInt32(w, 9999); // untrusted reported Y
            }));
            var move = (await input.ReadAsync())!.Value;
            Assert.That(move.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var values = WorldPayload.Decode(move.Payload, r => (WorldPayload.ReadInt64(r),
                WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r)));
            Assert.That(values, Is.EqualTo((89L, 21000, 1300, 190000)));
            Assert.That((await input.ReadAsync())!.Value.Command,
                Is.EqualTo(WorldCommandId.SafeZoneCommand));
            Assert.That(store.RadiationWarning(pilot.UserId), Is.False);
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_ConfigurationSwitch_RecalculatesRemainingTravelTime()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(90, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(100, 1, 1000, 0, 100, []),
                ConfigurationTwoStats = new WorldShipStats(200, 1, 2000, 0, 300, []) };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(90, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, 3000); WorldPayload.WriteInt32(w, 1300);
            }));
            var first = (await input.ReadAsync())!.Value;
            var duration = WorldPayload.Decode(first.Payload, r =>
            {
                WorldPayload.ReadInt64(r); WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
                return WorldPayload.ReadInt32(r);
            });
            Assert.That(duration, Is.EqualTo(10000));
            await output.WriteAsync(WorldCommandId.ConfigurationRequest, new byte[] { 2 });
            var second = (await input.ReadAsync())!.Value;
            var remaining = WorldPayload.Decode(second.Payload, r =>
            {
                WorldPayload.ReadInt64(r); WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
                return WorldPayload.ReadInt32(r);
            });
            Assert.That(remaining, Is.InRange(1, 5000));
            var attributes = (await input.ReadAsync())!.Value;
            Assert.That(attributes.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
            var changed = WorldPayload.Decode(attributes.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadByte(), WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r),
                    WorldPayload.ReadInt32(r)));
            Assert.That(changed.Item2, Is.EqualTo((byte)2));
            Assert.That(changed.Item3, Is.EqualTo(200));
            Assert.That(changed.Item6, Is.EqualTo(2000));
            Assert.That(changed.Item7, Is.EqualTo(300));
            await output.WriteAsync(WorldCommandId.ConfigurationRequest, new byte[] { 1 });
            var cooldown = (await input.ReadAsync())!.Value;
            Assert.That(cooldown.Command, Is.EqualTo(WorldCommandId.ConfigurationCooldownCommand));
            Assert.That(WorldPayload.Decode(cooldown.Payload, WorldPayload.ReadInt32), Is.InRange(1, 5000));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_CompanionsSpawnAndFollowMove()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(504, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1, 0, 0, 0, []),
            Companions = [new("drone", "drone_flax", 0, "standard"),
                new("pet", "pet_10", 0, "standard")]
        };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), store);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(504, "opaque", "local-1"));
            for (var i = 0; i < 12; i++) await input.ReadAsync();
            var spawned = (await input.ReadAsync())!.Value;
            Assert.That(spawned.Command, Is.EqualTo(WorldCommandId.CompanionStateCommand));
            var original = WorldPayload.Decode(spawned.Payload, r =>
            {
                WorldPayload.ReadInt64(r);
                var count = WorldPayload.ReadInt32(r);
                var companions = new List<(string Kind, string Item, int X, int Y)>();
                for (var i = 0; i < count; i++)
                {
                    var kind = WorldPayload.ReadString(r);
                    var item = WorldPayload.ReadString(r);
                    WorldPayload.ReadInt32(r); WorldPayload.ReadString(r);
                    var x = WorldPayload.ReadInt32(r);
                    var y = WorldPayload.ReadInt32(r);
                    WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
                    companions.Add((kind, item, x, y));
                }
                return companions;
            });
            Assert.That(original.Select(c => c.Item), Is.EqualTo(new[] { "drone_flax", "pet_10" }));
            await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, 3000); WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await input.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var followed = (await input.ReadAsync())!.Value;
            Assert.That(followed.Command, Is.EqualTo(WorldCommandId.CompanionStateCommand));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker;
        }
    }

    [Test]
    public async Task Loopback_MoveBroadcast_ReachesAnotherPilotOnSameMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(91, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(100, 1, 0, 0, 0, []) };
        var first = await ConnectAsync(new FakeResolver((pilot, default)), store);
        var second = await ConnectAsync(new FakeResolver((pilot with { UserId = 92 }, default)), store);
        using (first.Client) using (first.Server) using (second.Client) using (second.Server)
        {
            async Task<WorldPacketReader> LoginPilot(TcpClient client, long id)
            {
                var reader = new WorldPacketReader(client.GetStream());
                var writer = new WorldPacketWriter(client.GetStream());
                await writer.WriteAsync(WorldCommandId.VersionRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
                await reader.ReadAsync();
                await writer.WriteAsync(WorldCommandId.LoginRequest, Login(id, "opaque", "local-1"));
                for (var i = 0; i < 12; i++) await reader.ReadAsync();
                return reader;
            }
            var firstReader = await LoginPilot(first.Client, 91);
            var secondReader = await LoginPilot(second.Client, 92);
            await new WorldPacketWriter(first.Client.GetStream()).WriteAsync(WorldCommandId.MoveRequest,
                WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 1300);
                    WorldPayload.WriteInt32(w, 3000); WorldPayload.WriteInt32(w, 1300);
                }));
            Assert.That((await firstReader.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            var observed = (await secondReader.ReadAsync())!.Value;
            Assert.That(observed.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(WorldPayload.Decode(observed.Payload, r =>
            {
                var id = WorldPayload.ReadInt64(r);
                WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
                return id;
            }), Is.EqualTo(91L));
            first.Client.Client.Shutdown(SocketShutdown.Send);
            second.Client.Client.Shutdown(SocketShutdown.Send);
            await Task.WhenAll(first.Worker, second.Worker);
        }
    }

    [Test]
    public async Task HostedListener_BindsPublishedDynamicLoopbackPort()
    {
        // A released reservation leaves a race for another process to claim its port.
        var options = new WorldServerOptions { Enabled = true, DynamicTcpPort = true, Port = 0 };
        using var services = new ServiceCollection()
            .AddSingleton<IWorldPilotResolver>(new FakeResolver((null, WorldLoginError.UnknownSession)))
            .AddSingleton<IGameAuthSessionStore>(new FakeSessions(null))
            .AddDistributedMemoryCache()
            .AddSingleton<WorldSessionStore>()
            .BuildServiceProvider();
        var hosted = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 250 && hosted.ActualPort == 0; attempt++)
                await Task.Delay(20);
            Assert.That(hosted.ActualPort, Is.GreaterThan(0));
            Assert.That(options.Port, Is.EqualTo(hosted.ActualPort));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, hosted.ActualPort);
            Assert.That(client.Connected, Is.True);
            await new WorldPacketWriter(client.GetStream()).WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            Assert.That((await new WorldPacketReader(client.GetStream()).ReadAsync())!.Value.Command,
                Is.EqualTo(WorldCommandId.VersionCommand));
        }
        finally { await hosted.StopAsync(CancellationToken.None); hosted.Dispose(); }
    }

    [TestCase(WorldLoginError.UnknownSession)]
    [TestCase(WorldLoginError.ExpiredSession)]
    [TestCase(WorldLoginError.CompanyRequired)]
    [TestCase(WorldLoginError.UserMismatch)]
    public async Task Loopback_RejectsInvalidPilot(WorldLoginError reason)
    {
        var resolver = new FakeResolver((null, reason));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "local-1"));
            var response = (await input.ReadAsync())!.Value;
            Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
            Assert.That(response.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)reason }));
            await worker;
        }
    }

    [Test]
    public async Task Loopback_RejectsLoginBeforeVersionWithoutResolvingSession()
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            await new WorldPacketWriter(client.GetStream()).WriteAsync(WorldCommandId.LoginRequest,
                Login(42, "opaque", "local-1"));
            var response = (await new WorldPacketReader(client.GetStream()).ReadAsync())!.Value;
            Assert.That(response.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)WorldLoginError.UnsupportedVersion }));
            Assert.That(resolver.Calls, Is.Zero);
            await worker;
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Loopback_NativeVersionPreamble_AcceptsVersionWithoutResolvingSession(bool preamble)
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            // Native PacketParser uses a three-byte length; 0x029a is VersionRequest.
            await WriteNativeAsync(client.GetStream(), NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)), preamble);
            var version = await ReadNativeAsync(client.GetStream());
            Assert.That(version.Module, Is.EqualTo(NativeWorldHandshake.VersionCommand));
            Assert.That(WorldPayload.Decode(version.Payload, r =>
                (WorldPayload.ReadString(r), r.ReadBoolean())),
                Is.EqualTo((NativeWorldHandshake.ClientVersion, true)));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
            Assert.That(resolver.Calls, Is.Zero);
        }
    }

    [Test]
    public async Task Loopback_NativeLogin_AuthenticatesSessionAndSendsMapAndShip()
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 42,
            Company = "eic", Dosid = "opaque", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var account = new FakeAccountState();
        var resolver = new WorldPilotResolver(new FakeSessions(session), account);
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var stream = client.GetStream();
            await WriteNativeAsync(stream, NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)), true);
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.VersionCommand));
            await WriteNativeAsync(stream, NativeWorldHandshake.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(42u, 3));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)BitOperations.RotateLeft(2u, 7)));
                WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion);
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(1u, 12));
                w.Write(false);
            }));
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.LoginResponse));
            var map = await ReadNativeAsync(stream);
            Assert.That(map.Module, Is.EqualTo(NativeWorldHandshake.MapChanged));
            Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(map.Payload.Span), 4), Is.EqualTo(5));
            var hero = await ReadNativeAsync(stream);
            Assert.That(hero.Module, Is.EqualTo(NativeWorldHandshake.ShipInitialization));
            Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(hero.Payload.Span), 14), Is.EqualTo(42));
            var gate = await ReadNativeAsync(stream);
            Assert.That(gate.Module, Is.EqualTo(NativeWorldHandshake.JumpgateCreate));
            Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(gate.Payload.Span), 10), Is.EqualTo(501));
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(account.Calls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Loopback_NativeMove_UsesSavedOriginAndKeepsSocketOpenForNextCommand()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new WorldSessionStore(cache);
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship_phoenix", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), sessions);
        using (client)
        using (server)
        {
            var stream = client.GetStream();
            await WriteNativeAsync(stream, NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)));
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.VersionCommand));
            await WriteNativeAsync(stream, NativeWorldHandshake.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(42u, 3));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)BitOperations.RotateLeft(1u, 7)));
                WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion);
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(1u, 12));
                w.Write(false);
            }));
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.LoginResponse));
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.MapChanged));
            Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.ShipInitialization));
            foreach (var _ in WorldMapInstance.Starter(1).Portals)
                Assert.That((await ReadNativeAsync(stream)).Module, Is.EqualTo(NativeWorldHandshake.JumpgateCreate));

            async Task<int> MoveAsync(int x, int y)
            {
                await WriteNativeAsync(stream, NativeWorldHandshake.MoveRequest, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, 9999); // client position cannot set the travel origin
                    WorldPayload.WriteInt32(w, y);
                    WorldPayload.WriteInt32(w, x);
                    WorldPayload.WriteInt32(w, 9999);
                }));
                var hero = await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(hero.Module, Is.EqualTo(NativeWorldHandshake.HeroMoveCommand));
                Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(hero.Payload.Span), 9),
                    Is.EqualTo(x));
                Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(hero.Payload.Span[4..]), 25),
                    Is.EqualTo(y));
                var move = await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(move.Module, Is.EqualTo(NativeWorldHandshake.MoveCommand));
                Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(move.Payload.Span), 16),
                    Is.EqualTo(42));
                Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(move.Payload.Span[4..]), 13),
                    Is.EqualTo(x));
                Assert.That(BitOperations.RotateRight((uint)BinaryPrimitives.ReadInt32BigEndian(move.Payload.Span[8..]), 16),
                    Is.EqualTo(y));
                return (int)BitOperations.RotateRight(
                    (uint)BinaryPrimitives.ReadInt32BigEndian(move.Payload.Span[12..]), 18);
            }

            Assert.That(await MoveAsync(2300, 1300), Is.EqualTo(1000));
            Assert.That(sessions.Destination(pilot), Is.EqualTo((2300, 1300)));
            await MoveAsync(2400, 1400);
            Assert.That(sessions.Destination(pilot), Is.EqualTo((2400, 1400)));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            var restored = await new WorldSessionStore(cache).RestoreAsync(pilot, CancellationToken.None);
            Assert.That(restored.MapId, Is.EqualTo(1));
            using var saved = JsonDocument.Parse((await cache.GetStringAsync("world:position:42"))!);
            Assert.That(saved.RootElement.GetProperty("TargetX").GetInt32(), Is.EqualTo(2400));
            Assert.That(saved.RootElement.GetProperty("TargetY").GetInt32(), Is.EqualTo(1400));
            Assert.That(sessions.Connected(1), Is.Empty);
        }
    }

    [Test]
    public async Task Loopback_NativeMove_KeepsHullAndReportsChangedShieldWithClientRotations()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship_phoenix", 0, 0)
        {
            FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 100, 0, 30, [])
        };
        var (client, server, worker) = await ConnectAsync(new FakeResolver((pilot, default)), sessions);
        using (client)
        using (server)
        {
            var stream = client.GetStream();
            await WriteNativeAsync(stream, NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.VersionCommand));
            await WriteNativeAsync(stream, NativeWorldHandshake.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(42u, 3));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)BitOperations.RotateLeft(1u, 7)));
                WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion);
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(1u, 12));
                w.Write(false);
            }));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.LoginResponse));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.MapChanged));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.ShipInitialization));
            foreach (var _ in WorldMapInstance.Starter(1).Portals)
                Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.JumpgateCreate));

            await WriteNativeAsync(stream, NativeWorldHandshake.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000);
                WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, 2000);
                WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module,
                Is.EqualTo(NativeWorldHandshake.HeroMoveCommand));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module,
                Is.EqualTo(NativeWorldHandshake.MoveCommand));
            sessions.SetCurrentShield(pilot, 40);
            await WriteNativeAsync(stream, NativeWorldHandshake.MoveRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, 2000);
                WorldPayload.WriteInt32(w, 1300);
                WorldPayload.WriteInt32(w, -1); // movement does not advance radiation damage
                WorldPayload.WriteInt32(w, 1300);
            }));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.HeroMoveCommand));
            Assert.That((await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2))).Module, Is.EqualTo(NativeWorldHandshake.MoveCommand));
            Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(1000));
            var shield = await ReadNativeAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(shield.Module, Is.EqualTo(NativeWorldHandshake.AttributeShieldUpdate));
            Assert.That(shield.Payload.Length, Is.EqualTo(8));
            Assert.That(BitOperations.RotateLeft((uint)BinaryPrimitives.ReadInt32BigEndian(shield.Payload.Span), 16),
                Is.EqualTo(40));
            Assert.That(BitOperations.RotateLeft((uint)BinaryPrimitives.ReadInt32BigEndian(shield.Payload.Span[4..]), 20),
                Is.EqualTo(100));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestCase(1, false)]
    [TestCase(2, true)]
    public async Task Loopback_NativeLogin_RequiresSelectedSessionInstance(int requested, bool accepted)
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 42,
            Company = "eic", Dosid = "opaque", InstanceId = 2,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var auth = new FakeSessions(session);
        var resolver = new WorldPilotResolver(auth, new FakeAccountState());
        var (client, server, worker) = await ConnectAsync(resolver, auth: auth);
        using (client)
        using (server)
        {
            var stream = client.GetStream();
            await WriteNativeAsync(stream, NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)));
            await ReadNativeAsync(stream);
            await WriteNativeAsync(stream, NativeWorldHandshake.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(42u, 3));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)BitOperations.RotateLeft(2u, 7)));
                WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion);
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft((uint)requested, 12));
                w.Write(false);
            }));
            var reply = await ReadNativeAsync(stream);
            Assert.That(reply.Module, Is.EqualTo(NativeWorldHandshake.LoginResponse));
            Assert.That(reply.Payload.ToArray(), Is.EqualTo(accepted ? new byte[] { 0, 0 } : new byte[] { 0, 1 }));
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public void NativeWorldPackets_UseIl2CppFieldOrderAndRotations()
    {
        var pilot = new WorldPilot(42, "eic", 5, 2, "phoenix", 1234, 56)
        {
            UserName = "Ace", FactionId = 2, X = 345, Y = 678, IsInvisible = true,
            Stats = new WorldShipStats(320, 4000, 5000, 100, 150, []),
            Experience = 789, Honor = 1011, Level = 7
        };
        using var bytes = new MemoryStream(NativeWorldPackets.ShipInitialization(pilot));
        using var reader = new BinaryReader(bytes);
        uint Number(int left) => BitOperations.RotateRight((uint)WorldPayload.ReadInt32(reader), left);
        long DoubleNumber() => checked((long)BitConverter.Int64BitsToDouble(WorldPayload.ReadInt64(reader)));
        Assert.Multiple(() =>
        {
            Assert.That(Number(14), Is.EqualTo(42));
            Assert.That(WorldPayload.ReadString(reader), Is.EqualTo("Ace"));
            Assert.That(WorldPayload.ReadString(reader), Is.EqualTo("phoenix"));
            Assert.That(Number(20), Is.EqualTo(320));
            Assert.That(Number(18), Is.EqualTo(5000));
            Assert.That(Number(19), Is.EqualTo(5000));
            Assert.That(DoubleNumber(), Is.EqualTo(4000));
            Assert.That(DoubleNumber(), Is.EqualTo(4000));
            Assert.That(Number(22), Is.EqualTo(100));
            Assert.That(Number(14), Is.EqualTo(100));
            Assert.That(Number(11), Is.Zero);
            Assert.That(Number(15), Is.Zero);
            Assert.That(Number(14), Is.EqualTo(345));
            Assert.That(Number(4), Is.EqualTo(678));
            Assert.That(Number(21), Is.EqualTo(5));
            Assert.That(Number(27), Is.EqualTo(2));
            Assert.That(Number(29), Is.Zero);
            Assert.That(Number(5), Is.Zero);
            Assert.That(reader.ReadBoolean(), Is.False);
            Assert.That(DoubleNumber(), Is.EqualTo(789));
            Assert.That(DoubleNumber(), Is.EqualTo(1011));
            Assert.That(Number(15), Is.EqualTo(7));
            Assert.That(DoubleNumber(), Is.EqualTo(1234));
            Assert.That(DoubleNumber(), Is.EqualTo(56));
            Assert.That(BitConverter.Int32BitsToSingle(WorldPayload.ReadInt32(reader)), Is.Zero);
            Assert.That(Number(18), Is.Zero);
            Assert.That(WorldPayload.ReadString(reader), Is.Empty);
            Assert.That(Number(27), Is.Zero);
            Assert.That(reader.ReadBoolean(), Is.False);
            Assert.That(reader.ReadBoolean(), Is.True);
            Assert.That(reader.ReadBoolean(), Is.True);
            Assert.That(Number(23), Is.Zero);
            Assert.That(Number(31), Is.Zero);
            Assert.That(reader.ReadByte(), Is.Zero);
            Assert.That(bytes.Position, Is.EqualTo(bytes.Length));
        });
        var visiblePayload = NativeWorldPackets.ShipInitialization(pilot with { IsInvisible = false });
        var cloakedPayload = bytes.ToArray();
        Assert.That(visiblePayload.Length, Is.EqualTo(cloakedPayload.Length));
        Assert.That(visiblePayload.Zip(cloakedPayload).Count(pair => pair.First != pair.Second), Is.EqualTo(1));

        using var move = new MemoryStream(NativeWorldPackets.Move(42, 345, 678, 900));
        using var moveReader = new BinaryReader(move);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(moveReader), 16), Is.EqualTo(42));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(moveReader), 13), Is.EqualTo(345));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(moveReader), 16), Is.EqualTo(678));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(moveReader), 18), Is.EqualTo(900));
        Assert.That(move.Position, Is.EqualTo(move.Length));

        using var laser = new MemoryStream(NativeWorldPackets.Laser(42, 123, 7, 9, true));
        using var laserReader = new BinaryReader(laser);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(laserReader), 2), Is.EqualTo(42));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(laserReader), 31), Is.EqualTo(123));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(laserReader), 16), Is.EqualTo(7));
        Assert.That(laserReader.ReadBoolean(), Is.True);
        Assert.That(laserReader.ReadBoolean(), Is.False);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(laserReader), 23), Is.EqualTo(9));
        Assert.That(laser.Position, Is.EqualTo(laser.Length));

        using var rocket = new MemoryStream(NativeWorldPackets.Rocket(42, 123, 4, 5, true));
        using var rocketReader = new BinaryReader(rocket);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(rocketReader), 16), Is.EqualTo(42));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(rocketReader), 11), Is.EqualTo(123));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(rocketReader), 3), Is.EqualTo(4));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(rocketReader), 3), Is.EqualTo(5));
        Assert.That(rocketReader.ReadBoolean(), Is.True);
        Assert.That(rocketReader.ReadBoolean(), Is.False);
        Assert.That(rocket.Position, Is.EqualTo(rocket.Length));

        using var hit = new MemoryStream(NativeWorldPackets.Hit(1, 42, 123, 4000, 500, 0, 75,
            false, true, 2));
        using var hitReader = new BinaryReader(hit);
        Assert.That(BinaryPrimitives.ReverseEndianness(hitReader.ReadUInt16()), Is.EqualTo(0x001b));
        Assert.That(BinaryPrimitives.ReverseEndianness(hitReader.ReadInt16()), Is.EqualTo(1));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(hitReader), 25), Is.EqualTo(42));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(hitReader), 3), Is.EqualTo(123));
        Assert.That(BitConverter.Int64BitsToDouble(WorldPayload.ReadInt64(hitReader)), Is.EqualTo(4000));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(hitReader), 19), Is.EqualTo(500));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(hitReader), 8), Is.Zero);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(hitReader), 8), Is.EqualTo(75));
        Assert.That(hitReader.ReadBoolean(), Is.False);
        Assert.That(hitReader.ReadBoolean(), Is.True);
        Assert.That(BinaryPrimitives.ReverseEndianness(hitReader.ReadInt16()), Is.EqualTo(2));
        Assert.That(hit.Position, Is.EqualTo(hit.Length));

        using var jump = new MemoryStream(NativeWorldPackets.JumpInitiated(6, 501));
        using var jumpReader = new BinaryReader(jump);
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(jumpReader), 25), Is.EqualTo(6));
        Assert.That(BitOperations.RotateRight((uint)WorldPayload.ReadInt32(jumpReader), 26), Is.EqualTo(501));
        Assert.That(jump.Position, Is.EqualTo(jump.Length));
    }

    [Test]
    public async Task Loopback_NativeLogin_RejectsWrongSessionBeforeMapEntry()
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 42,
            Company = "eic", Dosid = "expected", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var account = new FakeAccountState();
        var (client, server, worker) = await ConnectAsync(
            new WorldPilotResolver(new FakeSessions(session), account));
        using (client)
        using (server)
        {
            var stream = client.GetStream();
            await WriteNativeAsync(stream, NativeWorldHandshake.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion)));
            await ReadNativeAsync(stream);
            await WriteNativeAsync(stream, NativeWorldHandshake.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(42u, 3));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)BitOperations.RotateLeft(2u, 7)));
                WorldPayload.WriteString(w, "wrong");
                WorldPayload.WriteString(w, NativeWorldHandshake.ClientVersion);
                WorldPayload.WriteInt32(w, (int)BitOperations.RotateLeft(1u, 12));
                w.Write(false);
            }));
            var response = await ReadNativeAsync(stream);
            Assert.That(response.Module, Is.EqualTo(NativeWorldHandshake.LoginResponse));
            Assert.That(response.Payload.ToArray(), Is.Not.EqualTo(new byte[] { 0, 0 }));
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(account.Calls, Is.Zero);
        }
    }

    private static async Task WriteNativeAsync(Stream stream, ushort module, byte[] payload, bool preamble = false)
    {
        var length = payload.Length + 2 + (preamble ? 1 : 0);
        var frame = new byte[3 + length];
        frame[1] = (byte)(length >> 8);
        frame[2] = (byte)length;
        var offset = preamble ? 4 : 3;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), module);
        payload.CopyTo(frame, offset + 2);
        await stream.WriteAsync(frame);
    }

    private static async Task<(ushort Module, ReadOnlyMemory<byte> Payload)> ReadNativeAsync(Stream stream)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header);
        var length = header[0] << 16 | header[1] << 8 | header[2];
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadUInt16BigEndian(body), body.AsMemory(2));
    }

    [Test]
    public async Task Loopback_OversizedPreloginFrame_RejectsHeaderWithoutWaitingForBody()
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            await client.GetStream().WriteAsync(new byte[] { 0, 0, 0x20, 1 });
            var error = (await new WorldPacketReader(client.GetStream()).ReadAsync()
                .WaitAsync(TimeSpan.FromSeconds(2)))!.Value;
            Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
            Assert.That(error.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)WorldLoginError.Malformed }));
            Assert.That(resolver.Calls, Is.Zero);
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task Loopback_MalformedVersionRequest_ReturnsMalformedWithoutIdentityLookup(int shape)
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var payload = shape switch
            {
                0 => new byte[] { 0 }, // truncated length prefix
                1 => new byte[] { 0, 1, 0xff }, // invalid UTF-8
                _ => new byte[] { 0, 0, 0x7f } // trailing byte
            };
            await new WorldPacketWriter(client.GetStream()).WriteAsync(WorldCommandId.VersionRequest, payload);
            var response = (await new WorldPacketReader(client.GetStream()).ReadAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(response.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.Malformed }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task Loopback_MalformedLoginAfterVersion_ReturnsMalformedWithoutIdentityLookup(int shape)
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.VersionCommand));
            var payload = shape switch
            {
                0 => new byte[] { 0 }, // truncated user ID
                1 => WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt64(w, 42);
                    w.Write((byte)0);
                    w.Write((byte)1);
                    w.Write((byte)0xff);
                }),
                _ => Login(42, "opaque", "local-1").Concat(new byte[] { 0x7f }).ToArray()
            };
            await output.WriteAsync(WorldCommandId.LoginRequest, payload);
            var response = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(response.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.Malformed }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public async Task Loopback_LoginBeforeVersion_ReportsHandshakeErrorBeforeDecodingMalformedPayload()
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            await new WorldPacketWriter(client.GetStream()).WriteAsync(WorldCommandId.LoginRequest, new byte[] { 0 });
            var response = (await new WorldPacketReader(client.GetStream()).ReadAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(response.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(response.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.UnsupportedVersion }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [TestCase(WorldCommandId.VersionCommand, false)]
    [TestCase(WorldCommandId.LoginResponse, true)]
    [TestCase(WorldCommandId.MoveRequest, false)]
    [TestCase((WorldCommandId)ushort.MaxValue, true)]
    public async Task Loopback_NonHandshakePreloginCommand_ReturnsUnknownCommandWithoutDecodingOrResolving(
        WorldCommandId command, bool acceptedVersion)
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            if (acceptedVersion)
            {
                await output.WriteAsync(WorldCommandId.VersionRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
                var version = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.That(version.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            }

            await output.WriteAsync(command, new byte[] { 0xff });
            var error = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(error.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.UnknownCommand }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [TestCase(WorldCommandId.VersionRequest)]
    [TestCase(WorldCommandId.LoginRequest)]
    [TestCase(WorldCommandId.LoginResponse)]
    [TestCase(WorldCommandId.MoveRequest)]
    [TestCase((WorldCommandId)ushort.MaxValue)]
    public async Task Loopback_UnsupportedAuthenticatedCommand_ReturnsUnknownCommandAndEndsSession(
        WorldCommandId command)
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "phoenix", 100, 5);
        var resolver = new FakeResolver((pilot, default));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.VersionCommand));
            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "local-1"));
            Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                Is.EqualTo(WorldCommandId.LoginResponse));
            for (var i = 0; i < 11; i++)
                Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                    Is.Not.EqualTo(WorldCommandId.LoginErrorCommand));

            await output.WriteAsync(command, new byte[] { 0xff });
            var error = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(error.Payload.ToArray(), Is.EqualTo(new[] { (byte)WorldLoginError.UnknownCommand }));
                Assert.That(resolver.Calls, Is.EqualTo(1));
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public async Task Loopback_VersionRetry_ReplacesRejectedHandshakeBeforeLogin()
    {
        var pilot = new WorldPilot(42, "eic", 5, 2, "phoenix", 100, 5);
        var resolver = new FakeResolver((pilot, default));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "obsolete")));
            var rejected = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(rejected.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(WorldPayload.Decode(rejected.Payload, r =>
                (WorldPayload.ReadString(r), r.ReadBoolean())), Is.EqualTo(("local-1", false)));
            Assert.That(resolver.Calls, Is.Zero);

            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            var accepted = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(accepted.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(WorldPayload.Decode(accepted.Payload, r =>
                (WorldPayload.ReadString(r), r.ReadBoolean())), Is.EqualTo(("local-1", true)));
            Assert.That(resolver.Calls, Is.Zero);

            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "local-1"));
            var login = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(login.Command, Is.EqualTo(WorldCommandId.LoginResponse));
            Assert.That(resolver.Calls, Is.EqualTo(1));
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_RejectedVersionRetry_RevokesEarlierAcceptance()
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            foreach (var (requested, expected) in new[] { ("local-1", true), ("obsolete", false) })
            {
                await output.WriteAsync(WorldCommandId.VersionRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteString(w, requested)));
                var reply = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
                Assert.That(reply.Command, Is.EqualTo(WorldCommandId.VersionCommand));
                Assert.That(WorldPayload.Decode(reply.Payload, r =>
                    (WorldPayload.ReadString(r), r.ReadBoolean())), Is.EqualTo(("local-1", expected)));
            }

            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "local-1"));
            var error = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(error.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)WorldLoginError.UnsupportedVersion }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(0)]
    [TestCase(129)]
    public async Task Loopback_InvalidInstanceIdLength_RejectsBeforeIdentityResolution(int length)
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await output.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt64(w, 42);
                WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, "local-1");
                WorldPayload.WriteString(w, new string('i', length));
            }));
            var error = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(error.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)WorldLoginError.UnsupportedVersion }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Loopback_LoginVersionMismatchAfterAcceptedHandshake_EndsHandlerWithoutResolvingSession()
    {
        var resolver = new FakeResolver((null, WorldLoginError.UnknownSession));
        var (client, server, worker) = await ConnectAsync(resolver);
        using (client)
        using (server)
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            var version = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.That(WorldPayload.Decode(version.Payload, r =>
                (WorldPayload.ReadString(r), r.ReadBoolean())), Is.EqualTo(("local-1", true)));

            await output.WriteAsync(WorldCommandId.LoginRequest, Login(42, "opaque", "obsolete"));
            var error = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(error.Command, Is.EqualTo(WorldCommandId.LoginErrorCommand));
                Assert.That(error.Payload.ToArray(), Is.EqualTo(new byte[] { (byte)WorldLoginError.UnsupportedVersion }));
                Assert.That(resolver.Calls, Is.Zero);
            });
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(worker.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void MapCatalogue_AdvertisesSameConfiguredLoopbackPortForEveryCompany()
    {
        var options = new WorldServerOptions { Enabled = true, Port = 61000 };
        var xml = System.Xml.Linq.XDocument.Parse(LocalMapsXml.XmlFor(options));
        foreach (var company in CompanyCatalog.Companies)
            Assert.That(xml.Root!.Elements("map").Single(e => (string)e.Attribute("ID")! == company.StarterMap)
                .Attribute("GameServerIP")!.Value, Is.EqualTo("127.0.0.1:61000"));
    }

    [TestCase(null, WorldLoginError.UnknownSession)]
    [TestCase("", WorldLoginError.CompanyRequired)]
    [TestCase("invalid", WorldLoginError.CompanyRequired)]
    [TestCase("mmo", WorldLoginError.UserMismatch, 99L)]
    public async Task Resolver_RejectsMissingCompanyOrWrongUserBeforeAccountLookup(string? company,
        WorldLoginError expected, long userId = 42)
    {
        var session = company is null ? null : new GameAuthSession
        {
            AccountId = Guid.NewGuid(), GameUserId = 42, Company = company,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var state = new FakeAccountState();
        var (pilot, error) = await new WorldPilotResolver(new FakeSessions(session), state)
            .ResolveAsync(userId, "test-token", CancellationToken.None);
        Assert.That(pilot, Is.Null);
        Assert.That(error, Is.EqualTo(expected));
        Assert.That(state.Calls, Is.Zero);
    }

    [Test]
    public async Task Resolver_RejectsExpiredSessionBeforeAccountLookup()
    {
        var session = new GameAuthSession
        {
            AccountId = Guid.NewGuid(), GameUserId = 42, Company = "mmo",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        var state = new FakeAccountState();
        var (pilot, error) = await new WorldPilotResolver(new FakeSessions(session), state)
            .ResolveAsync(42, "test-token", CancellationToken.None);
        Assert.That(pilot, Is.Null);
        Assert.That(error, Is.EqualTo(WorldLoginError.ExpiredSession));
        Assert.That(state.Calls, Is.Zero);
    }

    [TestCase("mmo", 1)] [TestCase("eic", 5)]
    [TestCase("vru", 9)] [TestCase("tst", 13)]
    public async Task Resolver_LoadsSavedShipAndBalancesForCompany(string company, int map)
    {
        var session = new GameAuthSession
        {
            AccountId = Guid.NewGuid(), GameUserId = 42, Company = company,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var state = new FakeAccountState();
        var (pilot, _) = await new WorldPilotResolver(new FakeSessions(session), state)
            .ResolveAsync(42, "test-token", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(pilot?.UserId, Is.EqualTo(42));
            Assert.That(pilot?.Company, Is.EqualTo(company));
            Assert.That(pilot?.MapId, Is.EqualTo(map));
            Assert.That(pilot?.FactionId, Is.EqualTo((map + 3) / 4));
            Assert.That(pilot?.HangarSlot, Is.EqualTo(2));
            Assert.That(pilot?.ShipId, Is.EqualTo("phoenix"));
            Assert.That(pilot?.Credits, Is.EqualTo(100));
            Assert.That(pilot?.Uridium, Is.EqualTo(5));
        });
        Assert.That(state.Calls, Is.EqualTo(1));
    }

    [TestCase("owner", false, true)]
    [TestCase("pilot", true, true)]
    [TestCase("pilot", false, false)]
    public async Task Resolver_MapsAuthenticatedAdminEligibility(string name, bool markedAdmin, bool expected)
    {
        var session = new GameAuthSession
        {
            AccountId = Guid.NewGuid(), Username = name, IsAdmin = markedAdmin,
            GameUserId = 42, Company = "mmo", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var (pilot, _) = await new WorldPilotResolver(new FakeSessions(session), new FakeAccountState())
            .ResolveAsync(42, "test-token", CancellationToken.None);
        Assert.That(pilot?.IsAdmin, Is.EqualTo(expected));
    }

    private static byte[] Login(long userId, string dosid, string version) => WorldPayload.Encode(w =>
    {
        WorldPayload.WriteInt64(w, userId);
        WorldPayload.WriteString(w, dosid);
        WorldPayload.WriteString(w, version);
        WorldPayload.WriteString(w, "test-instance");
    });

    private static async Task<(TcpClient Client, TcpClient Server, Task Worker)> ConnectAsync(IWorldPilotResolver resolver,
        WorldSessionStore? sessions = null, IServerMeshRegistry? registry = null, string? nodeId = null,
        WorldNpcs? npcs = null, WorldAdminEvents? adminEvents = null,
        IAdminCommandDispatcher? adminCommands = null, IRuntimeVariableRegistry? variables = null,
        IGameAuthSessionStore? auth = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        listener.Stop();
        var worker = new WorldSessionHandler(resolver, new WorldServerOptions(), sessions, registry, nodeId,
            npcs: npcs, adminEvents: adminEvents, adminCommands: adminCommands, variables: variables,
            auth: auth)
            .RunAsync(server.GetStream());
        return (client, server, worker);
    }

    private sealed class FakeResolver((WorldPilot? Pilot, WorldLoginError Error) answer) : IWorldPilotResolver
    {
        public int Calls { get; private set; }
        public Task<(WorldPilot? Pilot, WorldLoginError Error)> ResolveAsync(long userId, string dosid,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeAccountState : IWorldAccountState
    {
        public int Calls { get; private set; }
        public Task<(int Slot, string ShipId, long Credits, long Uridium)> GetAsync(GameAuthSession session,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult((2, "phoenix", 100L, 5L));
        }
    }

    private sealed class FakeSessions(GameAuthSession? session) : IGameAuthSessionStore
    {
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken ct) =>
            Task.FromResult(session is not null && session.Dosid.Length > 0 && session.Dosid != dosid
                ? null : session);
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? token,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class FailFirstHealWriteStream : MemoryStream
    {
        private bool failed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!failed)
            {
                failed = true;
                throw new IOException("Simulated heal delivery failure.");
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailNextWriteStream : MemoryStream
    {
        private bool failNext;
        public void FailNext() => failNext = true;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (failNext)
            {
                failNext = false;
                throw new IOException("Simulated packet delivery failure.");
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class PauseFirstWriteStream : MemoryStream
    {
        private readonly bool failFirstOnResume;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool first = true;
        public PauseFirstWriteStream(bool failFirstOnResume = false) => this.failFirstOnResume = failFirstOnResume;
        public Task FirstWriteStarted => started.Task;
        public void Resume() => resumed.SetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (first)
            {
                first = false;
                started.SetResult();
                await resumed.Task.WaitAsync(cancellationToken);
                if (failFirstOnResume) throw new IOException("Simulated packet delivery failure.");
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class PauseSecondWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writes;
        public Task SecondWriteStarted => started.Task;
        public void Resume() => resumed.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                started.SetResult();
                await resumed.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class AdvancingWorldClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
