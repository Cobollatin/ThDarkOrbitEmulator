using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Operations;

public class ServerMeshCoordinatorTests
{
    [Test]
    public void NodeStatus_UsesNamesInCmsJsonAndReadsLegacyNumericState()
    {
        Assert.That(JsonSerializer.Serialize(ServerMeshNodeStatus.Draining), Is.EqualTo("\"Draining\""));
        Assert.That(JsonSerializer.Deserialize<ServerMeshNodeStatus>("1"), Is.EqualTo(ServerMeshNodeStatus.Draining));
    }
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
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ServerMeshNode Node(string id, DateTimeOffset now) => new(id, id, $"http://{id}",
        "127.0.0.1", 61000, ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0);

    private static (ServerMeshCoordinator[] Nodes, TestClock Clock, ServiceProvider Services) Create(int count)
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new SharedMutex();
        var clock = new TestClock();
        var options = Options.Create(new ClusterOptions { LeaseTtlSeconds = 30, HeartbeatIntervalSeconds = 10 });
        return (Enumerable.Range(0, count).Select(_ => new ServerMeshCoordinator(cache, mutex, clock, options)).ToArray(), clock, services);
    }

    [TestCase(2)]
    [TestCase(4)]
    public async Task ConcurrentNodes_DistributeAllMapsWithoutDuplicateOwners(int count)
    {
        var (coordinators, clock, services) = Create(count);
        using (services)
        {
            await Task.WhenAll(coordinators.Select((c, i) => c.HeartbeatAsync(Node($"node-{i}", clock.Now))));
            var snapshot = await coordinators[0].GetSnapshotAsync();
            Assert.That(snapshot.Leases.Select(l => l.MapId), Is.EquivalentTo(ServerMeshCoordinator.GameMapIds));
            Assert.That(snapshot.Leases.GroupBy(l => l.NodeId).Select(g => g.Count()),
                Has.All.EqualTo(ServerMeshCoordinator.GameMapIds.Length / count));
            Assert.That(snapshot.Nodes.Sum(n => n.LeasedMapIds.Length), Is.EqualTo(16));
        }
    }

    [Test]
    public async Task JoiningNode_RebalancesAndIncrementsMovedLeaseEpochs()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            var original = await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            var balanced = await nodes[1].HeartbeatAsync(Node("b", clock.Now));
            Assert.That(balanced.Nodes.Single(n => n.NodeId == "b").LeasedMapIds, Has.Length.EqualTo(8));
            foreach (var lease in balanced.Leases)
                Assert.That(lease.Epoch, Is.EqualTo(lease.NodeId == "a" ? 1 : 2));
            Assert.That(original.Leases, Has.Length.EqualTo(16));
        }
    }

    [Test]
    public async Task ExpiredHeartbeat_FailsOverAndOldNodeCannotOwnMaps()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            await nodes[1].HeartbeatAsync(Node("b", clock.Now));
            var before = await nodes[0].GetSnapshotAsync();
            clock.Now += TimeSpan.FromSeconds(31);
            var after = await nodes[1].HeartbeatAsync(Node("b", clock.Now));
            Assert.That(after.Nodes.Single(n => n.NodeId == "a").Status, Is.EqualTo(ServerMeshNodeStatus.Offline));
            Assert.That(after.Leases, Has.All.Matches<ServerMapLease>(l => l.NodeId == "b"));
            foreach (var old in before.Leases.Where(l => l.NodeId == "a"))
                Assert.That(after.Leases.Single(l => l.MapId == old.MapId).Epoch, Is.GreaterThan(old.Epoch));
        }
    }

    [Test]
    public async Task DrainingNode_ReleasesAllMapsImmediately()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            await nodes[1].HeartbeatAsync(Node("b", clock.Now));
            var snapshot = await nodes[0].SetDrainingAsync("a");
            Assert.That(snapshot.Leases, Has.All.Matches<ServerMapLease>(l => l.NodeId == "b"));
            Assert.That(snapshot.Nodes.Single(n => n.NodeId == "a").LeasedMapIds, Is.Empty);
        }
    }

    [Test]
    public async Task CompleteOutage_PreservesEpochBeforeNewNodeAcquiresMaps()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            var before = await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            clock.Now += TimeSpan.FromSeconds(31);
            var expired = await nodes[1].GetSnapshotAsync();
            Assert.That(expired.Nodes.Single().Status, Is.EqualTo(ServerMeshNodeStatus.Offline));
            var recovered = await nodes[1].HeartbeatAsync(Node("b", clock.Now));
            Assert.That(recovered.Leases, Has.All.Matches<ServerMapLease>(l => l.NodeId == "b"));
            foreach (var old in before.Leases)
                Assert.That(recovered.Leases.Single(l => l.MapId == old.MapId).Epoch, Is.GreaterThan(old.Epoch));
        }
    }

    [Test]
    public async Task Scale_PersistsDesiredStateAndRegistersExplicitSimulations()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            var scaled = await nodes[0].ScaleAsync(4, 5);
            Assert.That(scaled.DesiredReplicas, Is.EqualTo(4));
            Assert.That(scaled.MaxMapsPerNode, Is.EqualTo(5));
            Assert.That(scaled.Nodes.Count(n => n.Simulated), Is.EqualTo(3));
            Assert.That(scaled.Leases.Select(l => l.NodeId).Distinct().ToArray(), Has.Length.EqualTo(4));
            clock.Now += TimeSpan.FromSeconds(11);
            await nodes[1].HeartbeatAsync(Node("a", clock.Now));
            var reloaded = await nodes[1].GetSnapshotAsync();
            Assert.That(reloaded.Nodes.Count(n => n.Status == ServerMeshNodeStatus.Active), Is.EqualTo(4));
            Assert.That(reloaded.DesiredReplicas, Is.EqualTo(4));
            Assert.That((await nodes[1].RebalanceAsync()).Leases, Has.Length.EqualTo(16));
        }
    }

    [Test]
    public async Task DrainedNode_HeartbeatDoesNotReactivateIt()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            await nodes[0].HeartbeatAsync(Node("b", clock.Now));
            await nodes[0].SetDrainingAsync("a");
            var after = await nodes[1].HeartbeatAsync(Node("a", clock.Now));
            Assert.That(after.Nodes.Single(n => n.NodeId == "a").Status, Is.EqualTo(ServerMeshNodeStatus.Draining));
            Assert.That(after.Leases, Has.All.Matches<ServerMapLease>(l => l.NodeId == "b"));
        }
    }

    [Test]
    public async Task ActivatingLiveDrainedNode_RestoresLeaseOwnership()
    {
        var (nodes, clock, services) = Create(2);
        using (services)
        {
            await nodes[0].HeartbeatAsync(Node("a", clock.Now));
            await nodes[0].HeartbeatAsync(Node("b", clock.Now));
            await nodes[0].SetDrainingAsync("a");
            var activated = await nodes[1].SetActiveAsync("a");
            Assert.That(activated.Nodes.Single(n => n.NodeId == "a").Status, Is.EqualTo(ServerMeshNodeStatus.Active));
            Assert.That(activated.Leases.Count(l => l.NodeId == "a"), Is.EqualTo(8));
        }
    }
}
