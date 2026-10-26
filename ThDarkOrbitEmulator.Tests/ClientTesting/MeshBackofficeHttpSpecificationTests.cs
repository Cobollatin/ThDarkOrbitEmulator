using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

/// <summary>
/// SPEC-07 §9: request validation, precondition and no-store contract of the backoffice mesh and world HTTP slices.
/// </summary>
public class MeshBackofficeHttpSpecificationTests
{
    [TestCase(1, 1)]
    [TestCase(16, 256)]
    [TestCase(3, 8)]
    public async Task Scale_InclusiveBounds_ForwardsToRegistryWithNoStore(int replicas, int maxMaps)
    {
        var registry = new FakeRegistry();

        var context = await ScaleAsync(registry, new MeshScaleRequest(replicas, maxMaps));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { $"scale:{replicas}:{maxMaps}" }));
        });
    }

    [TestCase(0, 8)]
    [TestCase(17, 8)]
    [TestCase(-1, 8)]
    [TestCase(3, 0)]
    [TestCase(3, 257)]
    [TestCase(int.MinValue, int.MaxValue)]
    public async Task Scale_OutOfBounds_Returns400WithoutRegistryCallOrNoStore(int replicas, int maxMaps)
    {
        var registry = new FakeRegistry();

        var context = await ScaleAsync(registry, new MeshScaleRequest(replicas, maxMaps));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(ErrorOf(context), Is.EqualTo("DesiredReplicas must be 1..16 and MaxMapsPerNode 1..256."));
            Assert.That(registry.Calls, Is.Empty);
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task Drain_UnknownNode_Returns404WithoutMutation()
    {
        var registry = new FakeRegistry(Node("a"), Node("b"));

        var context = await DrainAsync(registry, "missing");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
        });
    }

    [Test]
    public async Task Drain_NodeIdMatchIsCaseSensitive()
    {
        var registry = new FakeRegistry(Node("node-a"), Node("node-b"));

        var context = await DrainAsync(registry, "NODE-A");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [TestCase(ServerMeshNodeStatus.Draining)]
    [TestCase(ServerMeshNodeStatus.Offline)]
    public async Task Drain_NonActiveNode_Returns409(ServerMeshNodeStatus status)
    {
        var registry = new FakeRegistry(Node("a", status), Node("b"), Node("c"));

        var context = await DrainAsync(registry, "a");

        AssertDrainConflict(context, registry);
    }

    [Test]
    public async Task Drain_LastActiveNode_Returns409EvenWhenOtherNodesAreDrainingOrOffline()
    {
        var registry = new FakeRegistry(Node("a"), Node("b", ServerMeshNodeStatus.Draining),
            Node("c", ServerMeshNodeStatus.Offline));

        var context = await DrainAsync(registry, "a");

        AssertDrainConflict(context, registry);
    }

    [Test]
    public async Task Drain_RealNodeWithConnectedPilots_Returns409()
    {
        var registry = new FakeRegistry(Node("a", pilots: 1), Node("b"));

        var context = await DrainAsync(registry, "a");

        AssertDrainConflict(context, registry);
    }

    [Test]
    public async Task Drain_SimulatedNodeWithPilots_IsAllowed()
    {
        var registry = new FakeRegistry(Node("sim", pilots: 5, simulated: true), Node("b"));

        var context = await DrainAsync(registry, "sim");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot", "drain:sim" }));
        });
    }

    [Test]
    public async Task Drain_IdleActiveNodeWithHealthyPeer_DrainsExactlyThatNode()
    {
        var registry = new FakeRegistry(Node("a"), Node("b"));

        var context = await DrainAsync(registry, "a");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot", "drain:a" }));
        });
    }

    [Test]
    public async Task Activate_UnknownNode_Returns404WithoutMutation()
    {
        var registry = new FakeRegistry(Node("a", ServerMeshNodeStatus.Draining));

        var context = await ActivateAsync(registry, "missing");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
        });
    }

    [TestCase(ServerMeshNodeStatus.Active)]
    [TestCase(ServerMeshNodeStatus.Offline)]
    public async Task Activate_NonDrainingNode_Returns409WithoutMutation(ServerMeshNodeStatus status)
    {
        var registry = new FakeRegistry(Node("a", status));

        var context = await ActivateAsync(registry, "a");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(ErrorOf(context), Is.EqualTo("Only a live draining node can be activated."));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
        });
    }

    [Test]
    public async Task Activate_DrainingNode_ReactivatesItWithNoStore()
    {
        var registry = new FakeRegistry(Node("a", ServerMeshNodeStatus.Draining, pilots: 3));

        var context = await ActivateAsync(registry, "a");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot", "activate:a" }));
        });
    }

    [Test]
    public async Task Rebalance_AlwaysDelegatesWithoutPreconditions()
    {
        var registry = new FakeRegistry();
        var context = NewContext();

        await Factory.Create<MeshRebalanceEndpoint>(context, registry).HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "rebalance" }));
        });
    }

    [Test]
    public async Task Status_ReturnsRegistrySnapshotWithNoStore()
    {
        var registry = new FakeRegistry(Node("a", ServerMeshNodeStatus.Draining));
        var context = NewContext();

        await Factory.Create<ServerMeshStatusEndpoint>(context, registry).HandleAsync(CancellationToken.None);

        context.Response.Body.Position = 0;
        var snapshot = await JsonSerializer.DeserializeAsync<ServerMeshSnapshot>(context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
            Assert.That(snapshot!.Nodes.Single().Status, Is.EqualTo(ServerMeshNodeStatus.Draining));
            Assert.That(raw, Does.Contain("\"Draining\""), "node status is serialised by name");
        });
    }

    [Test]
    public async Task Topology_ProjectsSnapshotAndCountsDistinctPilotsAcrossMaps()
    {
        var registry = new FakeRegistry(Node("active"), Node("draining", ServerMeshNodeStatus.Draining),
            Node("offline", ServerMeshNodeStatus.Offline))
        {
            Leases = [new ServerMapLease(1, "active", 7, DateTimeOffset.UtcNow.AddMinutes(1))]
        };
        var (sessions, cache) = NewSessions();
        await sessions.EnterAsync(Pilot(11, 1), default);
        await sessions.EnterAsync(Pilot(22, 2), default);
        await SetPresenceAsync(cache, 2, Pilot(11, 2));
        var context = NewContext();

        await Factory.Create<MeshTopologyEndpoint>(context, registry, sessions).HandleAsync(default);
        var response = await ReadAsync<MeshTopologyResponse>(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
            Assert.That((response.DesiredWorkers, response.DesiredReplicas, response.MaxReplicas,
                response.MaxMapsPerNode, response.ActiveNodeCount, response.TotalOnlinePilots),
                Is.EqualTo((2, 2, 16, 8, 1, 2)));
            Assert.That(response.MapLeaseDistribution, Is.EqualTo(registry.Leases));
            Assert.That(response.Nodes.Select(n => n.NodeId), Is.EqualTo(new[] { "active", "draining", "offline" }));
            Assert.That(response.AspireDashboardLink, Is.EqualTo("https://localhost:17081"));
        });
    }

    [Test]
    public async Task WorldOverview_UsesFirstLeaseAndCountsDeduplicatedPilotsOnTheirFirstMap()
    {
        var registry = new FakeRegistry(Node("first"), Node("second"))
        {
            Leases = [new ServerMapLease(1, "first", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
                new ServerMapLease(1, "second", 2, DateTimeOffset.UtcNow.AddMinutes(1)),
                new ServerMapLease(2, "second", 3, DateTimeOffset.UtcNow.AddMinutes(-1))]
        };
        var (sessions, cache) = NewSessions();
        await sessions.EnterAsync(Pilot(11, 1), default);
        await sessions.EnterAsync(Pilot(22, 2), default);
        await SetPresenceAsync(cache, 2, Pilot(11, 2));
        var context = NewContext();

        await Factory.Create<WorldOverviewEndpoint>(context, registry, sessions).HandleAsync(default);
        var response = await ReadAsync<WorldOverviewResponse>(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
            Assert.That(response.Maps.Select(m => m.MapId), Is.EqualTo(ServerMeshCoordinator.GameMapIds));
            Assert.That((response.Maps[0].OwnerNodeId, response.Maps[0].OnlinePilots),
                Is.EqualTo(("first", 1)));
            Assert.That((response.Maps[1].OwnerNodeId, response.Maps[1].OnlinePilots),
                Is.EqualTo(("second", 1)), "overview does not filter an expired lease");
            Assert.That(response.Maps.Skip(2).All(m => m.OwnerNodeId is null && m.OnlinePilots == 0), Is.True);
            Assert.That(response.Pilots.Select(p => p.UserId), Is.EqualTo(new long[] { 11, 22 }));
        });
    }

    [Test]
    public async Task ReadPilots_ExcludesExpiredPresenceAndKeepsFirstDuplicate()
    {
        var (sessions, cache) = NewSessions();
        await SetPresenceAsync(cache, 1, Pilot(11, 1));
        await SetPresenceAsync(cache, 2, Pilot(11, 2));
        await SetPresenceAsync(cache, 3, Pilot(33, 3), DateTimeOffset.UtcNow.AddMinutes(-2));

        var pilots = await MeshWorldOverview.ReadPilotsAsync(sessions, default);

        Assert.That(pilots.Select(p => (p.UserId, p.MapId)), Is.EqualTo(new[] { (11L, 1) }));
    }

    [TestCase(0L, 1, 0, 0)]
    [TestCase(-5L, 1, 0, 0)]
    [TestCase(1L, 0, 0, 0)]
    [TestCase(1L, 17, 0, 0)]
    [TestCase(1L, 1, -1, 0)]
    [TestCase(1L, 1, 21001, 0)]
    [TestCase(1L, 1, 0, -1)]
    [TestCase(1L, 1, 0, 13101)]
    public async Task Teleport_InvalidPilotMapOrCoordinates_Returns400(long userId, int mapId, int x, int y)
    {
        var context = await TeleportAsync(userId, new WorldTeleportRequest(mapId, x, y, false));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(ErrorOf(context), Is.EqualTo("Invalid pilot, map or coordinates."));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.Empty);
        });
    }

    [TestCase(1, 0, 0)]
    [TestCase(16, 21000, 13100)]
    public async Task Teleport_InclusiveBoundsForUnknownPilot_Returns404(int mapId, int x, int y)
    {
        var context = await TeleportAsync(42, new WorldTeleportRequest(mapId, x, y, true));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.Empty);
        });
    }

    private static void AssertDrainConflict(HttpContext context, FakeRegistry registry) =>
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(ErrorOf(context),
                Is.EqualTo("Node cannot drain while pilots are connected or no healthy target exists."));
            Assert.That(registry.Calls, Is.EqualTo(new[] { "snapshot" }));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.Empty);
        });

    private static async Task<HttpContext> ScaleAsync(FakeRegistry registry, MeshScaleRequest request)
    {
        var context = NewContext();
        await Factory.Create<MeshScaleEndpoint>(context, new LocalWorkerSpawner(registry))
            .HandleAsync(request, CancellationToken.None);
        return context;
    }

    private static async Task<HttpContext> DrainAsync(FakeRegistry registry, string nodeId)
    {
        var context = NewContext();
        context.Request.RouteValues["nodeId"] = nodeId;
        await Factory.Create<MeshDrainEndpoint>(context, registry).HandleAsync(CancellationToken.None);
        return context;
    }

    private static async Task<HttpContext> ActivateAsync(FakeRegistry registry, string nodeId)
    {
        var context = NewContext();
        context.Request.RouteValues["nodeId"] = nodeId;
        await Factory.Create<MeshActivateEndpoint>(context, registry).HandleAsync(CancellationToken.None);
        return context;
    }

    private static async Task<HttpContext> TeleportAsync(long userId, WorldTeleportRequest request)
    {
        var context = NewContext();
        context.Request.RouteValues["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sessions = new WorldSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        await Factory.Create<WorldTeleportEndpoint>(context, sessions).HandleAsync(request, CancellationToken.None);
        return context;
    }

    private static string? ErrorOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(context.Response.Body);
        return body.RootElement.GetProperty("error").GetString();
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static (WorldSessionStore Sessions, IDistributedCache Cache) NewSessions()
    {
        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return (new WorldSessionStore(cache), cache);
    }

    private static WorldPilot Pilot(long userId, int mapId) =>
        new(userId, "mmo", mapId, 1, "ship", 0, 0);

    private static async Task SetPresenceAsync(IDistributedCache cache, int mapId, WorldPilot pilot,
        DateTimeOffset? seenAt = null)
    {
        var key = $"world:presence:map:{mapId}";
        var existing = await cache.GetStringAsync(key);
        var presence = existing is null ? new Dictionary<long, WorldPresence>()
            : JsonSerializer.Deserialize<Dictionary<long, WorldPresence>>(existing)!;
        presence[pilot.UserId] = new(pilot, seenAt ?? DateTimeOffset.UtcNow);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(presence));
    }

    private static async Task<T> ReadAsync<T>(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<T>(context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static ServerMeshNode Node(string id, ServerMeshNodeStatus status = ServerMeshNodeStatus.Active,
        int pilots = 0, bool simulated = false) =>
        new(id, id, "http://localhost", "127.0.0.1", 8080, status, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, pilots, [], 0, 0, simulated);

    private sealed class FakeRegistry(params ServerMeshNode[] nodes) : IServerMeshRegistry
    {
        public List<string> Calls { get; } = [];

        public ServerMapLease[] Leases { get; init; } = [];

        private ServerMeshSnapshot Snapshot => new(nodes, Leases, 2, 8);

        public Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default) =>
            Record("heartbeat");

        public Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default) =>
            Record($"drain:{nodeId}");

        public Task<ServerMeshSnapshot> SetActiveAsync(string nodeId, CancellationToken ct = default) =>
            Record($"activate:{nodeId}");

        public Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default) => Record("snapshot");

        public Task<ServerMeshSnapshot> ScaleAsync(int desiredReplicas, int maxMapsPerNode, CancellationToken ct = default) =>
            Record($"scale:{desiredReplicas}:{maxMapsPerNode}");

        public Task<ServerMeshSnapshot> RebalanceAsync(CancellationToken ct = default) => Record("rebalance");

        private Task<ServerMeshSnapshot> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(Snapshot);
        }
    }
}
