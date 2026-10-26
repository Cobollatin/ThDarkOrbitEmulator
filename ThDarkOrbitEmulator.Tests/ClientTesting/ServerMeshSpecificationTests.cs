using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.AppHost;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-07: scalable server mesh, map leasing, cross-node handoff and Aspire replica bounds.
public class ServerMeshSpecificationTests
{
    private sealed class SharedMutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Releaser(gate);
        }
        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Mesh(ServiceProvider services, ServerMeshCoordinator coordinator, TestClock clock) : IDisposable
    {
        public ServerMeshCoordinator Coordinator { get; } = coordinator;
        public TestClock Clock { get; } = clock;
        public void Dispose() => services.Dispose();
    }

    private static Mesh Create()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new TestClock();
        var options = Options.Create(new ClusterOptions
            { LeaseTtlSeconds = 30, HeartbeatIntervalSeconds = 10, DesiredWorkers = 1, MaxMapsPerNode = 4 });
        return new Mesh(services, new ServerMeshCoordinator(services.GetRequiredService<IDistributedCache>(),
            new SharedMutex(), clock, options), clock);
    }

    private static ServerMeshNode Node(string id, DateTimeOffset now, int port = 61000) => new(id, id,
        $"http://{id}", "127.0.0.1", port, ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0);

    private static IConfiguration Config(string key, string value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    [Test]
    public void GameMapCatalogue_CoversMapIdsOneToSixteen() =>
        Assert.That(ServerMeshCoordinator.GameMapIds, Is.EqualTo(Enumerable.Range(1, 16).ToArray()));

    [Test]
    public async Task EmptyRegistry_ReturnsEmptySnapshotWithoutLeases()
    {
        using var mesh = Create();
        var snapshot = await mesh.Coordinator.GetSnapshotAsync();
        Assert.That(snapshot.Nodes, Is.Empty);
        Assert.That(snapshot.Leases, Is.Empty);
    }

    [Test]
    public async Task SingleNode_RegistersAndOwnsEveryMapAtEpochOne()
    {
        using var mesh = Create();
        var snapshot = await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        Assert.That(snapshot.Nodes.Single().Status, Is.EqualTo(ServerMeshNodeStatus.Active));
        Assert.That(snapshot.Nodes.Single().LeasedMapIds, Is.EqualTo(ServerMeshCoordinator.GameMapIds));
        Assert.That(snapshot.Leases.Select(l => l.Epoch), Has.All.EqualTo(1));
        Assert.That(snapshot.Leases.Select(l => l.ExpiresAtUtc), Has.All.EqualTo(mesh.Clock.Now.AddSeconds(30)));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(8)]
    [TestCase(16)]
    public async Task MultiNodeRegistration_PartitionsMapsWithBalancedUniqueOwners(int count)
    {
        using var mesh = Create();
        ServerMeshSnapshot snapshot = null!;
        for (var i = 0; i < count; i++)
            snapshot = await mesh.Coordinator.HeartbeatAsync(Node($"node-{i:D2}", mesh.Clock.Now, 61000 + i));
        Assert.That(snapshot.Nodes, Has.Length.EqualTo(count));
        Assert.That(snapshot.Leases.Select(l => l.MapId), Is.EqualTo(ServerMeshCoordinator.GameMapIds));
        var loads = snapshot.Nodes.Select(n => n.LeasedMapIds.Length).ToArray();
        Assert.That(loads.Sum(), Is.EqualTo(16));
        Assert.That(loads.Max() - loads.Min(), Is.LessThanOrEqualTo(1));
        Assert.That(snapshot.Nodes.SelectMany(n => n.LeasedMapIds).Distinct().Count(), Is.EqualTo(16));
    }

    [Test]
    public async Task Partitioning_IsDeterministicRegardlessOfRegistrationOrder()
    {
        using var forward = Create();
        using var reverse = Create();
        string[] ids = ["node-a", "node-b", "node-c"];
        foreach (var id in ids) await forward.Coordinator.HeartbeatAsync(Node(id, forward.Clock.Now));
        foreach (var id in ids.Reverse()) await reverse.Coordinator.HeartbeatAsync(Node(id, reverse.Clock.Now));
        var a = (await forward.Coordinator.RebalanceAsync()).Nodes.ToDictionary(n => n.NodeId, n => n.LeasedMapIds.Length);
        var b = (await reverse.Coordinator.RebalanceAsync()).Nodes.ToDictionary(n => n.NodeId, n => n.LeasedMapIds.Length);
        Assert.That(a.Values.Order(), Is.EqualTo(b.Values.Order()));
        Assert.That(a.Values.Sum(), Is.EqualTo(16));
    }

    [Test]
    public async Task RepeatedHeartbeat_KeepsOwnersAndEpochsButRenewsExpiry()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var first = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(10);
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var renewed = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        Assert.That(renewed.Leases.Select(l => (l.MapId, l.NodeId, l.Epoch)),
            Is.EqualTo(first.Leases.Select(l => (l.MapId, l.NodeId, l.Epoch))));
        Assert.That(renewed.Leases.Select(l => l.ExpiresAtUtc), Has.All.EqualTo(mesh.Clock.Now.AddSeconds(30)));
    }

    [Test]
    public async Task StaleNode_IsReportedOfflineOnReadAndEvictedFromLeasesOnNextWrite()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(20);
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(11);

        var read = await mesh.Coordinator.GetSnapshotAsync();
        var stale = read.Nodes.Single(n => n.NodeId == "node-b");
        Assert.That(stale.Status, Is.EqualTo(ServerMeshNodeStatus.Offline));
        Assert.That(stale.LeasedMapIds, Is.Empty);

        var failover = await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        Assert.That(failover.Leases.Select(l => l.NodeId), Has.All.EqualTo("node-a"));
        Assert.That(failover.Nodes.Single(n => n.NodeId == "node-a").LeasedMapIds, Has.Length.EqualTo(16));
    }

    [Test]
    public async Task HeartbeatTtl_IsInclusiveAndReadOnlySnapshotDoesNotPersistOfflineStatus()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var registered = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(30);

        var boundary = await mesh.Coordinator.GetSnapshotAsync();
        Assert.That(boundary.Nodes.Select(n => n.Status), Has.All.EqualTo(ServerMeshNodeStatus.Active));
        Assert.That(boundary.Leases.Select(l => l.NodeId), Is.EqualTo(registered.Leases.Select(l => l.NodeId)));

        mesh.Clock.Now = mesh.Clock.Now.AddTicks(1);
        var staleRead = await mesh.Coordinator.GetSnapshotAsync();
        Assert.That(staleRead.Nodes.Select(n => n.Status), Has.All.EqualTo(ServerMeshNodeStatus.Offline));
        Assert.That(staleRead.Nodes.SelectMany(n => n.LeasedMapIds), Is.Empty);
        Assert.That(staleRead.Leases.Select(l => (l.MapId, l.NodeId, l.Epoch)),
            Is.EqualTo(registered.Leases.Select(l => (l.MapId, l.NodeId, l.Epoch))));

        var revived = await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        Assert.That(revived.Nodes.Single(n => n.NodeId == "node-a").Status, Is.EqualTo(ServerMeshNodeStatus.Active));
        Assert.That(revived.Nodes.Single(n => n.NodeId == "node-b").Status, Is.EqualTo(ServerMeshNodeStatus.Offline));
        Assert.That(revived.Leases.Select(l => l.NodeId), Has.All.EqualTo("node-a"));
        foreach (var lease in revived.Leases)
        {
            var prior = registered.Leases.Single(l => l.MapId == lease.MapId);
            Assert.That(lease.Epoch, Is.EqualTo(prior.Epoch + (prior.NodeId == "node-a" ? 0 : 1)),
                $"map {lease.MapId}");
        }
    }

    [Test]
    public async Task ReturnOfOfflineNode_RebalancesWithoutResettingPerMapEpochs()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var original = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(31);
        var failedOver = await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var rejoined = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));

        Assert.That(rejoined.Nodes.Select(n => n.LeasedMapIds.Length), Is.EqualTo(new[] { 8, 8 }));
        Assert.That(rejoined.Leases.Select(l => l.ExpiresAtUtc), Has.All.EqualTo(mesh.Clock.Now.AddSeconds(30)));
        foreach (var lease in rejoined.Leases)
        {
            var before = failedOver.Leases.Single(l => l.MapId == lease.MapId);
            var initial = original.Leases.Single(l => l.MapId == lease.MapId);
            Assert.That(lease.Epoch, Is.EqualTo(before.Epoch + (lease.NodeId == before.NodeId ? 0 : 1)),
                $"map {lease.MapId}");
            Assert.That(lease.Epoch, Is.GreaterThanOrEqualTo(initial.Epoch), $"map {lease.MapId}");
        }
    }

    [Test]
    public async Task LeaseFailover_IncrementsEpochOnlyForMapsThatMoved()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var before = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(31);
        var after = await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        foreach (var lease in after.Leases)
        {
            var old = before.Leases.Single(l => l.MapId == lease.MapId);
            Assert.That(lease.Epoch, Is.EqualTo(old.NodeId == "node-a" ? old.Epoch : old.Epoch + 1),
                $"map {lease.MapId}");
        }
    }

    [Test]
    public async Task CrossNodeJumpHandoff_RoutesDestinationMapToRemoteOwnerEndpoint()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now, 61001));
        var snapshot = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now, 61002));
        var source = snapshot.Leases.First(l => l.NodeId == "node-a");
        var destination = snapshot.Leases.First(l => l.NodeId == "node-b");
        var owner = snapshot.Nodes.Single(n => n.NodeId == destination.NodeId);
        // The MapChange handoff payload is (mapId, ownerNodeId, "host:port"); the epoch fences the admission.
        Assert.That(owner.Status, Is.EqualTo(ServerMeshNodeStatus.Active));
        Assert.That($"{owner.WorldTcpHost}:{owner.WorldTcpPort}", Is.EqualTo("127.0.0.1:61002"));
        Assert.That(destination.NodeId, Is.Not.EqualTo(source.NodeId));
        Assert.That(destination.ExpiresAtUtc, Is.GreaterThan(mesh.Clock.Now));
    }

    [Test]
    public async Task HandoffToken_EpochChangeInvalidatesPreviouslyAdmittedRoute()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        var before = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        var admitted = before.Leases.First(l => l.NodeId == "node-b");
        await mesh.Coordinator.SetDrainingAsync("node-b");
        var after = (await mesh.Coordinator.GetSnapshotAsync()).Leases.Single(l => l.MapId == admitted.MapId);
        Assert.That(after.NodeId, Is.EqualTo("node-a"));
        Assert.That(after.Epoch, Is.GreaterThan(admitted.Epoch));
    }

    [Test]
    public async Task DrainingNode_ReleasesLeasesAndStaysDrainingAcrossHeartbeats()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        await mesh.Coordinator.SetDrainingAsync("node-b");
        var snapshot = await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        var drained = snapshot.Nodes.Single(n => n.NodeId == "node-b");
        Assert.That(drained.Status, Is.EqualTo(ServerMeshNodeStatus.Draining));
        Assert.That(drained.LeasedMapIds, Is.Empty);
        Assert.That(snapshot.Leases.Select(l => l.NodeId), Has.All.EqualTo("node-a"));
    }

    [Test]
    public async Task ReactivatedNode_RejoinsPartitionAfterRebalance()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        await mesh.Coordinator.HeartbeatAsync(Node("node-b", mesh.Clock.Now));
        await mesh.Coordinator.SetDrainingAsync("node-b");
        var snapshot = await mesh.Coordinator.SetActiveAsync("node-b");
        Assert.That(snapshot.Nodes.Select(n => n.LeasedMapIds.Length), Is.EqualTo(new[] { 8, 8 }));
    }

    [Test]
    public async Task UnknownNodeStatusChanges_AreNoOps()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        await mesh.Coordinator.SetDrainingAsync("ghost");
        var snapshot = await mesh.Coordinator.SetActiveAsync("ghost");
        Assert.That(snapshot.Nodes.Select(n => n.NodeId), Is.EqualTo(new[] { "node-a" }));
        Assert.That(snapshot.Nodes.Single().LeasedMapIds, Has.Length.EqualTo(16));
    }

    [Test]
    public async Task AllNodesStale_ExpiresLeasesInsteadOfAssigningOwners()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        mesh.Clock.Now = mesh.Clock.Now.AddSeconds(31);
        var snapshot = await mesh.Coordinator.RebalanceAsync();
        Assert.That(snapshot.Leases.Select(l => l.ExpiresAtUtc), Has.All.EqualTo(mesh.Clock.Now));
        Assert.That(snapshot.Nodes.Single().LeasedMapIds, Is.Empty);
    }

    [TestCase(1, 0)]
    [TestCase(4, 3)]
    [TestCase(16, 15)]
    public async Task Scale_SimulatesMissingReplicasOnTheRealHostListener(int desired, int simulated)
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now, 61010));
        var snapshot = await mesh.Coordinator.ScaleAsync(desired, 4);
        Assert.That(snapshot.DesiredReplicas, Is.EqualTo(desired));
        var sims = snapshot.Nodes.Where(n => n.Simulated).ToArray();
        Assert.That(sims, Has.Length.EqualTo(simulated));
        Assert.That(sims.Select(n => n.WorldTcpPort), Has.All.EqualTo(61010));
        Assert.That(snapshot.Nodes.Sum(n => n.LeasedMapIds.Length), Is.EqualTo(16));
    }

    [Test]
    public async Task ScaleDown_RemovesSimulatedReplicasAndReturnsMapsToRealNode()
    {
        using var mesh = Create();
        await mesh.Coordinator.HeartbeatAsync(Node("node-a", mesh.Clock.Now));
        await mesh.Coordinator.ScaleAsync(8, 4);
        var snapshot = await mesh.Coordinator.ScaleAsync(1, 4);
        Assert.That(snapshot.Nodes.Select(n => n.NodeId), Is.EqualTo(new[] { "node-a" }));
        Assert.That(snapshot.Nodes.Single().LeasedMapIds, Has.Length.EqualTo(16));
    }

    [TestCase(0, 4)]
    [TestCase(-1, 4)]
    [TestCase(17, 4)]
    [TestCase(4, 0)]
    [TestCase(4, 257)]
    public void Scale_RejectsOutOfRangeReplicasOrMapsPerNode(int desired, int maxMaps)
    {
        using var mesh = Create();
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mesh.Coordinator.ScaleAsync(desired, maxMaps));
    }

    [TestCase("1", 1)]
    [TestCase("2", 2)]
    [TestCase("8", 8)]
    [TestCase("16", 16)]
    public void AspireMeshScalingOptions_AcceptsReplicasOneToSixteen(string raw, int expected) =>
        Assert.That(MeshScalingOptions.DesiredReplicas(Config("Emulator:Mesh:ApiReplicas", raw)), Is.EqualTo(expected));

    [TestCase("0")]
    [TestCase("-3")]
    [TestCase("17")]
    [TestCase("64")]
    [TestCase("")]
    [TestCase("two")]
    public void AspireMeshScalingOptions_RejectsReplicasOutsideOneToSixteen(string raw) =>
        Assert.Throws<InvalidOperationException>(() =>
            MeshScalingOptions.DesiredReplicas(Config("Emulator:Mesh:ApiReplicas", raw)));

    [Test]
    public void AspireMeshScalingOptions_DefaultsToOneAndPrefersEmulatorKeyOverCluster()
    {
        Assert.That(MeshScalingOptions.DesiredReplicas(new ConfigurationBuilder().Build()), Is.EqualTo(1));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Emulator:Mesh:ApiReplicas"] = "5",
            ["Cluster:DesiredWorkers"] = "9",
        }).Build();
        Assert.That(MeshScalingOptions.DesiredReplicas(configuration), Is.EqualTo(5));
    }

    [TestCase(1, true)]
    [TestCase(16, true)]
    [TestCase(0, false)]
    [TestCase(17, false)]
    public void ClusterOptions_EnforcesSameReplicaBoundsAsAppHost(int workers, bool valid)
    {
        var options = new ClusterOptions { DesiredWorkers = workers };
        if (valid) Assert.DoesNotThrow(options.Validate);
        else Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [TestCase(1, true)]
    [TestCase(256, true)]
    [TestCase(0, false)]
    [TestCase(257, false)]
    public void ClusterOptions_EnforcesInclusiveMapCapacityBounds(int capacity, bool valid)
    {
        var options = new ClusterOptions { MaxMapsPerNode = capacity };
        if (valid) Assert.DoesNotThrow(options.Validate);
        else Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [TestCase(1, 3, true)]
    [TestCase(1, 2, false)]
    [TestCase(10, 21, true)]
    [TestCase(10, 20, false)]
    [TestCase(300, 601, true)]
    [TestCase(300, 600, false)]
    [TestCase(0, 30, false)]
    [TestCase(301, 603, false)]
    [TestCase(10, 3600, true)]
    [TestCase(10, 3601, false)]
    public void ClusterOptions_RequiresTtlStrictlyAboveTwiceHeartbeatWithinLimits(
        int heartbeatSeconds, int ttlSeconds, bool valid)
    {
        var options = new ClusterOptions
        {
            HeartbeatIntervalSeconds = heartbeatSeconds,
            LeaseTtlSeconds = ttlSeconds
        };
        if (valid) Assert.DoesNotThrow(options.Validate);
        else Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Test]
    public void ClusterOptions_NodeIdAllowsNullEmptyWhitespaceAndLength128()
    {
        foreach (var id in new string?[] { null, "", " ", new string('x', 128) })
            Assert.DoesNotThrow(() => new ClusterOptions { NodeId = id }.Validate(), id ?? "null");
    }

    [Test]
    public void ClusterOptions_NodeIdRejectsLength129AndControlCharacters()
    {
        foreach (var id in new[] { new string('x', 129), "node\nnext", "node\t01", "node\0suffix" })
            Assert.Throws<InvalidOperationException>(() => new ClusterOptions { NodeId = id }.Validate());
    }

    [Test]
    public void ClusterOptions_UsesDocumentedHeartbeatAndLeaseDefaults()
    {
        var options = new ClusterOptions();
        Assert.Multiple(() =>
        {
            Assert.That(options.DesiredWorkers, Is.EqualTo(1));
            Assert.That(options.MaxMapsPerNode, Is.EqualTo(4));
            Assert.That(options.HeartbeatIntervalSeconds, Is.EqualTo(10));
            Assert.That(options.LeaseTtlSeconds, Is.EqualTo(30));
            Assert.That(options.NodeId, Is.EqualTo($"{Environment.MachineName}-{Environment.ProcessId}"));
            Assert.DoesNotThrow(options.Validate);
        });
    }
}
