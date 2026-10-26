using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public sealed record WorldAnnouncement(string Message, string Actor, string Surface,
    DateTimeOffset AtUtc);

public sealed class WorldAdminEvents(IDistributedCache cache, IServerMeshMutex mutex)
{
    private static string SpawnKey(int mapId) => $"world:admin:spawns:{mapId}";
    private const string AnnouncementKey = "world:admin:announcement";
    private static readonly DistributedCacheEntryOptions Lifetime = new()
        { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) };
    private sealed record SpawnList(WorldCollectible[] Boxes, WorldNpc[] Npcs);

    private async Task<SpawnList> ReadAsync(int mapId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(SpawnKey(mapId), ct);
        return json is null ? new([], []) : JsonSerializer.Deserialize<SpawnList>(json)!;
    }

    public async Task<object> SpawnAsync(string kind, int mapId, int x, int y, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        var current = await ReadAsync(mapId, ct);
        if (current.Boxes.Length + current.Npcs.Length >= 200)
            throw new InvalidOperationException("Map already has 200 manual spawns.");
        var id = -Random.Shared.Next(1, int.MaxValue);
        object spawn;
        if (kind is "box" or "ore")
        {
            var box = new WorldCollectible(id, kind == "box" ? "bonus" : "ore", x, y,
                kind == "box" ? "credits" : "prometium", kind == "box" ? 200 : 10);
            current = current with { Boxes = [.. current.Boxes, box] }; spawn = box;
        }
        else
        {
            var (hp, shield) = kind switch
            { "streuner" => (800, 200), "lordakia" => (2000, 800), _ => (4000, 1600) };
            var npc = new WorldNpc(id, char.ToUpperInvariant(kind[0]) + kind[1..],
                $"npc_{kind}", x, y, hp, hp, shield, shield, 400, 1, 400, 2);
            current = current with { Npcs = [.. current.Npcs, npc] }; spawn = npc;
        }
        await cache.SetStringAsync(SpawnKey(mapId), JsonSerializer.Serialize(current), Lifetime, ct);
        return spawn;
    }

    public async Task<IReadOnlyList<WorldCollectible>> BoxesAsync(int mapId, CancellationToken ct) =>
        (await ReadAsync(mapId, ct)).Boxes;
    public async Task<IReadOnlyList<WorldNpc>> NpcsAsync(int mapId, CancellationToken ct) =>
        (await ReadAsync(mapId, ct)).Npcs;

    public async Task<WorldAnnouncement> AnnounceAsync(string message, AdminCommandActor actor, CancellationToken ct)
    {
        var announcement = new WorldAnnouncement(message, actor.Actor, actor.Surface, DateTimeOffset.UtcNow);
        await cache.SetStringAsync(AnnouncementKey, JsonSerializer.Serialize(announcement),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, ct);
        return announcement;
    }

    public async Task<WorldAnnouncement?> LatestAsync(CancellationToken ct)
    {
        var json = await cache.GetStringAsync(AnnouncementKey, ct);
        return json is null ? null : JsonSerializer.Deserialize<WorldAnnouncement>(json);
    }
}
