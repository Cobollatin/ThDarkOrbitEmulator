using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

public sealed record MeshTopologyResponse(int DesiredWorkers, int DesiredReplicas, int MaxReplicas,
    int MaxMapsPerNode, int ActiveNodeCount, int TotalOnlinePilots,
    ServerMapLease[] MapLeaseDistribution, ServerMeshNode[] Nodes, string AspireDashboardLink);

public sealed class MeshTopologyEndpoint(IServerMeshRegistry registry, WorldSessionStore sessions)
    : EndpointWithoutRequest<MeshTopologyResponse>
{
    public override void Configure()
    {
        Get("backoffice/mesh/topology"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await registry.GetSnapshotAsync(ct);
        var pilots = await MeshWorldOverview.ReadPilotsAsync(sessions, ct);
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new(snapshot.DesiredReplicas, snapshot.DesiredReplicas, 16,
            snapshot.MaxMapsPerNode, snapshot.Nodes.Count(n => n.Status == ServerMeshNodeStatus.Active),
            pilots.Count, snapshot.Leases, snapshot.Nodes, "https://localhost:17081"), ct);
    }
}

public sealed record MeshScaleRequest(int DesiredReplicas, int MaxMapsPerNode);

// The local spawner registers explicit simulations sharing this process's world listener.
public sealed class LocalWorkerSpawner(IServerMeshRegistry registry)
{
    public Task<ServerMeshSnapshot> SetDesiredAsync(int desired, int maxMapsPerNode, CancellationToken ct) =>
        registry.ScaleAsync(desired, maxMapsPerNode, ct);
}

public sealed class MeshScaleEndpoint(LocalWorkerSpawner spawner) : Endpoint<MeshScaleRequest, ServerMeshSnapshot>
{
    public override void Configure()
    {
        Put("backoffice/mesh/scale"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(MeshScaleRequest req, CancellationToken ct)
    {
        if (req.DesiredReplicas is < 1 or > 16 || req.MaxMapsPerNode is < 1 or > 256)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "DesiredReplicas must be 1..16 and MaxMapsPerNode 1..256." }, ct);
            return;
        }
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await spawner.SetDesiredAsync(req.DesiredReplicas, req.MaxMapsPerNode, ct), ct);
    }
}

public sealed class MeshDrainEndpoint(IServerMeshRegistry registry) : EndpointWithoutRequest<ServerMeshSnapshot>
{
    public override void Configure()
    {
        Post("backoffice/mesh/nodes/{nodeId}/drain"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var nodeId = Route<string>("nodeId");
        var before = await registry.GetSnapshotAsync(ct);
        var node = before.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null) { await Send.NotFoundAsync(ct); return; }
        if (node.Status != ServerMeshNodeStatus.Active
            || before.Nodes.Count(n => n.Status == ServerMeshNodeStatus.Active) < 2
            || (!node.Simulated && node.ActivePilots > 0))
        {
            HttpContext.Response.StatusCode = 409;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Node cannot drain while pilots are connected or no healthy target exists." }, ct);
            return;
        }
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.SetDrainingAsync(nodeId!, ct), ct);
    }
}

public sealed class MeshRebalanceEndpoint(IServerMeshRegistry registry) : EndpointWithoutRequest<ServerMeshSnapshot>
{
    public override void Configure()
    {
        Post("backoffice/mesh/rebalance"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.RebalanceAsync(ct), ct);
    }
}

public sealed class MeshActivateEndpoint(IServerMeshRegistry registry) : EndpointWithoutRequest<ServerMeshSnapshot>
{
    public override void Configure()
    {
        Post("backoffice/mesh/nodes/{nodeId}/activate"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var nodeId = Route<string>("nodeId");
        var node = (await registry.GetSnapshotAsync(ct)).Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null) { await Send.NotFoundAsync(ct); return; }
        if (node.Status != ServerMeshNodeStatus.Draining)
        {
            HttpContext.Response.StatusCode = 409;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Only a live draining node can be activated." }, ct);
            return;
        }
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.SetActiveAsync(nodeId!, ct), ct);
    }
}

public static class MeshWorldOverview
{
    public static async Task<IReadOnlyList<WorldPilot>> ReadPilotsAsync(WorldSessionStore sessions, CancellationToken ct)
    {
        var maps = await Task.WhenAll(ServerMeshCoordinator.GameMapIds.Select(id => sessions.ConnectedAsync(id, ct)));
        return maps.SelectMany(p => p).GroupBy(p => p.UserId).Select(p => p.First()).ToArray();
    }
}

public sealed record WorldMapOverview(int MapId, string? OwnerNodeId, int OnlinePilots);
public sealed record WorldOverviewResponse(WorldMapOverview[] Maps, IReadOnlyList<WorldPilot> Pilots);

public sealed class WorldOverviewEndpoint(IServerMeshRegistry registry, WorldSessionStore sessions)
    : EndpointWithoutRequest<WorldOverviewResponse>
{
    public override void Configure()
    {
        Get("backoffice/world/overview"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await registry.GetSnapshotAsync(ct);
        var pilots = await MeshWorldOverview.ReadPilotsAsync(sessions, ct);
        var maps = ServerMeshCoordinator.GameMapIds.Select(id => new WorldMapOverview(id,
            snapshot.Leases.FirstOrDefault(l => l.MapId == id)?.NodeId,
            pilots.Count(p => p.MapId == id))).ToArray();
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new(maps, pilots), ct);
    }
}

public sealed record WorldTeleportRequest(int MapId, int X, int Y, bool Repair);
public sealed class WorldTeleportEndpoint(WorldSessionStore sessions) : Endpoint<WorldTeleportRequest, WorldPilot>
{
    public override void Configure()
    {
        Post("backoffice/world/pilots/{userId}/teleport"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(WorldTeleportRequest req, CancellationToken ct)
    {
        var userId = Route<long>("userId");
        if (userId <= 0 || req.MapId is < 1 or > 16 || req.X is < 0 or > 21000 || req.Y is < 0 or > 13100)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Invalid pilot, map or coordinates." }, ct);
            return;
        }
        var pilot = await sessions.TeleportAsync(userId, req.MapId, req.X, req.Y, req.Repair, ct);
        if (pilot is null) { await Send.NotFoundAsync(ct); return; }
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(pilot, ct);
    }
}
