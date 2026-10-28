using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Positions are deterministic from UTC time; only the current map lease owner emits local TCP updates.
public sealed class WorldNpcSimulationService(WorldNpcs npcs, WorldSessionStore sessions,
    IServerMeshRegistry registry, IOptions<ClusterOptions> cluster,
    IRuntimeVariableRegistry? variables = null, WorldCollectibles? collectibles = null,
    WorldAdminEvents? adminEvents = null) : BackgroundService
{
    private readonly Dictionary<int, HashSet<int>> known = [];
    private readonly Dictionary<int, HashSet<int>> knownBoxes = [];
    private DateTimeOffset lastAnnouncement;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = variables is null ? 1000 : (int)await variables.NumberAsync("world.tick_rate_ms", stoppingToken);
            await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
            var snapshot = await registry.GetSnapshotAsync(stoppingToken);
            if (adminEvents is not null)
            {
                var announcement = await adminEvents.LatestAsync(stoppingToken);
                if (announcement is not null && announcement.AtUtc > lastAnnouncement)
                {
                    lastAnnouncement = announcement.AtUtc;
                    await sessions.BroadcastAnnouncementAsync(announcement.Message, stoppingToken);
                }
            }
            var nodeId = string.IsNullOrWhiteSpace(cluster.Value.NodeId)
                ? $"{Environment.MachineName}-{Environment.ProcessId}" : cluster.Value.NodeId;
            var owned = snapshot.Leases.Where(l => l.NodeId == nodeId && l.ExpiresAtUtc > DateTimeOffset.UtcNow)
                .Select(l => l.MapId).ToHashSet();
            foreach (var mapId in known.Keys.Where(id => !owned.Contains(id)).ToArray())
            { known.Remove(mapId); knownBoxes.Remove(mapId); }
            foreach (var lease in snapshot.Leases.Where(l => l.NodeId == nodeId && l.ExpiresAtUtc > DateTimeOffset.UtcNow))
            {
                try
                {
                    var fence = new WorldLeaseFence(registry, nodeId, lease.Epoch);
                    await npcs.TickAggroAsync(lease.MapId, sessions, stoppingToken, fence);
                    await npcs.TickLasersAsync(lease.MapId, sessions, stoppingToken, fence);
                    await sessions.RecoverShieldsAsync(lease.MapId, stoppingToken, fence);
                }
                catch (WorldLeaseLostException) { continue; }
                var visible = await npcs.VisibleAsync(lease.MapId, stoppingToken);
                if (!known.TryGetValue(lease.MapId, out var ids)) known[lease.MapId] = ids = [];
                foreach (var npc in visible)
                {
                    if (ids.Add(npc.Id)) await sessions.BroadcastNpcSpawnAsync(lease.MapId, npc, stoppingToken);
                    await sessions.BroadcastNpcMoveAsync(lease.MapId, npc, stoppingToken);
                }
                ids.IntersectWith(visible.Select(n => n.Id));
                if (collectibles is not null)
                {
                    var boxes = await collectibles.VisibleAsync(lease.MapId, stoppingToken);
                    if (!knownBoxes.TryGetValue(lease.MapId, out var boxIds))
                        knownBoxes[lease.MapId] = boxIds = [];
                    var (removed, created) = WorldCollectibleVisibility.Diff(boxIds, boxes);
                    foreach (var id in removed)
                        await sessions.BroadcastBoxRemovalAsync(lease.MapId, id, stoppingToken);
                    foreach (var box in created)
                        await sessions.BroadcastBoxSpawnAsync(lease.MapId, box, stoppingToken);
                }
            }
        }
    }
}
