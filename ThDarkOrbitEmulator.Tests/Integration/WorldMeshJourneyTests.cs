using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class WorldMeshJourneyTests
{
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Release(gate);
        }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Sessions(IReadOnlyDictionary<string, GameAuthSession> sessions) : IGameAuthSessionStore
    {
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken ct) =>
            Task.FromResult(sessions.GetValueOrDefault(dosid));
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

    private sealed class AccountState : IWorldAccountState
    {
        public Task<(int Slot, string ShipId, long Credits, long Uridium)> GetAsync(GameAuthSession session,
            CancellationToken ct) => Task.FromResult((session.Company == "tst" ? 2 : 1,
                session.Company == "tst" ? "phoenix" : "ship_pusat-plus", 100L, 5L));
        public Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, CancellationToken ct) =>
            Task.FromResult<WorldAccountDetails?>(new(new WorldShipStats(300, 1000, 500, 100, 500, []),
                0, 0, 1, 200, new WorldShipStats(200, 1000, 1000, 100, 600, []),
                [new WorldCompanion("drone", "drone_flax", 0, "standard")]));
    }

    private sealed class Wallet : IWorldRewardWallet
    {
        public int Credits;
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        { Credits++; return Task.FromResult(true); }
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class NpcRewards : IWorldNpcRewards
    {
        public int Credits;
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        { Credits++; return Task.CompletedTask; }
    }

    private static ServerMeshNode Node(int index, DateTimeOffset now) =>
        new($"mesh-{index}", "api-game", $"http://mesh-{index}", "127.0.0.1", 61000 + index,
            ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0);

    [Test]
    public async Task MapXmlRoutesAllCompaniesToTcpHeroAndMovementOnThreeNodes()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ClusterOptions()));
        var listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        try
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 3; i++)
                await registry.HeartbeatAsync(Node(i + 1, now) with
                { WorldTcpPort = ((IPEndPoint)listeners[i].LocalEndpoint).Port });
            var snapshot = await registry.GetSnapshotAsync();
            var xml = XDocument.Parse(LocalMapsXml.XmlFor(new WorldServerOptions { Enabled = true, Port = 60954 }, snapshot));
            var companies = new[] { (Code: "mmo", Map: 1), (Code: "eic", Map: 5),
                (Code: "vru", Map: 9), (Code: "tst", Map: 13) };
            var sessions = companies.Select((company, i) => new GameAuthSession
            {
                Dosid = $"tcp-fixture-{i}", AccountId = Guid.NewGuid(), GameUserId = 301 + i,
                Username = $"TCP pilot {i}", Company = company.Code,
                ExpiresAt = now.AddHours(1)
            }).ToDictionary(s => s.Dosid);
            var resolver = new WorldPilotResolver(new Sessions(sessions), new AccountState());
            var stores = Enumerable.Range(0, 3).Select(_ => new WorldSessionStore(cache, mutex)).ToArray();
            foreach (var (company, i) in companies.Select((c, i) => (c, i)))
            {
                var owner = snapshot.Leases.Single(l => l.MapId == company.Map).NodeId;
                var nodeIndex = int.Parse(owner[5..]) - 1;
                var address = xml.Root!.Elements("map")
                    .Single(e => (int)e.Attribute("id")! == company.Map)
                    .Element("gameserverIP")!.Value;
                Assert.That(address, Is.EqualTo($"127.0.0.1:{((IPEndPoint)listeners[nodeIndex].LocalEndpoint).Port}"));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback,
                    ((IPEndPoint)listeners[nodeIndex].LocalEndpoint).Port);
                using var server = await listeners[nodeIndex].AcceptTcpClientAsync();
                var worker = new WorldSessionHandler(resolver, new WorldServerOptions(), stores[nodeIndex],
                    registry, owner).RunAsync(server.GetStream());
                var input = new WorldPacketReader(client.GetStream());
                var output = new WorldPacketWriter(client.GetStream());
                await output.WriteAsync(WorldCommandId.VersionRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
                Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                    Is.EqualTo(WorldCommandId.VersionCommand));
                await output.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt64(w, 301 + i);
                    WorldPayload.WriteString(w, $"tcp-fixture-{i}");
                    WorldPayload.WriteString(w, "local-1");
                    WorldPayload.WriteString(w, "mesh-journey");
                }));
                Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                    Is.EqualTo(WorldCommandId.LoginResponse));
                WorldPacket hero;
                do { hero = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value; }
                while (hero.Command != WorldCommandId.HeroInitCommand);
                using var heroStream = new MemoryStream(hero.Payload.ToArray());
                using var heroReader = new BinaryReader(heroStream);
                var id = WorldPayload.ReadInt64(heroReader);
                _ = WorldPayload.ReadString(heroReader);
                var ship = WorldPayload.ReadString(heroReader);
                var faction = WorldPayload.ReadInt32(heroReader);
                var map = WorldPayload.ReadInt32(heroReader);
                var identity = (id, ship, faction, map);
                Assert.That(identity, Is.EqualTo(((long)(301 + i),
                    company.Code == "tst" ? "phoenix" : "ship_pusat-plus", (company.Map + 3) / 4, company.Map)));
                Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                    Is.EqualTo(WorldCommandId.CompanionStateCommand));
                await output.WriteAsync(WorldCommandId.MoveRequest, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, 2000); WorldPayload.WriteInt32(w, 3000);
                    WorldPayload.WriteInt32(w, 4000); WorldPayload.WriteInt32(w, 1300);
                }));
                Assert.That((await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Command,
                    Is.EqualTo(WorldCommandId.MoveCommand));
                client.Client.Shutdown(SocketShutdown.Send);
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { foreach (var listener in listeners) listener.Stop(); }
    }

    [Test]
    public async Task OneToThreeToFourNodes_RebalancesMapXmlAndPreservesPilotsThroughDrain()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var options = Options.Create(new ClusterOptions());
        var mesh = Enumerable.Range(0, 4).Select(_ =>
            new ServerMeshCoordinator(cache, mutex, TimeProvider.System, options)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var one = await mesh[0].HeartbeatAsync(Node(1, now));
        Assert.That(one.Leases.Select(l => l.NodeId).Distinct(), Is.EqualTo(new[] { "mesh-1" }));
        await mesh[0].ScaleAsync(1, 16);
        await mesh[1].HeartbeatAsync(Node(2, now));
        await mesh[2].HeartbeatAsync(Node(3, now));
        var three = await mesh[0].ScaleAsync(3, 8);
        Assert.That(three.DesiredReplicas, Is.EqualTo(3));
        Assert.That(three.Nodes.Count(n => n.Status == ServerMeshNodeStatus.Active && !n.Simulated), Is.EqualTo(3));
        Assert.That(three.Leases.Select(l => l.MapId), Is.EquivalentTo(ServerMeshCoordinator.GameMapIds));
        Assert.That(three.Leases.GroupBy(l => l.NodeId).Select(g => g.Count()), Has.All.InRange(5, 6));

        var companies = new[] { (Code: "mmo", Map: 1), (Code: "eic", Map: 5),
            (Code: "vru", Map: 9), (Code: "tst", Map: 13) };
        var auth = new Sessions(companies.Select((c, i) => new GameAuthSession
        {
            Dosid = $"fixture-{i}", AccountId = Guid.NewGuid(), GameUserId = 100 + i,
            Username = $"Pilot {i}", Company = c.Code, ExpiresAt = now.AddHours(1)
        }).ToDictionary(s => s.Dosid));
        var resolver = new WorldPilotResolver(auth, new AccountState());
        var stores = Enumerable.Range(0, 4).Select(_ => new WorldSessionStore(cache, mutex)).ToArray();
        var xml = XDocument.Parse(LocalMapsXml.XmlFor(new WorldServerOptions { Enabled = true, Port = 60954 }, three));
        foreach (var (company, index) in companies.Select((c, i) => (c, i)))
        {
            var lease = three.Leases.Single(l => l.MapId == company.Map);
            var node = three.Nodes.Single(n => n.NodeId == lease.NodeId);
            var address = xml.Root!.Elements("map").Single(e => (int)e.Attribute("id")! == company.Map)
                .Element("gameserverIP")!.Value;
            Assert.That(address, Is.EqualTo($"127.0.0.1:{node.WorldTcpPort}"));
            var entry = await HttpAsync("enter", null, $"fixture-{index}", auth, resolver, stores[index]);
            using var state = JsonDocument.Parse(entry.Body);
            Assert.Multiple(() =>
            {
                Assert.That(state.RootElement.GetProperty("map").GetProperty("id").GetInt32(), Is.EqualTo(company.Map));
                Assert.That(state.RootElement.GetProperty("pilot").GetProperty("company").GetString(), Is.EqualTo(company.Code));
                Assert.That(state.RootElement.GetProperty("pilot").GetProperty("shipId").GetString(),
                    Is.EqualTo(company.Code == "tst" ? "phoenix" : "ship_pusat-plus"));
                Assert.That(state.RootElement.GetProperty("pilot").GetProperty("companions").GetArrayLength(), Is.EqualTo(1));
            });
            var move = await HttpAsync("move", "{\"x\":4000,\"y\":3000}", $"fixture-{index}",
                auth, resolver, stores[(index + 1) % 4]);
            using var moved = JsonDocument.Parse(move.Body);
            Assert.That(moved.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(), Is.EqualTo(4000));
        }
        var switched = await HttpAsync("config", "{\"configuration\":2}", "fixture-0",
            auth, resolver, stores[2]);
        using (var selected = JsonDocument.Parse(switched.Body))
            Assert.That(selected.RootElement.GetProperty("configuration").GetInt32(), Is.EqualTo(2));
        var configFromAnotherNode = await HttpAsync("state", null, "fixture-0", auth, resolver, stores[0]);
        using (var selected = JsonDocument.Parse(configFromAnotherNode.Body))
            Assert.That(selected.RootElement.GetProperty("pilot").GetProperty("configuration").GetInt32(), Is.EqualTo(2));

        await mesh[3].HeartbeatAsync(Node(4, now));
        var four = await mesh[0].ScaleAsync(4, 8);
        Assert.That(four.Leases.GroupBy(l => l.NodeId).Select(g => g.Count()), Has.All.EqualTo(4));
        Assert.That(four.Leases.Count(l => three.Leases.Single(old => old.MapId == l.MapId).NodeId != l.NodeId),
            Is.GreaterThan(0));
        var portal = WorldMapInstance.Starter(1).Portals.First(p =>
            four.Leases.Single(l => l.MapId == p.DestinationMapId).NodeId !=
            four.Leases.Single(l => l.MapId == 1).NodeId);
        await stores[0].TeleportAsync(100, 1, portal.X, portal.Y, false, default);
        using var meshService = new ServiceCollection().AddSingleton<IServerMeshRegistry>(mesh[0]).BuildServiceProvider();
        var jump = await HttpAsync("jump", $"{{\"portalId\":{portal.Id}}}", "fixture-0",
            auth, resolver, stores[1], meshService);
        using (var arrival = JsonDocument.Parse(jump.Body))
        {
            Assert.That(arrival.RootElement.GetProperty("map").GetProperty("id").GetInt32(),
                Is.EqualTo(portal.DestinationMapId));
            Assert.That(arrival.RootElement.GetProperty("jump").GetProperty("targetNodeId").GetString(),
                Is.EqualTo(four.Leases.Single(l => l.MapId == portal.DestinationMapId).NodeId));
        }
        var toDrain = four.Leases.Single(l => l.MapId == portal.DestinationMapId).NodeId;
        var position = (X: portal.ArrivalX, Y: portal.ArrivalY);
        var drained = await mesh[1].SetDrainingAsync(toDrain);
        Assert.That(drained.Leases, Has.All.Matches<ServerMapLease>(l => l.NodeId != toDrain));
        Assert.That(drained.Leases.Single(l => l.MapId == portal.DestinationMapId).Epoch,
            Is.GreaterThan(four.Leases.Single(l => l.MapId == portal.DestinationMapId).Epoch));
        var stalePilot = stores[1].Find(100)!;
        var staleLease = four.Leases.Single(l => l.MapId == portal.DestinationMapId);
        Assert.ThrowsAsync<WorldLeaseLostException>(async () => await stores[1].MoveAsync(stalePilot,
            position.X + 1000, position.Y, null, default,
            fence: new WorldLeaseFence(mesh[1], staleLease.NodeId, staleLease.Epoch)));
        var after = await HttpAsync("state", null, "fixture-0", auth, resolver, stores[3]);
        using var recovered = JsonDocument.Parse(after.Body);
        Assert.Multiple(() =>
        {
            Assert.That(recovered.RootElement.GetProperty("pilot").GetProperty("shipId").GetString(), Is.EqualTo("ship_pusat-plus"));
            Assert.That(recovered.RootElement.GetProperty("map").GetProperty("id").GetInt32(),
                Is.EqualTo(portal.DestinationMapId));
            Assert.That(recovered.RootElement.GetProperty("pilot").GetProperty("x").GetInt32(), Is.EqualTo(position.X));
            Assert.That(recovered.RootElement.GetProperty("pilot").GetProperty("y").GetInt32(), Is.EqualTo(position.Y));
            Assert.That(recovered.RootElement.GetProperty("pilot").GetProperty("configuration").GetInt32(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task CollectionAndCombat_AreVisibleFromAnotherReplica()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var wallet = new Wallet();
        var rewards = new NpcRewards();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] }), TimeProvider.System);
        using var services = new ServiceCollection().AddSingleton(collectibles).AddSingleton(npcs).BuildServiceProvider();
        var session = new GameAuthSession { Dosid = "fixture-combat", AccountId = Guid.NewGuid(),
            GameUserId = 801, Username = "Combat pilot", Company = "mmo",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var auth = new Sessions(new Dictionary<string, GameAuthSession> { [session.Dosid] = session });
        var resolver = new WorldPilotResolver(auth, new AccountState());
        var a = new WorldSessionStore(cache, mutex);
        var b = new WorldSessionStore(cache, mutex);
        await HttpAsync("enter", null, session.Dosid, auth, resolver, a, services);

        var box = (await collectibles.VisibleAsync(1, default)).First();
        await a.TeleportAsync(801, 1, box.X, box.Y, false, default);
        var collected = await HttpAsync("collect", $"{{\"boxId\":{box.Id}}}", session.Dosid,
            auth, resolver, b, services);
        Assert.That(collected.Status, Is.EqualTo(200));
        var otherState = await HttpAsync("state", null, session.Dosid, auth, resolver, a, services);
        using (var state = JsonDocument.Parse(otherState.Body))
            Assert.That(state.RootElement.GetProperty("boxes").EnumerateArray()
                .Any(item => item.GetProperty("id").GetInt32() == box.Id), Is.False);
        Assert.That(wallet.Credits, Is.EqualTo(1));

        var npc = (await npcs.VisibleAsync(1, default)).Single();
        await a.TeleportAsync(801, 1, npc.X, npc.Y, false, default);
        var target = await HttpAsync("target", $"{{\"npcId\":{npc.Id}}}", session.Dosid,
            auth, resolver, a, services);
        Assert.That(target.Status, Is.EqualTo(200));
        var fired = await HttpAsync("fire", $"{{\"npcId\":{npc.Id}}}", session.Dosid,
            auth, resolver, b, services);
        using (var attack = JsonDocument.Parse(fired.Body))
            Assert.That(attack.RootElement.GetProperty("destroyed").GetBoolean(), Is.True);
        Assert.That((await npcs.VisibleAsync(1, default)), Is.Empty);
        Assert.That(rewards.Credits, Is.EqualTo(1));
    }

    [Test]
    public async Task LeaseTransfer_FencesStaleCollectionAndCombat_ThenNewOwnerCanAct()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        var initial = await registry.HeartbeatAsync(Node(1, now));
        await registry.HeartbeatAsync(Node(2, now));
        var lease = initial.Leases.Single(item => item.MapId == 1);
        var oldFence = new WorldLeaseFence(registry, lease.NodeId, lease.Epoch);
        var wallet = new Wallet();
        var rewards = new NpcRewards();
        var boxes = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] }),
            TimeProvider.System);
        var stale = new WorldSessionStore(cache, mutex);
        var fresh = new WorldSessionStore(cache, mutex);
        var box = (await boxes.VisibleAsync(1, default)).First();
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var pilot = new WorldPilot(901, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = Guid.NewGuid(), X = box.X, Y = box.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        };
        await stale.EnterAsync(pilot, default);
        var reassigned = await registry.SetDrainingAsync(lease.NodeId);
        var nextLease = reassigned.Leases.Single(item => item.MapId == 1);
        Assert.That(nextLease.Epoch, Is.GreaterThan(lease.Epoch));
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await boxes.CollectAsync(pilot, box.Id, stale, default, oldFence));
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await npcs.AttackAsync(pilot, npc.Id, stale, default, oldFence));
        Assert.That(wallet.Credits, Is.Zero);
        Assert.That(rewards.Credits, Is.Zero);
        Assert.That((await boxes.VisibleAsync(1, default)).Any(item => item.Id == box.Id), Is.True);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(npc.MaxHp));

        var newFence = new WorldLeaseFence(registry, nextLease.NodeId, nextLease.Epoch);
        Assert.That((await boxes.CollectAsync(pilot, box.Id, fresh, default, newFence)).Status,
            Is.EqualTo("collected"));
        await fresh.TeleportAsync(pilot.UserId, 1, npc.X, npc.Y, false, default);
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, fresh, default, newFence)).Destroyed,
            Is.True);
        Assert.That(wallet.Credits, Is.EqualTo(1));
        Assert.That(rewards.Credits, Is.EqualTo(1));
    }

    private static async Task<(int Status, string Body)> HttpAsync(string route, string? body, string cookie,
        IGameAuthSessionStore auth, IWorldPilotResolver resolver, WorldSessionStore store,
        IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext();
        if (services is not null) context.RequestServices = services;
        context.Request.Method = route is "state" ? "GET" : "POST";
        context.Request.Headers.Cookie = "dosid=" + cookie;
        context.Request.ContentType = body is null ? null : "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        context.Response.Body = new MemoryStream();
        switch (route)
        {
            case "enter": await WorldHttpBridge.EnterAsync(context, auth, resolver, store, default); break;
            case "move": await WorldHttpBridge.MoveAsync(context, auth, resolver, store, default); break;
            case "config": await WorldHttpBridge.ConfigAsync(context, auth, resolver, store, default); break;
            case "jump": await WorldHttpBridge.JumpAsync(context, auth, resolver, store, default); break;
            case "collect": await WorldHttpBridge.CollectAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldCollectibles>(), default); break;
            case "target": await WorldHttpBridge.TargetAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldNpcs>(), default); break;
            case "fire": await WorldHttpBridge.FireAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldNpcs>(), default); break;
            default: await WorldHttpBridge.StateAsync(context, auth, resolver, store, default); break;
        }
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
