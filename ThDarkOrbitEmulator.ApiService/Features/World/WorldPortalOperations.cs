using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

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

public sealed record PortalOperationOverride(bool Working);

public static class WorldPortalOperationEndpoints
{
    public static void MapWorldPortalOperations(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/backoffice/world/maps")
            .RequireAuthorization(EmulatorRoles.Admin).WithTags("World operations");
        admin.MapGet("/{mapId:int}/portals", async (int mapId, WorldPortalOperations operations, CancellationToken ct) =>
            ValidMap(mapId) ? Results.Ok((await operations.MapAsync(mapId, ct)).Portals) : Results.NotFound());
        admin.MapPut("/{mapId:int}/portals/{portalId:int}", async (int mapId, int portalId,
            PortalOperationOverride input, WorldPortalOperations operations, CancellationToken ct) =>
        {
            if (!ValidMap(mapId)) return Results.NotFound();
            var map = await operations.MapAsync(mapId, ct);
            if (!map.Portals.Any(portal => portal.Id == portalId)) return Results.NotFound();
            await operations.SetWorkingAsync(mapId, portalId, input.Working, ct);
            return Results.NoContent();
        });
    }

    private static bool ValidMap(int mapId) => mapId is >= 1 and <= 16 or >= 101 and <= 120;
}
