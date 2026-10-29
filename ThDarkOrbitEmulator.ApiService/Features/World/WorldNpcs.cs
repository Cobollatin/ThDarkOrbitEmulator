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
public sealed record WorldNpcPulse(string Status, IReadOnlyList<WorldNpcAttack> Hits);
public sealed record WorldLauncherStatus(int Loaded, int Capacity, long ReloadRemainingMs, bool AutoFire = false);
public sealed record WorldNpcTemplate(string Kind, string ShipId, int Count, int Hp, int Shield,
    int Credits, int Uridium, int Experience, int Honor, int Speed, bool Aggressive = true);
public sealed class WorldNpcOptions
{
    public const string SectionName = "WorldNpcs";
    public int RespawnSeconds { get; set; } = 300;
    public int LaserRange { get; set; } = 1000;
    public int RocketRange { get; set; } = 800;
    public int LauncherRange { get; set; } = 1500;
    public int RocketDamage { get; set; } = 250;
    public double RocketMissChance { get; set; }
    public double RocketDamageSpread { get; set; }
    public int RocketCooldownSeconds { get; set; } = 3;
    public int LauncherCapacity { get; set; } = 5;
    public int LauncherReloadSeconds { get; set; } = 2;
    public int LauncherRocketDamage { get; set; } = 100;
    public bool AutoLauncherOnLaser { get; set; }
    public bool AutoRocketOnLaser { get; set; }
    public int PulseRange { get; set; } = 700;
    public int PulseCooldownSeconds { get; set; } = 30;
    public int LaserCooldownMilliseconds { get; set; } = 1000;
    public double LaserMissChance { get; set; }
    public double LaserDamageSpread { get; set; }
    public int AggroRange { get; set; } = 1200;
    public int NpcStandoffRange { get; set; } = 300;
    public int NpcFireRange { get; set; } = 650;
    public int NpcDamage { get; set; } = 40;
    public double NpcMissChance { get; set; } = 0.1;
    public double NpcDamageSpread { get; set; }
    public double PilotShieldAbsorption { get; set; } = 0.8;
    public int NpcFireCooldownSeconds { get; set; } = 1;
    public int NpcShieldRepairDelaySeconds { get; set; } = 10;
    public int NpcShieldRepairIntervalSeconds { get; set; } = 1;
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
    IRuntimeVariableRegistry? variables = null, WorldAdminEvents? adminEvents = null,
    IWorldPulseInventory? pulseInventory = null)
{
    private readonly WorldNpcOptions settings = options.Value;
    public static readonly TimeSpan ObserverSnapshotTtl = TimeSpan.FromMilliseconds(500);
    private sealed record ObserverSnapshot(DateTimeOffset ReadAtUtc, IReadOnlyList<WorldNpc> Npcs);
    private readonly ConcurrentDictionary<int, Lazy<Task<ObserverSnapshot>>> observerSnapshots = new();
    private sealed record Vital(int Hp, int Shield, bool Destroyed, Guid Killer,
        WorldNpcReward? Reward = null, WorldNpc? Snapshot = null,
        int? X = null, int? Y = null, long TargetUserId = 0, long LastFireUnixMs = 0,
        long LastStepUnixMs = 0, long LastHitUnixMs = 0, long LastShieldRepairUnixMs = 0,
        long RespawnAtUnixMs = 0, long Generation = 0, Guid[]? PulseActions = null,
        bool Retaliating = false, int? PatrolOriginX = null, int? PatrolOriginY = null,
        bool PatrolOutbound = true, int PatrolLeg = 0);
    private long Cycle => clock.GetUtcNow().ToUnixTimeSeconds() / Math.Max(30, settings.RespawnSeconds);
    public long CurrentCycle => Cycle;
    private static string Key(int map, int id) => $"world:npc:v2:{map}:{id}";
    private static string TargetKey(long userId) => $"world:npc:target:{userId}";
    private static string LaserKey(long userId) => $"world:npc:laser:{userId}";
    private sealed record ActiveLaser(int MapId, int NpcId, long Cycle, long NextShotUnixMs);
    private sealed record LauncherLoad(int Count, long UpdatedAtUnixMs);
    private static string LauncherAutoKey(long userId) => $"world:launcher:auto:{userId}";
    private static string RocketAutoKey(long userId) => $"world:rocket:auto:{userId}";
    private static DistributedCacheEntryOptions Expiry => new();

    public async Task<bool> SetAutoLauncherAsync(long userId, bool enabled, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        await cache.SetStringAsync(LauncherAutoKey(userId), enabled ? "1" : "0", Expiry, ct);
        return enabled;
    }

    private async Task<bool> AutoLauncherEnabledAsync(long userId, CancellationToken ct)
    {
        var stored = await cache.GetStringAsync(LauncherAutoKey(userId), ct);
        return stored is null ? settings.AutoLauncherOnLaser : stored == "1";
    }

    public async Task<bool> SetAutoRocketAsync(long userId, bool enabled, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        await cache.SetStringAsync(RocketAutoKey(userId), enabled ? "1" : "0", Expiry, ct);
        return enabled;
    }

    public async Task<bool> AutoRocketEnabledAsync(long userId, CancellationToken ct)
    {
        var stored = await cache.GetStringAsync(RocketAutoKey(userId), ct);
        return stored is null ? settings.AutoRocketOnLaser : stored == "1";
    }

    public async Task<WorldLauncherStatus> LauncherStatusAsync(long userId, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        var capacity = Math.Clamp(settings.LauncherCapacity, 1, 20);
        var key = $"world:launcher:{userId}";
        var stored = await cache.GetStringAsync(key, ct);
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (stored is null)
        {
            stored = JsonSerializer.Serialize(new LauncherLoad(0, nowMs));
            await cache.SetStringAsync(key, stored, Expiry, ct);
        }
        var load = JsonSerializer.Deserialize<LauncherLoad>(stored)!;
        var reloadMs = Math.Max(1, settings.LauncherReloadSeconds) * 1000L;
        var elapsed = Math.Max(0, nowMs - load.UpdatedAtUnixMs);
        var loaded = Math.Clamp(load.Count, 0, capacity);
        var added = Math.Min(capacity - loaded, elapsed / reloadMs);
        loaded += (int)added;
        var remaining = loaded == capacity ? 0 : reloadMs - (elapsed % reloadMs);
        return new(loaded, capacity, remaining, await AutoLauncherEnabledAsync(userId, ct));
    }

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
        var result = new List<WorldNpc>();
        foreach (var npc in await SpawnLiveAsync(mapId, clock.GetUtcNow(), ct))
        {
            var stored = await cache.GetStringAsync(Key(mapId, npc.Id), ct);
            var vital = stored is null ? null : JsonSerializer.Deserialize<Vital>(stored);
            if (vital?.Destroyed != true)
                result.Add(npc with { Hp = vital?.Hp ?? npc.Hp, Shield = vital?.Shield ?? npc.Shield,
                    X = vital?.X ?? npc.X, Y = vital?.Y ?? npc.Y });
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
            if (sessions.CurrentHitPoints(pilot) <= 0) return null;
        }
        var target = (await VisibleAsync(pilot.MapId, ct)).FirstOrDefault(n => n.Id == npcId);
        if (target is null) return null;
        await cache.SetStringAsync(TargetKey(pilot.UserId), $"{pilot.MapId}:{npcId}:{Cycle}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
        return target;
    }

    public async Task<bool> IsSelectedAsync(WorldPilot pilot, int npcId, CancellationToken ct) =>
        await cache.GetStringAsync(TargetKey(pilot.UserId), ct) == $"{pilot.MapId}:{npcId}:{Cycle}";

    public async Task StartLaserAsync(WorldPilot pilot, int npcId, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        if (await AutoLauncherEnabledAsync(pilot.UserId, ct))
        {
            var loadKey = $"world:launcher:{pilot.UserId}";
            if (await cache.GetStringAsync(loadKey, ct) is null)
                await cache.SetStringAsync(loadKey,
                    JsonSerializer.Serialize(new LauncherLoad(0, clock.GetUtcNow().ToUnixTimeMilliseconds())),
                    Expiry, ct);
        }
        await cache.SetStringAsync(LaserKey(pilot.UserId),
            JsonSerializer.Serialize(new ActiveLaser(pilot.MapId, npcId, Cycle,
                clock.GetUtcNow().ToUnixTimeMilliseconds() + Math.Max(1, settings.LaserCooldownMilliseconds))),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
    }

    public async Task StopLaserAsync(long userId, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        await cache.RemoveAsync(LaserKey(userId), ct);
    }

    public async Task TickLasersAsync(int mapId, WorldSessionStore sessions, CancellationToken ct,
        WorldLeaseFence? fence = null)
    {
        await WorldSessionStore.CheckLeaseAsync(mapId, fence, ct);
        foreach (var pilot in await sessions.ConnectedAsync(mapId, ct))
        {
            var stored = await cache.GetStringAsync(LaserKey(pilot.UserId), ct);
            if (stored is null) continue;
            var active = JsonSerializer.Deserialize<ActiveLaser>(stored)!;
            if (active.MapId != mapId || active.Cycle != Cycle ||
                !await IsSelectedAsync(pilot, active.NpcId, ct) || sessions.CurrentHitPoints(pilot) <= 0)
            {
                await StopLaserAsync(pilot.UserId, ct);
                continue;
            }
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (now < active.NextShotUnixMs) continue;
            // Reserve the interval before firing so a failed broadcast cannot repeat damage.
            await using (var held = await mutex.AcquireAsync(ct))
            {
                if (await cache.GetStringAsync(LaserKey(pilot.UserId), ct) != stored) continue;
                await cache.SetStringAsync(LaserKey(pilot.UserId), JsonSerializer.Serialize(active with
                { NextShotUnixMs = now + Math.Max(1, settings.LaserCooldownMilliseconds) }),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
            }
            var hit = await AttackAsync(pilot, active.NpcId, sessions, ct, fence, mapId);
            if (hit.Status != "hit" || hit.Destroyed)
            {
                await StopLaserAsync(pilot.UserId, ct);
                continue;
            }
            if (await AutoRocketEnabledAsync(pilot.UserId, ct))
            {
                var rocket = await AttackAsync(pilot, active.NpcId, sessions, ct, fence, mapId,
                    rocket: true);
                if (rocket.Destroyed)
                {
                    await StopLaserAsync(pilot.UserId, ct);
                    continue;
                }
            }
            if (await AutoLauncherEnabledAsync(pilot.UserId, ct) &&
                (await LauncherStatusAsync(pilot.UserId, ct)).Loaded == Math.Clamp(settings.LauncherCapacity, 1, 20))
            {
                var volley = await AttackAsync(pilot, active.NpcId, sessions, ct, fence, mapId,
                    launcher: true);
                if (volley.Destroyed)
                    await StopLaserAsync(pilot.UserId, ct);
            }
        }
    }

    public async Task<bool> CanRetryRewardAsync(WorldPilot pilot, int npcId, CancellationToken ct)
    {
        if (pilot.AccountId == Guid.Empty) return false;
        var stored = await cache.GetStringAsync(Key(pilot.MapId, npcId), ct);
        if (stored is null) return false;
        var vital = JsonSerializer.Deserialize<Vital>(stored);
        return vital is { Destroyed: true } && vital.Killer == pilot.AccountId;
    }

    public async Task<WorldNpcPulse> PulseAsync(WorldPilot pilot, WorldSessionStore sessions,
        CancellationToken ct, WorldLeaseFence? fence = null, int? expectedMapId = null,
        Guid? actionId = null)
    {
        if (pilot.AccountId == Guid.Empty || WorldMapInstance.IsGate(pilot.MapId))
            return new("invalid", []);
        var pulseActionId = actionId.GetValueOrDefault();
        if (pulseActionId == Guid.Empty) pulseActionId = Guid.NewGuid();
        PulseTarget[] targets;
        var resumePulse = false;
        await using (var held = await mutex.AcquireAsync(ct))
        {
            await WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct);
            var actionKey = $"world:npc:pulse:action:{pilot.UserId}:{pulseActionId:N}";
            var plan = pulseInventory is null ? null
                : await pulseInventory.PlanAsync(pilot.AccountId, pulseActionId, ct);
            if (plan is null) pilot = await sessions.RestoreAsync(pilot, ct);
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (plan is not null)
            {
                if (plan.Status == "charged")
                    return new(await pulseInventory!.RefundUnstartedAsync(pilot.AccountId, pulseActionId, ct)
                        ? "refunded" : "duplicate", []);
                if (plan.Status != "started" || plan.MapId != pilot.MapId)
                    return new("duplicate", []);
                resumePulse = true;
                targets = plan.Targets;
            }
            else
            {
                if (sessions.CurrentHitPoints(pilot) <= 0) return new("destroyed_pilot", []);
                var position = sessions.CurrentPosition(pilot, clock.GetUtcNow());
                var map = WorldMapInstance.Starter(pilot.MapId);
                if (InsidePeaceZone(pilot.MapId, position) || map.Portals.Any(portal =>
                    DistanceSquared(position.X, position.Y, portal.X, portal.Y) <= 300L * 300))
                    return new("restricted", []);
                if (await cache.GetStringAsync(actionKey, ct) is not null)
                    return new("duplicate", []);
                var key = $"world:npc:pulse:{pilot.UserId}";
                var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                var saved = await cache.GetStringAsync(key, ct);
                if (long.TryParse(saved, out var last) &&
                    now - last < Math.Max(1, settings.PulseCooldownSeconds) * 1000L)
                    return new("cooldown", []);
                var targetIds = (await VisibleAsync(pilot.MapId, ct))
                    .Where(npc => DistanceSquared(position.X, position.Y, npc.X, npc.Y) <=
                        (long)settings.PulseRange * settings.PulseRange)
                    .Select(npc => npc.Id).ToArray();
                targets = new PulseTarget[targetIds.Length];
                for (var i = 0; i < targetIds.Length; i++)
                {
                    var storedVital = await cache.GetStringAsync(Key(pilot.MapId, targetIds[i]), ct);
                    var generation = storedVital is null ? 0
                        : JsonSerializer.Deserialize<Vital>(storedVital)!.Generation;
                    targets[i] = new PulseTarget(targetIds[i], generation);
                }
                if (pulseInventory is not null)
                {
                    var charge = await pulseInventory.ConsumeAsync(pilot.AccountId, pulseActionId, ct);
                    if (charge == PulseChargeResult.Duplicate)
                        return new(await pulseInventory.RefundUnstartedAsync(pilot.AccountId, pulseActionId, ct)
                            ? "refunded" : "duplicate", []);
                    if (charge == PulseChargeResult.Unavailable) return new("out_of_ammo", []);
                    if (!await pulseInventory.StartAsync(pilot.AccountId, pulseActionId,
                            pilot.MapId, targets, ct))
                        return new("duplicate", []);
                }
                await cache.SetStringAsync(key, now.ToString(), new DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, settings.PulseCooldownSeconds)) }, ct);
                await cache.SetStringAsync(actionKey, "1", new DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) }, ct);
            }
        }
        var hits = new List<WorldNpcAttack>(targets.Length);
        foreach (var target in targets)
        {
            var hit = await AttackAsync(pilot, target.Id, sessions, ct, fence, pilot.MapId,
                pulse: true, pulseActionId: pulseActionId, expectedGeneration: target.Generation,
                resumePulse: resumePulse);
            if (hit.Status == "hit") hits.Add(hit);
        }
        if (pulseInventory is not null)
        {
            await pulseInventory.CompleteAsync(pilot.AccountId, pulseActionId, ct);
            await ClearCompletedPulseActionAsync(pilot.MapId, targets, pulseActionId, ct);
            await pulseInventory.SettleAsync(pilot.AccountId, pulseActionId, ct);
        }
        return new("hit", hits);
    }

    // The map owner resumes saved effects even after a pilot disconnects or changes maps.
    // The plan fixes the original targets and account; no new charge or range check occurs.
    public async Task ReconcileStartedPulsesAsync(int mapId, WorldSessionStore sessions,
        CancellationToken ct, WorldLeaseFence? fence = null)
    {
        if (pulseInventory is null || WorldMapInstance.IsGate(mapId)) return;
        foreach (var action in await pulseInventory.StartedOnMapAsync(mapId, ct))
        {
            await WorldSessionStore.CheckLeaseAsync(mapId, fence, ct);
            var actor = new WorldPilot(0, string.Empty, mapId, 0, string.Empty, 0, 0)
                { AccountId = action.AccountId };
            await PulseAsync(actor, sessions, ct, fence, mapId, action.ActionId);
        }
    }

    public async Task ReconcileCompletedPulsesAsync(int mapId, CancellationToken ct,
        WorldLeaseFence? fence = null)
    {
        if (pulseInventory is null || WorldMapInstance.IsGate(mapId)) return;
        foreach (var action in await pulseInventory.CompletedOnMapAsync(mapId, ct))
        {
            await WorldSessionStore.CheckLeaseAsync(mapId, fence, ct);
            var plan = await pulseInventory.PlanAsync(action.AccountId, action.ActionId, ct);
            if (plan is not { Status: "completed" } || plan.MapId != mapId) continue;
            await ClearCompletedPulseActionAsync(mapId, plan.Targets, action.ActionId, ct);
            await pulseInventory.SettleAsync(action.AccountId, action.ActionId, ct);
        }
    }

    private async Task ClearCompletedPulseActionAsync(int mapId, PulseTarget[] targets,
        Guid actionId, CancellationToken ct)
    {
        await using var held = await mutex.AcquireAsync(ct);
        foreach (var target in targets)
        {
            var key = Key(mapId, target.Id);
            var stored = await cache.GetStringAsync(key, ct);
            if (stored is null) continue;
            var vital = JsonSerializer.Deserialize<Vital>(stored)!;
            if (vital.Generation != target.Generation ||
                vital.PulseActions?.Contains(actionId) != true) continue;
            await cache.SetStringAsync(key, JsonSerializer.Serialize(vital with
            {
                PulseActions = vital.PulseActions.Where(id => id != actionId).ToArray()
            }), Expiry, ct);
        }
    }

    public async Task<WorldNpcAttack> AttackAsync(WorldPilot pilot, int npcId,
        WorldSessionStore sessions, CancellationToken ct, WorldLeaseFence? fence = null,
        int? expectedMapId = null, bool rocket = false, bool pulse = false,
        Guid? pulseActionId = null, long? expectedGeneration = null, bool resumePulse = false,
        bool launcher = false)
    {
        if (pilot.AccountId == Guid.Empty) return new("invalid");
        WorldNpc? npc;
        WorldNpc? target = null;
        string key;
        var damage = 0;
        var missed = false;
        var dead = false;
        var retryReward = false;
        var savedGeneration = 0L;
        WorldNpcReward? reward = null;
        await using (var held = await mutex.AcquireAsync(ct))
        {
            await WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct);
            var mapId = pilot.MapId;
            if (!resumePulse) pilot = await sessions.RestoreAsync(pilot, ct);
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (fence is not null && pilot.MapId != mapId) throw new WorldLeaseLostException();
            if (!resumePulse && sessions.CurrentHitPoints(pilot) <= 0) return new("destroyed_pilot");
            key = Key(pilot.MapId, npcId);
            var stored = await cache.GetStringAsync(key, ct);
            var saved = stored is null ? null : JsonSerializer.Deserialize<Vital>(stored);
            npc = (await VisibleAsync(pilot.MapId, ct)).FirstOrDefault(n => n.Id == npcId)
                ?? (saved?.Destroyed == true ? saved.Snapshot : null);
            if (npc is null) return new("missing");
            var vital = saved ?? new Vital(npc.Hp, npc.Shield, false, Guid.Empty);
            if (expectedGeneration is not null && vital.Generation != expectedGeneration)
                return new("stale_target", npc);
            if (pulseActionId is { } pulseId && vital.PulseActions?.Contains(pulseId) == true &&
                !(vital.Destroyed && vital.Killer == pilot.AccountId))
                return new("duplicate", npc);
            savedGeneration = vital.Generation;
            if (vital.Destroyed)
            {
                if (vital.Killer != pilot.AccountId) return new("destroyed");
                retryReward = true;
                reward = vital.Reward;
            }
            else
            {
                var (x, y) = sessions.CurrentPosition(pilot, clock.GetUtcNow());
                if (pulse && !resumePulse && (InsidePeaceZone(pilot.MapId, (x, y)) ||
                    WorldMapInstance.Starter(pilot.MapId).Portals.Any(portal =>
                        DistanceSquared(x, y, portal.X, portal.Y) <= 300L * 300)))
                    return new("restricted", npc);
                var fireRange = pulse ? settings.PulseRange : rocket ? settings.RocketRange
                    : launcher ? settings.LauncherRange : variables is null ? settings.LaserRange
                    : await variables.NumberAsync("world.combat.fire_range", ct);
                if (!resumePulse && (long)(x - npc.X) * (x - npc.X) + (long)(y - npc.Y) * (y - npc.Y) >
                    (long)fireRange * fireRange) return new("distant", npc);
                if (!pulse && !rocket && !launcher && sessions.ActiveStats(pilot).LaserDamage <= 0)
                    return new("no_lasers", npc);
                if (pulse) damage = Math.Max(1, vital.Hp / 5);
                else if (launcher)
                {
                    var loadKey = $"world:launcher:{pilot.UserId}";
                    var storedLoad = await cache.GetStringAsync(loadKey, ct);
                    var capacity = Math.Clamp(settings.LauncherCapacity, 1, 20);
                    var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
                    var reloadMs = Math.Max(1, settings.LauncherReloadSeconds) * 1000L;
                    var load = storedLoad is null
                        ? new LauncherLoad(0, nowMs)
                        : JsonSerializer.Deserialize<LauncherLoad>(storedLoad)!;
                    if (storedLoad is null)
                        await cache.SetStringAsync(loadKey, JsonSerializer.Serialize(load), Expiry, ct);
                    var elapsed = Math.Max(0, nowMs - load.UpdatedAtUnixMs);
                    var added = Math.Min(capacity - Math.Clamp(load.Count, 0, capacity), elapsed / reloadMs);
                    var count = Math.Clamp(load.Count, 0, capacity) + (int)added;
                    if (count == 0) return new("reloading", npc);
                    // A volley is one target hit; each loaded rocket rolls independently.
                    for (var i = 0; i < count; i++)
                    {
                        if (Random.Shared.NextDouble() < Math.Clamp(settings.RocketMissChance, 0, 1)) continue;
                        var spread = Math.Clamp(settings.RocketDamageSpread, 0, 1);
                        damage += Math.Max(1, (int)Math.Round(Math.Max(1, settings.LauncherRocketDamage) *
                            (1 - spread + Random.Shared.NextDouble() * 2 * spread)));
                    }
                    missed = damage == 0;
                    await cache.SetStringAsync(loadKey,
                        JsonSerializer.Serialize(new LauncherLoad(0, nowMs)), Expiry, ct);
                }
                else if (rocket)
                {
                    var cooldownKey = $"world:rocket:{pilot.UserId}";
                    var last = await cache.GetStringAsync(cooldownKey, ct);
                    if (long.TryParse(last, out var lastMs) && clock.GetUtcNow().ToUnixTimeMilliseconds() - lastMs <
                        Math.Max(1, settings.RocketCooldownSeconds) * 1000L) return new("cooldown", npc);
                    await cache.SetStringAsync(cooldownKey, clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow =
                            TimeSpan.FromSeconds(Math.Max(1, settings.RocketCooldownSeconds)) }, ct);
                    damage = Math.Max(1, settings.RocketDamage);
                    missed = Random.Shared.NextDouble() < Math.Clamp(settings.RocketMissChance, 0, 1);
                    if (missed) damage = 0;
                    else
                    {
                        var spread = Math.Clamp(settings.RocketDamageSpread, 0, 1);
                        damage = Math.Max(1, (int)Math.Round(damage *
                            (1 - spread + Random.Shared.NextDouble() * 2 * spread)));
                    }
                }
                else
                {
                    var multiplier = variables is null ? 1 : await variables.NumberAsync("world.combat.laser_damage_multiplier", ct);
                    damage = Math.Max(1, (int)Math.Round(sessions.ActiveStats(pilot).LaserDamage * multiplier));
                    missed = Random.Shared.NextDouble() < Math.Clamp(settings.LaserMissChance, 0, 1);
                    if (missed) damage = 0;
                    else
                    {
                        var spread = Math.Clamp(settings.LaserDamageSpread, 0, 1);
                        damage = Math.Max(1, (int)Math.Round(damage *
                            (1 - spread + Random.Shared.NextDouble() * 2 * spread)));
                    }
                }
                if (!resumePulse) await sessions.MarkCombatAsync(pilot, ct);
                if (missed)
                {
                    await cache.SetStringAsync(key, JsonSerializer.Serialize(vital with
                    {
                        LastHitUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
                    }), Expiry, ct);
                    target = npc with { Hp = vital.Hp, Shield = vital.Shield };
                }
                else
                {
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
                    await cache.SetStringAsync(key, JsonSerializer.Serialize(vital with { Hp = hp, Shield = shield,
                        Destroyed = dead, Killer = dead ? pilot.AccountId : Guid.Empty,
                        Reward = reward, Snapshot = dead ? npc : null,
                        RespawnAtUnixMs = dead ? clock.GetUtcNow().ToUnixTimeMilliseconds() +
                            Math.Max(1, settings.RespawnSeconds) * 1000L : 0,
                        LastHitUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                        TargetUserId = !resumePulse && !dead ? pilot.UserId : vital.TargetUserId,
                        Retaliating = !resumePulse && !dead || vital.Retaliating,
                        PulseActions = pulseActionId is { } appliedId
                            ? [.. (vital.PulseActions ?? []), appliedId] : vital.PulseActions }), Expiry, ct);
                    observerSnapshots.TryRemove(pilot.MapId, out _);
                    target = npc with { Hp = hp, Shield = shield };
                }
            }
        }
        // Older cached kills have no reward snapshot; retain their retry behavior.
        if (retryReward)
        {
            reward ??= await RewardValuesAsync(npc!, ct);
            await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward), $"{key}:{savedGeneration}", ct);
            return new("destroyed");
        }
        if (dead)
        {
            await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward!), $"{key}:{savedGeneration}", ct);
        }
        if (!resumePulse) await sessions.BroadcastNpcDamageAsync(pilot, target!, damage, dead, reward, ct,
            rocket || launcher);
        return new("hit", target, damage, dead, reward);
    }

    // Called only by the map lease owner. The mesh mutex serializes target choice,
    // pursuit and damage with player attacks across world nodes.
    public async Task TickAggroAsync(int mapId, WorldSessionStore sessions, CancellationToken ct,
        WorldLeaseFence? fence = null)
    {
        await using var held = await mutex.AcquireAsync(ct);
        await WorldSessionStore.CheckLeaseAsync(mapId, fence, ct);
        var now = clock.GetUtcNow();
        var pilots = (await sessions.ConnectedAsync(mapId, ct))
            .Where(p => p.AccountId != Guid.Empty).ToArray();
        foreach (var spawn in await SpawnLiveAsync(mapId, now, ct))
        {
            var key = Key(mapId, spawn.Id);
            var saved = await cache.GetStringAsync(key, ct);
            var vital = saved is null ? new Vital(spawn.Hp, spawn.Shield, false, Guid.Empty)
                : JsonSerializer.Deserialize<Vital>(saved)!;
            if (vital.Destroyed)
            {
                if (vital.RespawnAtUnixMs == 0 || now.ToUnixTimeMilliseconds() < vital.RespawnAtUnixMs)
                    continue;
                var respawnMap = WorldMapInstance.Starter(mapId);
                // Move each new life away from its death position. Persist the position
                // with the life so another map owner observes the same spawn.
                var respawnX = (int)(((long)(vital.Snapshot?.X ?? vital.X ?? spawn.X) +
                    respawnMap.Width / 3 + 1) % (respawnMap.Width + 1L));
                var respawnY = (int)(((long)(vital.Snapshot?.Y ?? vital.Y ?? spawn.Y) +
                    respawnMap.Height / 3 + 1) % (respawnMap.Height + 1L));
                vital = new Vital(spawn.Hp, spawn.Shield, false, Guid.Empty,
                    X: respawnX, Y: respawnY, Generation: vital.Generation + 1,
                    LastFireUnixMs: vital.LastFireUnixMs,
                    PatrolOriginX: respawnX, PatrolOriginY: respawnY);
                await cache.SetStringAsync(key, JsonSerializer.Serialize(vital), Expiry, ct);
                observerSnapshots.TryRemove(mapId, out _);
            }
            var nowMs = now.ToUnixTimeMilliseconds();
            if (vital.Shield < spawn.MaxShield && vital.LastHitUnixMs > 0 &&
                nowMs - vital.LastHitUnixMs >= Math.Max(0, settings.NpcShieldRepairDelaySeconds) * 1000L &&
                nowMs - vital.LastShieldRepairUnixMs >= Math.Max(1, settings.NpcShieldRepairIntervalSeconds) * 1000L)
            {
                vital = vital with
                {
                    Shield = Math.Min(spawn.MaxShield, vital.Shield + Math.Max(1, spawn.MaxShield / 10)),
                    LastShieldRepairUnixMs = nowMs
                };
                await cache.SetStringAsync(key, JsonSerializer.Serialize(vital), Expiry, ct);
                observerSnapshots.TryRemove(mapId, out _);
            }
            var npc = spawn with { Hp = vital.Hp, Shield = vital.Shield,
                X = vital.X ?? spawn.X, Y = vital.Y ?? spawn.Y };
            var eligible = pilots.Where(p => sessions.CurrentHitPoints(p) > 0)
                .Select(p => (Pilot: p, Position: sessions.CurrentPosition(p, now)))
                .Where(p => !InsidePeaceZone(mapId, p.Position))
                .Select(p => (p.Pilot, p.Position, Distance: DistanceSquared(npc.X, npc.Y, p.Position.X, p.Position.Y)))
                .Where(p => p.Distance <= (long)settings.AggroRange * settings.AggroRange)
                .ToArray();
            // A pursuing NPC keeps its current valid target until that pilot leaves,
            // dies, enters the peace zone, or escapes aggro range.
            var target = eligible.FirstOrDefault(p => p.Pilot.UserId == vital.TargetUserId);
            if (target.Pilot is null)
                target = eligible.OrderBy(p => p.Distance).ThenBy(p => p.Pilot.UserId).FirstOrDefault();
            if (target.Pilot is null)
            {
                var patrolSpeed = (settings.MapTemplates.GetValueOrDefault(mapId) ?? settings.Templates)
                    .FirstOrDefault(t => t.Kind == npc.Kind)?.Speed ?? 0;
                if (patrolSpeed > 0 || vital.TargetUserId != 0)
                {
                    var patrolMap = WorldMapInstance.Starter(mapId);
                    var originX = vital.PatrolOriginX ?? npc.X;
                    var originY = vital.PatrolOriginY ?? npc.Y;
                    var leg = Math.Max(0, vital.PatrolLeg);
                    var endX = Math.Clamp(originX + (leg % 2 == 0 ? 1 : -1) *
                        (npc.Id % 2 == 0 ? -1 : 1) * patrolMap.Width / (8 + leg % 3),
                        0, patrolMap.Width);
                    var endY = Math.Clamp(originY + (leg % 3 == 0 ? 1 : -1) *
                        (npc.Id % 3 == 0 ? -1 : 1) * patrolMap.Height / (8 + leg % 2),
                        0, patrolMap.Height);
                    var outbound = vital.TargetUserId == 0 && vital.PatrolOutbound;
                    var goalX = outbound ? endX : originX;
                    var goalY = outbound ? endY : originY;
                    var patrolElapsed = vital.LastStepUnixMs == 0 ? 1d : Math.Clamp(
                        (nowMs - vital.LastStepUnixMs) / 1000d, 0, 5);
                    var patrolDx = goalX - npc.X;
                    var patrolDy = goalY - npc.Y;
                    var patrolDistance = Math.Sqrt((double)patrolDx * patrolDx + (double)patrolDy * patrolDy);
                    var patrolStep = Math.Min(patrolDistance, Math.Max(0, patrolSpeed) * patrolElapsed);
                    var reached = patrolStep >= patrolDistance;
                    var nextX = reached ? goalX : npc.X + (int)Math.Round(patrolDx * patrolStep / patrolDistance);
                    var nextY = reached ? goalY : npc.Y + (int)Math.Round(patrolDy * patrolStep / patrolDistance);
                    (nextX, nextY) = LimitRoundedStep(npc.X, npc.Y, nextX, nextY,
                        patrolStep);
                    var subpixelPatrol = patrolStep > 0 && !reached && nextX == npc.X && nextY == npc.Y;
                    vital = vital with
                    {
                        X = nextX,
                        Y = nextY,
                        TargetUserId = 0,
                        Retaliating = false,
                        LastStepUnixMs = subpixelPatrol ? vital.LastStepUnixMs : nowMs,
                        PatrolOriginX = originX,
                        PatrolOriginY = originY,
                        PatrolOutbound = reached ? !outbound : outbound,
                        PatrolLeg = reached && !outbound ? leg + 1 : leg
                    };
                    await cache.SetStringAsync(key, JsonSerializer.Serialize(vital), Expiry, ct);
                    observerSnapshots.TryRemove(mapId, out _);
                }
                continue;
            }
            var dx = target.Position.X - npc.X;
            var dy = target.Position.Y - npc.Y;
            var distance = Math.Sqrt(target.Distance);
            var template = (settings.MapTemplates.GetValueOrDefault(mapId) ?? settings.Templates)
                .FirstOrDefault(t => t.Kind == npc.Kind);
            var speed = template?.Speed ?? 0;
            var elapsed = vital.LastStepUnixMs == 0 ? 1d : Math.Clamp(
                (now.ToUnixTimeMilliseconds() - vital.LastStepUnixMs) / 1000d, 0, 5);
            var maxStep = Math.Max(0, speed) * elapsed;
            var step = Math.Clamp(distance - Math.Max(0, settings.NpcStandoffRange),
                -maxStep, maxStep);
            var map = WorldMapInstance.Starter(mapId);
            // At overlap there is no direction to retreat along; choose the
            // horizontal direction with room inside this map.
            var rawX = distance == 0
                ? npc.X + (npc.X < map.Width / 2 ? -1 : 1) * (int)Math.Round(step)
                : npc.X + (int)Math.Round(dx * step / distance);
            var rawY = distance == 0 ? npc.Y : npc.Y + (int)Math.Round(dy * step / distance);
            // Keep accumulating elapsed time when a real step is too small to
            // change either integer coordinate. Otherwise fast ticks can
            // prevent a slow NPC from ever moving.
            var x = Math.Clamp(rawX, 0, map.Width);
            var y = Math.Clamp(rawY, 0, map.Height);
            if (step < 0 && distance > 0 && (rawX != x || rawY != y))
            {
                // When the boundary clips a diagonal retreat, an edge step can
                // use the remaining speed to create more separation.
                var stride = (int)Math.Round(-step);
                var bestDistance = DistanceSquared(x, y, target.Position.X, target.Position.Y);
                foreach (var (offsetX, offsetY) in new[]
                    { (-stride, 0), (stride, 0), (0, -stride), (0, stride) })
                {
                    var candidateX = Math.Clamp(npc.X + offsetX, 0, map.Width);
                    var candidateY = Math.Clamp(npc.Y + offsetY, 0, map.Height);
                    var candidateDistance = DistanceSquared(candidateX, candidateY,
                        target.Position.X, target.Position.Y);
                    if (candidateDistance <= bestDistance) continue;
                    (x, y) = (candidateX, candidateY);
                    bestDistance = candidateDistance;
                }
            }
            (x, y) = LimitRoundedStep(npc.X, npc.Y, x, y, Math.Abs(step));
            var subpixelStep = step != 0 && x == npc.X && y == npc.Y &&
                rawX >= 0 && rawX <= map.Width && rawY >= 0 && rawY <= map.Height;
            npc = npc with { X = x, Y = y };
            var fire = (template?.Aggressive != false ||
                vital.Retaliating && vital.TargetUserId == target.Pilot.UserId) &&
                DistanceSquared(x, y, target.Position.X, target.Position.Y) <=
                (long)settings.NpcFireRange * settings.NpcFireRange &&
                now.ToUnixTimeMilliseconds() - vital.LastFireUnixMs >=
                Math.Max(1, settings.NpcFireCooldownSeconds) * 1000L;
            vital = vital with { X = x, Y = y, TargetUserId = target.Pilot.UserId,
                Retaliating = vital.Retaliating && vital.TargetUserId == target.Pilot.UserId,
                LastStepUnixMs = subpixelStep ? vital.LastStepUnixMs : nowMs,
                LastFireUnixMs = fire ? now.ToUnixTimeMilliseconds() : vital.LastFireUnixMs,
                PatrolOriginX = vital.PatrolOriginX ?? spawn.X,
                PatrolOriginY = vital.PatrolOriginY ?? spawn.Y };
            await cache.SetStringAsync(key, JsonSerializer.Serialize(vital), Expiry, ct);
            observerSnapshots.TryRemove(mapId, out _);
            if (fire)
            {
                var missed = Random.Shared.NextDouble() < Math.Clamp(settings.NpcMissChance, 0, 1);
                var spread = Math.Clamp(settings.NpcDamageSpread, 0, 1);
                var multiplier = 1 - spread + Random.Shared.NextDouble() * 2 * spread;
                var damage = missed ? 0 : Math.Max(1,
                    (int)Math.Round(Math.Max(1, settings.NpcDamage) * multiplier));
                await sessions.ApplyNpcDamageAsync(target.Pilot, npc.Id, damage, ct,
                    settings.PilotShieldAbsorption);
            }
        }
    }

    private static long DistanceSquared(int x, int y, int otherX, int otherY) =>
        (long)(x - otherX) * (x - otherX) + (long)(y - otherY) * (y - otherY);

    private static (int X, int Y) LimitRoundedStep(int fromX, int fromY,
        int toX, int toY, double distance)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        while ((long)dx * dx + (long)dy * dy > distance * distance + 1e-9)
        {
            if (Math.Abs(dx) > Math.Abs(dy)) dx -= Math.Sign(dx);
            else dy -= Math.Sign(dy);
        }
        return (fromX + dx, fromY + dy);
    }

    private static bool InsidePeaceZone(int mapId, (int X, int Y) position)
    {
        var station = WorldMapInstance.Starter(mapId).HomeStation;
        return DistanceSquared(position.X, position.Y, station.X, station.Y) <=
            (long)station.PeaceRadius * station.PeaceRadius;
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
