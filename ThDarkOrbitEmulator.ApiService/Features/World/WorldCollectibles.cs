using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public sealed record WorldCollectible(int Id, string Kind, int X, int Y, string AssetId, int Amount);
public sealed record WorldCollectResult(string Status, WorldCollectible? Box = null);

public interface IWorldRewardWallet
{
    Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct);
    Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct);
}

public sealed class WorldRewardWallet(EconomyStore economy) : IWorldRewardWallet
{
    public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct) =>
        economy.AdjustAsync(accountId, box.AssetId, box.Amount, "world_collect", "spacemap", key, ct);

    public async Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct)
    {
        var balances = await economy.BalancesAsync(accountId, ct);
        return balances.Where(b => b.AssetId is "prometium" or "endurium" or "terbium").Sum(b => b.Amount);
    }
}

public sealed class WorldCollectibles(IDistributedCache cache, IServerMeshMutex mutex,
    IWorldRewardWallet wallet, TimeProvider clock, IRuntimeVariableRegistry? variables = null,
    WorldAdminEvents? adminEvents = null)
{
    public const int CollectionRadius = 180;
    public const int RespawnSeconds = 300;
    private async Task<long> CycleAsync(CancellationToken ct) => clock.GetUtcNow().ToUnixTimeSeconds() /
        Math.Max(1, variables is null ? RespawnSeconds : (long)await variables.NumberAsync("world.collectibles.respawn_seconds", ct));
    private static string Key(int mapId, int id, long cycle) => $"world:collect:{mapId}:{id}:{cycle}";

    public static IReadOnlyList<WorldCollectible> Spawn(int mapId, long cycle, int count = 6)
    {
        _ = WorldMapInstance.Starter(mapId);
        // Slot coordinates are stable within a cycle and differ across maps.
        var rewards = new (string Kind, string Asset, int Amount)[]
        {
            ("bonus", "credits", 200), ("bonus", "uridium", 5),
            ("bonus", "galaxy_energy", 1), ("ore", "prometium", 10),
            ("ore", "endurium", 8), ("ore", "terbium", 6)
        };
        return Enumerable.Range(0, Math.Clamp(count, 0, 1000)).Select(index =>
        {
            var reward = rewards[index % rewards.Length];
            var seed = (long)mapId * 1709 + index * 7919 + cycle * 31;
            // The wire ID identifies this spawn, not a reusable map slot. A delayed
            // pickup from the previous cycle must not claim its replacement.
            var id = (mapId << 26) | ((int)(cycle & 0xffff) << 10) | (index + 1);
            return new WorldCollectible(id, reward.Kind,
                2600 + (int)(seed % 15800), 2200 + (int)((seed * 37) % 8700),
                reward.Asset, reward.Amount);
        }).ToArray();
    }

    public async Task<IReadOnlyList<WorldCollectible>> VisibleAsync(int mapId, CancellationToken ct)
    {
        var cycle = await CycleAsync(ct);
        var result = new List<WorldCollectible>();
        var count = variables is null ? 6 : (int)await variables.NumberAsync("world.spawn.bonus_box_count", ct);
        var generated = Spawn(mapId, cycle, count);
        var manual = adminEvents is null ? [] : await adminEvents.BoxesAsync(mapId, ct);
        foreach (var box in generated.Concat(manual))
            if (await cache.GetStringAsync(Key(mapId, box.Id, cycle), ct) is null) result.Add(box);
        return result;
    }

    public async Task<WorldCollectResult> CollectAsync(WorldPilot pilot, int id,
        WorldSessionStore sessions, CancellationToken ct, WorldLeaseFence? fence = null,
        int? expectedMapId = null)
    {
        if (pilot.AccountId == Guid.Empty) return new("invalid");
        await using var held = await mutex.AcquireAsync(ct);
        await WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct);
        var mapId = pilot.MapId;
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (fence is not null && pilot.MapId != mapId) throw new WorldLeaseLostException();
        if (expectedMapId is not null && pilot.MapId != expectedMapId)
            throw new WorldMapChangedException();
        var cycle = await CycleAsync(ct);
        var count = variables is null ? 6 : (int)await variables.NumberAsync("world.spawn.bonus_box_count", ct);
        var box = Spawn(pilot.MapId, cycle, count).FirstOrDefault(item => item.Id == id)
            ?? (adminEvents is null ? null : (await adminEvents.BoxesAsync(pilot.MapId, ct))
                .FirstOrDefault(item => item.Id == id));
        if (box is null) return new("missing");
        var (x, y) = sessions.CurrentPosition(pilot, clock.GetUtcNow());
        var dx = (long)x - box.X;
        var dy = (long)y - box.Y;
        var radius = variables is null ? CollectionRadius
            : (int)await variables.NumberAsync("world.collectibles.pickup_radius", ct);
        if (dx * dx + dy * dy > (long)radius * radius) return new("distant");
        var claimKey = Key(pilot.MapId, id, cycle);
        if (await cache.GetStringAsync(claimKey, ct) is not null) return new("claimed");
        if (box.Kind == "ore" && await wallet.CargoUsedAsync(pilot.AccountId, ct) + box.Amount
            > sessions.ActiveStats(pilot).CargoCapacity) return new("cargo_full");
        await cache.SetStringAsync(claimKey, pilot.UserId.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow =
                TimeSpan.FromSeconds(Math.Max(1, variables is null ? RespawnSeconds
                    : (int)await variables.NumberAsync("world.collectibles.respawn_seconds", ct)) + 30) }, ct);
        // Reserve before crediting: a process loss can forfeit one spawn, but cannot
        // award it to two different accounts on separate nodes.
        bool applied;
        try
        {
            var multiplierKey = box.AssetId switch
            {
                "credits" => "economy.credits_reward_multiplier",
                "uridium" => "economy.uridium_reward_multiplier",
                _ => null
            };
            var adjusted = multiplierKey is null || variables is null ? box : box with
            {
                Amount = Math.Max(0, (int)Math.Round(box.Amount * await variables.NumberAsync(multiplierKey, ct)))
            };
            applied = await wallet.CreditAsync(pilot.AccountId, adjusted, claimKey, ct);
            box = adjusted;
        }
        catch
        {
            // Keep the reservation when the ledger outcome is unknown.
            throw;
        }
        if (!applied) return new("claimed");
        await sessions.BroadcastCollectAsync(pilot, box, ct);
        return new("collected", box);
    }
}
