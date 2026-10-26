using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public sealed record WorldNpc(int Id, string Kind, string ShipId, int X, int Y,
    int Hp, int MaxHp, int Shield, int MaxShield, int Credits, int Uridium, int Experience, int Honor);
public sealed record WorldNpcReward(int Credits, int Uridium, int Experience, int Honor);
public sealed record WorldNpcAttack(string Status, WorldNpc? Target = null, int Damage = 0,
    bool Destroyed = false, WorldNpcReward? Reward = null);
public sealed record WorldNpcTemplate(string Kind, string ShipId, int Count, int Hp, int Shield,
    int Credits, int Uridium, int Experience, int Honor, int Speed);
public sealed class WorldNpcOptions
{
    public const string SectionName = "WorldNpcs";
    public int RespawnSeconds { get; set; } = 300;
    public int LaserRange { get; set; } = 1000;
    public List<WorldNpcTemplate> Templates { get; set; } =
    [
        new("Streuner", "npc_streuner", 3, 800, 200, 400, 1, 400, 2, 90),
        new("Lordakia", "npc_lordakia", 2, 2000, 800, 800, 2, 800, 4, 70),
        new("Saimon", "npc_saimon", 1, 4000, 1600, 1600, 4, 1600, 8, 55)
    ];
    public Dictionary<int, List<WorldNpcTemplate>> MapTemplates { get; set; } = [];
}

public interface IWorldNpcRewards
{
    Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct);
}

public sealed class WorldNpcRewards(EconomyStore economy) : IWorldNpcRewards
{
    public async Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
    {
        foreach (var (asset, amount) in new[] { ("credits", npc.Credits), ("uridium", npc.Uridium),
                     ("experience", npc.Experience), ("honor", npc.Honor) })
            if (amount > 0)
                await economy.AdjustAsync(accountId, asset, amount, "npc_destroyed", npc.Kind.ToLowerInvariant(),
                    $"{key}:{asset}", ct);
    }
}

public sealed class WorldNpcs(IDistributedCache cache, IServerMeshMutex mutex,
    IWorldNpcRewards rewards, IOptions<WorldNpcOptions> options, TimeProvider clock,
    IRuntimeVariableRegistry? variables = null, WorldAdminEvents? adminEvents = null)
{
    private readonly WorldNpcOptions settings = options.Value;
    public static readonly TimeSpan ObserverSnapshotTtl = TimeSpan.FromMilliseconds(500);
    private sealed record ObserverSnapshot(DateTimeOffset ReadAtUtc, IReadOnlyList<WorldNpc> Npcs);
    private readonly ConcurrentDictionary<int, Lazy<Task<ObserverSnapshot>>> observerSnapshots = new();
    private sealed record Vital(int Hp, int Shield, bool Destroyed, Guid Killer,
        WorldNpcReward? Reward = null, WorldNpc? Snapshot = null);
    private long Cycle => clock.GetUtcNow().ToUnixTimeSeconds() / Math.Max(30, settings.RespawnSeconds);
    public long CurrentCycle => Cycle;
    private string Key(int map, int id, long cycle) => $"world:npc:{map}:{id}:{cycle}";
    private static string TargetKey(long userId) => $"world:npc:target:{userId}";
    private DistributedCacheEntryOptions Expiry => new() { AbsoluteExpirationRelativeToNow =
        TimeSpan.FromSeconds(Math.Max(30, settings.RespawnSeconds) + 30) };

    public IReadOnlyList<WorldNpc> Spawn(int mapId, DateTimeOffset at,
        IReadOnlyDictionary<string, int>? countOverrides = null)
    {
        var map = WorldMapInstance.Starter(mapId);
        var templates = settings.MapTemplates.GetValueOrDefault(mapId) ?? settings.Templates;
        var result = new List<WorldNpc>();
        for (var type = 0; type < templates.Count; type++)
        {
            var template = templates[type];
            var count = countOverrides?.GetValueOrDefault(template.Kind) ?? template.Count;
            if (count is < 0 or > 30 || template.Hp <= 0 || template.Shield < 0 ||
                template.Speed < 0 || template.Speed > 1000) continue;
            for (var slot = 0; slot < count; slot++)
            {
                var id = mapId * 10000 + type * 100 + slot + 1;
                var baseX = 3800 + (id * 1739L % 13000);
                var baseY = 2600 + (id * 941L % 7000);
                var phase = at.ToUnixTimeSeconds() * template.Speed / 100 + slot * 83;
                var x = (int)Math.Clamp(baseX + Math.Sin(phase / 110.0) * 850, 0, map.Width);
                var y = (int)Math.Clamp(baseY + Math.Cos(phase / 130.0) * 650, 0, map.Height);
                result.Add(new(id, template.Kind, template.ShipId, x, y, template.Hp,
                    template.Hp, template.Shield, template.Shield, template.Credits,
                    template.Uridium, template.Experience, template.Honor));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<WorldNpc>> VisibleAsync(int mapId, CancellationToken ct)
    {
        var cycle = Cycle;
        var result = new List<WorldNpc>();
        foreach (var npc in await SpawnLiveAsync(mapId, clock.GetUtcNow(), ct))
        {
            var stored = await cache.GetStringAsync(Key(mapId, npc.Id, cycle), ct);
            var vital = stored is null ? null : JsonSerializer.Deserialize<Vital>(stored);
            if (vital?.Destroyed != true)
                result.Add(npc with { Hp = vital?.Hp ?? npc.Hp, Shield = vital?.Shield ?? npc.Shield });
        }
        return result;
    }

    // Presentation-only memo shared by all TCP observers on this node. Attacks and selections
    // still read authoritative cache state. Remote writes become visible after the short TTL.
    public async Task<IReadOnlyList<WorldNpc>> VisibleForObserversAsync(int mapId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lazy = observerSnapshots.GetOrAdd(mapId, id => new(() => ReadObserverSnapshotAsync(id)));
            ObserverSnapshot snapshot;
            try { snapshot = await lazy.Value.WaitAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                ((ICollection<KeyValuePair<int, Lazy<Task<ObserverSnapshot>>>>)observerSnapshots)
                    .Remove(new(mapId, lazy));
                throw;
            }
            if (attempt > 0 || clock.GetUtcNow() - snapshot.ReadAtUtc < ObserverSnapshotTtl)
                return snapshot.Npcs;
            ((ICollection<KeyValuePair<int, Lazy<Task<ObserverSnapshot>>>>)observerSnapshots)
                .Remove(new(mapId, lazy));
        }
    }

    private async Task<ObserverSnapshot> ReadObserverSnapshotAsync(int mapId) =>
        new(clock.GetUtcNow(), await VisibleAsync(mapId, CancellationToken.None));

    private async Task<IReadOnlyList<WorldNpc>> SpawnLiveAsync(int mapId, DateTimeOffset at, CancellationToken ct)
    {
        if (variables is null) return adminEvents is null ? Spawn(mapId, at)
            : [.. Spawn(mapId, at), .. await adminEvents.NpcsAsync(mapId, ct)];
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Streuner"] = (int)await variables.NumberAsync("world.spawn.streuner_count", ct),
            ["Lordakia"] = (int)await variables.NumberAsync("world.spawn.lordakia_count", ct),
            ["Saimon"] = (int)await variables.NumberAsync("world.spawn.saimon_count", ct)
        };
        return adminEvents is null ? Spawn(mapId, at, limits)
            : [.. Spawn(mapId, at, limits), .. await adminEvents.NpcsAsync(mapId, ct)];
    }

    public async Task<WorldNpc?> SelectAsync(WorldPilot pilot, int npcId, CancellationToken ct,
        WorldLeaseFence? fence = null, WorldSessionStore? sessions = null, int? expectedMapId = null)
    {
        await using var held = await mutex.AcquireAsync(ct);
        await WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct);
        if (sessions is not null)
        {
            var mapId = pilot.MapId;
            pilot = await sessions.RestoreAsync(pilot, ct);
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (fence is not null && pilot.MapId != mapId) throw new WorldLeaseLostException();
        }
        var target = (await VisibleAsync(pilot.MapId, ct)).FirstOrDefault(n => n.Id == npcId);
        if (target is null) return null;
        await cache.SetStringAsync(TargetKey(pilot.UserId), $"{pilot.MapId}:{npcId}:{Cycle}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
        return target;
    }

    public async Task<bool> IsSelectedAsync(WorldPilot pilot, int npcId, CancellationToken ct) =>
        await cache.GetStringAsync(TargetKey(pilot.UserId), ct) == $"{pilot.MapId}:{npcId}:{Cycle}";

    public async Task<bool> CanRetryRewardAsync(WorldPilot pilot, int npcId, CancellationToken ct)
    {
        if (pilot.AccountId == Guid.Empty) return false;
        var stored = await cache.GetStringAsync(Key(pilot.MapId, npcId, Cycle), ct);
        if (stored is null) return false;
        var vital = JsonSerializer.Deserialize<Vital>(stored);
        return vital is { Destroyed: true } && vital.Killer == pilot.AccountId;
    }

    public async Task<WorldNpcAttack> AttackAsync(WorldPilot pilot, int npcId,
        WorldSessionStore sessions, CancellationToken ct, WorldLeaseFence? fence = null,
        int? expectedMapId = null)
    {
        if (pilot.AccountId == Guid.Empty) return new("invalid");
        WorldNpc? npc;
        WorldNpc? target = null;
        string key;
        var damage = 0;
        var dead = false;
        var retryReward = false;
        WorldNpcReward? reward = null;
        await using (var held = await mutex.AcquireAsync(ct))
        {
            await WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct);
            var mapId = pilot.MapId;
            pilot = await sessions.RestoreAsync(pilot, ct);
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (fence is not null && pilot.MapId != mapId) throw new WorldLeaseLostException();
            key = Key(pilot.MapId, npcId, Cycle);
            var stored = await cache.GetStringAsync(key, ct);
            var saved = stored is null ? null : JsonSerializer.Deserialize<Vital>(stored);
            npc = (await SpawnLiveAsync(pilot.MapId, clock.GetUtcNow(), ct)).FirstOrDefault(n => n.Id == npcId)
                ?? (saved?.Destroyed == true ? saved.Snapshot : null);
            if (npc is null) return new("missing");
            var vital = saved ?? new Vital(npc.Hp, npc.Shield, false, Guid.Empty);
            if (vital.Destroyed)
            {
                if (vital.Killer != pilot.AccountId) return new("destroyed");
                retryReward = true;
                reward = vital.Reward;
            }
            else
            {
                var (x, y) = sessions.CurrentPosition(pilot, clock.GetUtcNow());
                var fireRange = variables is null ? settings.LaserRange
                    : await variables.NumberAsync("world.combat.fire_range", ct);
                if ((long)(x - npc.X) * (x - npc.X) + (long)(y - npc.Y) * (y - npc.Y) >
                    fireRange * fireRange) return new("distant", npc);
                var multiplier = variables is null ? 1 : await variables.NumberAsync("world.combat.laser_damage_multiplier", ct);
                damage = Math.Max(1, (int)Math.Round(sessions.ActiveStats(pilot).LaserDamage * multiplier));
                var absorption = variables is null ? 1 : Math.Clamp(
                    await variables.NumberAsync("world.combat.shield_absorption_ratio", ct), 0, 1);
                var absorbed = Math.Min(vital.Shield, (int)Math.Round(damage * absorption));
                var shield = Math.Max(0, vital.Shield - absorbed);
                var hp = Math.Max(0, vital.Hp - (damage - absorbed));
                dead = hp == 0;
                if (dead)
                {
                    var adjusted = await RewardAdjustedAsync(npc, ct);
                    reward = new(adjusted.Credits, adjusted.Uridium,
                        adjusted.Experience, adjusted.Honor);
                }
                await cache.SetStringAsync(key, JsonSerializer.Serialize(new Vital(hp, shield, dead,
                    dead ? pilot.AccountId : Guid.Empty, reward, dead ? npc : null)), Expiry, ct);
                observerSnapshots.TryRemove(pilot.MapId, out _);
                target = npc with { Hp = hp, Shield = shield };
            }
        }
        // Older cached kills have no reward snapshot; retain their retry behavior.
        if (retryReward)
        {
            reward ??= await RewardValuesAsync(npc!, ct);
            await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward), key, ct);
            return new("destroyed");
        }
        if (dead)
        {
            await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward!), key, ct);
        }
        await sessions.BroadcastNpcDamageAsync(pilot, target!, damage, dead, reward, ct);
        return new("hit", target, damage, dead, reward);
    }

    private async Task<WorldNpc> RewardAdjustedAsync(WorldNpc npc, CancellationToken ct) => variables is null ? npc : npc with
    {
        Credits = (int)Math.Round(npc.Credits * await variables.NumberAsync("economy.credits_reward_multiplier", ct)),
        Uridium = (int)Math.Round(npc.Uridium * await variables.NumberAsync("economy.uridium_reward_multiplier", ct)),
        Experience = (int)Math.Round(npc.Experience * await variables.NumberAsync("economy.xp_reward_multiplier", ct)),
        Honor = (int)Math.Round(npc.Honor * await variables.NumberAsync("economy.honor_reward_multiplier", ct))
    };

    private async Task<WorldNpcReward> RewardValuesAsync(WorldNpc npc, CancellationToken ct)
    {
        var adjusted = await RewardAdjustedAsync(npc, ct);
        return new(adjusted.Credits, adjusted.Uridium, adjusted.Experience, adjusted.Honor);
    }

    private static WorldNpc ApplyReward(WorldNpc npc, WorldNpcReward reward) => npc with
    {
        Credits = reward.Credits, Uridium = reward.Uridium,
        Experience = reward.Experience, Honor = reward.Honor
    };
}
