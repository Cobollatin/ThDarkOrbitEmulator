using Microsoft.Extensions.Caching.Distributed;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Shared operation state for local map events and administrative controls.
public sealed class WorldPortalOperations(IDistributedCache cache)
{
    private static string Key(int mapId, int portalId) => $"world:portal-operation:v1:{mapId}:{portalId}";

    public async Task<WorldMapInstance> MapAsync(int mapId, CancellationToken ct)
    {
        var map = WorldMapInstance.Starter(mapId);
        var portals = new WorldPortal[map.Portals.Count];
        for (var index = 0; index < portals.Length; index++)
        {
            var portal = map.Portals[index];
            var state = await cache.GetStringAsync(Key(mapId, portal.Id), ct);
            portals[index] = portal with { Working = state is null ? portal.Working : state == "1" };
        }
        return map with { Portals = portals };
    }

    public async Task SetWorkingAsync(int mapId, int portalId, bool working, CancellationToken ct)
    {
        if (!WorldMapInstance.Starter(mapId).Portals.Any(portal => portal.Id == portalId))
            throw new ArgumentOutOfRangeException(nameof(portalId));
        await cache.SetStringAsync(Key(mapId, portalId), working ? "1" : "0", ct);
    }
}
