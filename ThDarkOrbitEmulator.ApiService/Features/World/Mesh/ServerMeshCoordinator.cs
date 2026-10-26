using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerMeshNodeStatus { Active, Draining, Offline }

public sealed record ServerMeshNode(string NodeId, string InstanceName, string HttpEndpoint,
    string WorldTcpHost, int WorldTcpPort, ServerMeshNodeStatus Status,
    DateTimeOffset StartedAtUtc, DateTimeOffset LastHeartbeatUtc, int ActivePilots,
    int[] LeasedMapIds, double ProcessCpuPercent, long WorkingSetBytes, bool Simulated = false);

public sealed record ServerMapLease(int MapId, string NodeId, long Epoch, DateTimeOffset ExpiresAtUtc);

public sealed record ServerMeshSnapshot(ServerMeshNode[] Nodes, ServerMapLease[] Leases,
    int DesiredReplicas = 0, int MaxMapsPerNode = 0);

public interface IServerMeshRegistry
{
    Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default);
    Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default);
    Task<ServerMeshSnapshot> SetActiveAsync(string nodeId, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<ServerMeshSnapshot> ScaleAsync(int desiredReplicas, int maxMapsPerNode, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task<ServerMeshSnapshot> RebalanceAsync(CancellationToken ct = default) => GetSnapshotAsync(ct);
}

public interface IServerMeshMutex
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
}

// Redis SET NX makes the read/modify/write of the cache snapshot exclusive across API replicas.
public sealed class RedisServerMeshMutex(IConnectionMultiplexer redis) : IServerMeshMutex
{
    private const string Key = "mesh:v1:coordinator-lock";
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var token = Guid.NewGuid().ToString("N");
        while (!await db.StringSetAsync(Key, token, TimeSpan.FromSeconds(30), When.NotExists))
            await Task.Delay(25, ct);
        return new Handle(db, token);
    }

    private sealed class Handle(IDatabase db, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await db.ScriptEvaluateAsync(
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end",
            [Key], [token]);
    }
}

public sealed class ServerMeshCoordinator(
    IDistributedCache cache, IServerMeshMutex mutex, TimeProvider clock, IOptions<ClusterOptions> options,
    IRuntimeVariableRegistry? variables = null)
    : IServerMeshRegistry
{
    private const string StateKey = "mesh:v1:registry";
    // Local game catalogue currently covers the four faction blocks 1..16.
    public static readonly int[] GameMapIds = Enumerable.Range(1, 16).ToArray();
    private async Task<TimeSpan> TtlAsync(CancellationToken ct) => TimeSpan.FromSeconds(
        variables is null ? options.Value.LeaseTtlSeconds
            : await variables.NumberAsync("mesh.heartbeat_ttl_seconds", ct));

    public Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default) =>
        UpdateAsync((nodes, now) =>
        {
            var draining = nodes.TryGetValue(node.NodeId, out var old)
                && old.Status == ServerMeshNodeStatus.Draining;
            nodes[node.NodeId] = node with { Status = draining ? ServerMeshNodeStatus.Draining : ServerMeshNodeStatus.Active,
                LastHeartbeatUtc = now, LeasedMapIds = [] };
        }, ct);

    public Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default) =>
        UpdateAsync((nodes, _) =>
        {
            if (nodes.TryGetValue(nodeId, out var node))
                nodes[nodeId] = node with { Status = ServerMeshNodeStatus.Draining };
        }, ct);

    public Task<ServerMeshSnapshot> SetActiveAsync(string nodeId, CancellationToken ct = default) =>
        UpdateAsync((nodes, _) =>
        {
            if (nodes.TryGetValue(nodeId, out var node) && node.Status == ServerMeshNodeStatus.Draining)
                nodes[nodeId] = node with { Status = ServerMeshNodeStatus.Active };
        }, ct);

    public Task<ServerMeshSnapshot> RebalanceAsync(CancellationToken ct = default) =>
        UpdateAsync((_, _) => { }, ct);

    public Task<ServerMeshSnapshot> ScaleAsync(int desiredReplicas, int maxMapsPerNode,
        CancellationToken ct = default)
    {
        if (desiredReplicas is < 1 or > 16 || maxMapsPerNode is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(desiredReplicas));
        return UpdateAsync((nodes, now) =>
        {
            foreach (var id in nodes.Where(n => n.Value.Simulated).Select(n => n.Key).ToArray())
                nodes.Remove(id);
            var real = nodes.Values.Where(n => !n.Simulated && n.Status == ServerMeshNodeStatus.Active).ToArray();
            if (real.Length == 0) return;
            // Development replicas share the real node's listener. They are visible as simulations,
            // not additional processes or independent failure domains.
            var host = real[0];
            for (var i = 1; i <= Math.Max(0, desiredReplicas - real.Length); i++)
            {
                var id = $"local-sim-{i}";
                nodes[id] = host with { NodeId = id, InstanceName = "api-game (simulated)",
                    Simulated = true, StartedAtUtc = now, LastHeartbeatUtc = now,
                    ActivePilots = 0, LeasedMapIds = [], ProcessCpuPercent = 0, WorkingSetBytes = 0 };
            }
        }, ct, desiredReplicas, maxMapsPerNode);
    }

    public async Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var ttl = await TtlAsync(ct);
        var bytes = await cache.GetAsync(StateKey, ct);
        if (bytes is null) return variables is null ? new([], []) : new([], [],
            (int)await variables.NumberAsync("mesh.desired_replicas", ct),
            (int)await variables.NumberAsync("mesh.max_maps_per_node", ct));
        var snapshot = JsonSerializer.Deserialize<ServerMeshSnapshot>(bytes)!;
        var now = clock.GetUtcNow();
        return snapshot with { DesiredReplicas = variables is null
                ? (snapshot.DesiredReplicas is >= 1 and <= 16 ? snapshot.DesiredReplicas : options.Value.DesiredWorkers)
                : (int)await variables.NumberAsync("mesh.desired_replicas", ct),
            MaxMapsPerNode = variables is null
                ? (snapshot.MaxMapsPerNode > 0 ? snapshot.MaxMapsPerNode : options.Value.MaxMapsPerNode)
                : (int)await variables.NumberAsync("mesh.max_maps_per_node", ct),
            Nodes = snapshot.Nodes.Select(n =>
            now - n.LastHeartbeatUtc > ttl
                ? n with { Status = ServerMeshNodeStatus.Offline, LeasedMapIds = [] }
                : n).ToArray() };
    }

    private async Task<ServerMeshSnapshot> UpdateAsync(Action<Dictionary<string, ServerMeshNode>, DateTimeOffset> change,
        CancellationToken ct, int? desiredReplicas = null, int? maxMapsPerNode = null)
    {
        await using var held = await mutex.AcquireAsync(ct);
        var now = clock.GetUtcNow();
        var previous = await GetSnapshotAsync(ct);
        var ttl = await TtlAsync(ct);
        var nodes = previous.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        foreach (var (id, node) in nodes.ToArray())
            if (now - node.LastHeartbeatUtc > ttl)
                nodes[id] = node with { Status = ServerMeshNodeStatus.Offline, LeasedMapIds = [] };
        change(nodes, now);
        foreach (var (id, node) in nodes.Where(n => n.Value.Simulated).ToArray())
            if (node.Status == ServerMeshNodeStatus.Active)
                nodes[id] = node with { LastHeartbeatUtc = now };
        var active = nodes.Values.Where(n => n.Status == ServerMeshNodeStatus.Active
            && now - n.LastHeartbeatUtc <= ttl).OrderBy(n => n.NodeId, StringComparer.Ordinal).ToArray();
        var leases = previous.Leases.ToDictionary(l => l.MapId);
        var counts = active.ToDictionary(n => n.NodeId, _ => 0, StringComparer.Ordinal);
        foreach (var map in GameMapIds)
        {
            if (leases.TryGetValue(map, out var lease) && counts.ContainsKey(lease.NodeId))
                counts[lease.NodeId]++;
        }
        // Keep healthy owners unless their load exceeds the balanced target.
        foreach (var map in GameMapIds)
        {
            leases.TryGetValue(map, out var old);
            var target = active.Length == 0 ? null : active
                .OrderBy(n => counts[n.NodeId]).ThenBy(n => n.NodeId, StringComparer.Ordinal).First();
            if (target is null) { if (old is not null) leases[map] = old with { ExpiresAtUtc = now }; continue; }
            var currentValid = old is not null && counts.ContainsKey(old.NodeId);
            var move = !currentValid || counts[old!.NodeId] > counts[target.NodeId] + 1;
            if (move)
            {
                if (currentValid) counts[old!.NodeId]--;
                leases[map] = new(map, target.NodeId, (old?.Epoch ?? 0) + 1, now + ttl);
                counts[target.NodeId]++;
            }
            else leases[map] = old! with { ExpiresAtUtc = now + ttl };
        }
        var result = new ServerMeshSnapshot(nodes.Values.OrderBy(n => n.NodeId, StringComparer.Ordinal)
            .Select(n => n with { LeasedMapIds = leases.Values.Where(l => n.Status == ServerMeshNodeStatus.Active
                && l.NodeId == n.NodeId && l.ExpiresAtUtc > now)
                .Select(l => l.MapId).Order().ToArray() }).ToArray(),
            leases.Values.OrderBy(l => l.MapId).ToArray(),
            desiredReplicas ?? (previous.DesiredReplicas > 0 ? previous.DesiredReplicas : options.Value.DesiredWorkers),
            maxMapsPerNode ?? (previous.MaxMapsPerNode > 0 ? previous.MaxMapsPerNode : options.Value.MaxMapsPerNode));
        await cache.SetAsync(StateKey, JsonSerializer.SerializeToUtf8Bytes(result),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) }, ct);
        return result;
    }
}
