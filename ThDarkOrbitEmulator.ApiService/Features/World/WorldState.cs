using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Emulator world geometry pending a redacted native map fixture.
public sealed record WorldStation(int Id, int X, int Y, int PeaceRadius);
public sealed record WorldPortal(int Id, int X, int Y, int DestinationMapId, int ArrivalX, int ArrivalY,
    int JumpRadius = 500, bool Working = true);
public enum WorldRespawnLocation { Base, DeathLocation, NearestPortal }
public sealed record WorldMapInstance(int MapId, int FactionId, int Width, int Height,
    WorldStation HomeStation, IReadOnlyList<WorldPortal> Portals)
{
    public bool AllowsPortalRepair => !IsGate(MapId) && Portals.Any(portal => portal.Working);
    public WorldPortal? NearestWorkingPortal(int x, int y) => Portals
        .Where(portal => portal.Working)
        .MinBy(portal => (long)(portal.X - x) * (portal.X - x)
            + (long)(portal.Y - y) * (portal.Y - y));
    public static bool IsGate(int mapId) => mapId is >= 101 and <= 120;
    public static string? GateId(int mapId) => IsGate(mapId)
        ? new[] { "alpha", "beta", "gamma", "delta", "epsilon" }[(mapId - 101) % 5] : null;
    public static int GateMap(int factionId, int gateIndex) => 101 + (factionId - 1) * 5 + gateIndex;

    public static WorldMapInstance Starter(int mapId) => mapId switch
    {
        >= 1 and <= 16 => Create(mapId, (mapId - 1) / 4 + 1, (mapId - 1) % 4 != 0),
        >= 101 and <= 120 => CreateGate(mapId),
        _ => throw new ArgumentOutOfRangeException(nameof(mapId))
    };

    private static WorldMapInstance CreateGate(int mapId)
    {
        var faction = (mapId - 101) / 5 + 1;
        return new(mapId, faction, 21000, 13100, new WorldStation(mapId * 100, 19000, 1300, 0),
            [new WorldPortal(mapId * 100 + 1, 2000, 1300, (faction - 1) * 4 + 1, 18000, 6500)]);
    }

    // Only the x-1 (1, 5, 9) and x-8 (20, 24, 28) maps have a home station.
    public bool HasHomeStation => MapId is 1 or 5 or 9 or 20 or 24 or 28;

    private const int MapWidth = 21000, MapHeight = 13100;

    // Jumpgates from the client's spacemap-config.xml: (gate key, map, destination map, tooltip x, tooltip y).
    // Tooltip coordinates span 315x210 and are scaled to map units; every gate has a reverse gate.
    private static readonly (int Key, int Map, int To, int X, int Y)[] Gates =
    [
        (1, 1, 2, 293, 177),
        (2, 2, 1, 31, 30),
        (3, 2, 3, 293, 30),
        (5, 2, 4, 293, 177),
        (4, 3, 2, 31, 177),
        (7, 3, 7, 293, 30),
        (27, 3, 4, 293, 177),
        (6, 4, 2, 31, 30),
        (11, 4, 12, 293, 177),
        (28, 4, 3, 293, 30),
        (31, 4, 13, 301, 92),
        (16, 5, 6, 31, 177),
        (10, 6, 7, 31, 177),
        (15, 6, 5, 293, 30),
        (17, 6, 8, 293, 177),
        (8, 7, 3, 31, 177),
        (9, 7, 6, 293, 30),
        (25, 7, 8, 293, 177),
        (18, 8, 6, 31, 30),
        (19, 8, 11, 31, 177),
        (26, 8, 7, 293, 30),
        (33, 8, 14, 158, 184),
        (24, 9, 10, 31, 30),
        (14, 10, 12, 31, 30),
        (22, 10, 11, 293, 30),
        (23, 10, 9, 293, 177),
        (20, 11, 8, 31, 30),
        (21, 11, 10, 293, 177),
        (30, 11, 12, 31, 177),
        (12, 12, 4, 31, 30),
        (13, 12, 10, 293, 177),
        (29, 12, 11, 293, 30),
        (35, 12, 15, 158, 23),
        (32, 13, 4, 23, 92),
        (37, 13, 14, 293, 30),
        (42, 13, 15, 293, 177),
        (43, 13, 16, 166, 104),
        (34, 14, 8, 158, 23),
        (38, 14, 13, 31, 177),
        (39, 14, 15, 293, 177),
        (45, 14, 16, 166, 104),
        (36, 15, 12, 301, 92),
        (40, 15, 14, 31, 30),
        (41, 15, 13, 31, 177),
        (47, 15, 16, 166, 104),
        (44, 16, 13, 152, 104),
        (46, 16, 14, 173, 91),
        (48, 16, 15, 173, 112),
    ];

    private static (int X, int Y) GatePosition((int Key, int Map, int To, int X, int Y) gate) =>
        ((int)Math.Round(gate.X * MapWidth / 315.0 / 100) * 100, (int)Math.Round(gate.Y * MapHeight / 210.0 / 100) * 100);

    // Ships arrive on the reverse gate of the destination map.
    private static WorldPortal Portal((int Key, int Map, int To, int X, int Y) gate)
    {
        var (x, y) = GatePosition(gate);
        var (arrivalX, arrivalY) = GatePosition(Gates.Single(g => g.Map == gate.To && g.To == gate.Map));
        return new WorldPortal(gate.Key, x, y, gate.To, arrivalX, arrivalY);
    }

    // Local Galaxy Gate combat keeps its entrance IDs without advertising synthetic gates to the Unity client.
    public static IReadOnlyList<WorldPortal> GateEntrances(int mapId)
    {
        if (mapId is not (1 or 5 or 9 or 13)) return [];
        var faction = (mapId - 1) / 4 + 1;
        return Enumerable.Range(0, 5).Select(index => new WorldPortal(mapId * 100 + 3 + index,
            17500 + index * 500, 6500, GateMap(faction, index), 2000, 1300)).ToArray();
    }

    private static WorldMapInstance Create(int map, int faction, bool adjacent) =>
        new(map, faction, MapWidth, MapHeight,
            new WorldStation(map * 100, adjacent ? 19000 : 2000, 1300, 1000),
            Gates.Where(gate => gate.Map == map).Select(Portal).ToArray());
}

public sealed record WorldShipStats(int Speed, long MaxHitPoints, long MaxShield, int CargoCapacity,
    int LaserDamage, IReadOnlyList<LocalEquipmentAssignment> Equipment);

public static class WorldShipStatCalculator
{
    // Values are local balancing defaults. The shop currently has no authoritative combat stats.
    private static readonly IReadOnlyDictionary<string, int> LaserDamage = new Dictionary<string, int>
    {
        ["laser_lf2"] = 100, ["laser_lf3"] = 150
    };
    private static readonly IReadOnlyDictionary<string, int> ShieldStrength = new Dictionary<string, int>
    {
        ["shield_sg3n_a03"] = 3000, ["shield_sg3n_b02"] = 5000
    };
    private static readonly IReadOnlyDictionary<string, int> SpeedBonus = new Dictionary<string, int>
    {
        ["generator_g3n_1010"] = 10, ["generator_g3n_2010"] = 20,
        ["generator_g3n_3010"] = 30, ["generator_g3n_3310"] = 40
    };

    public static WorldShipStats Calculate(int slot, LocalEquipmentState equipment, LocalPilotSkillState skills,
        IReadOnlyList<EconomyListing>? catalog = null, int configuration = 1, int? baseSpeed = null,
        string? shipId = null)
    {
        if (configuration is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(configuration));
        var spec = shipId is null ? LocalHangars.Spec(slot) : LocalHangars.SpecForShip(shipId, slot);
        var assigned = equipment.Assignments.Where(a => a.HangarSlot == slot && a.Configuration == configuration)
            .Where(a => a.Quantity > 0 && equipment.Inventory.Any(i => i.Id == a.ItemId && i.Quantity >= a.Quantity))
            .ToArray();
        int Value(string itemId, string field, IReadOnlyDictionary<string, int> defaults)
        {
            var listing = catalog?.FirstOrDefault(item => item.ItemId == itemId);
            if (listing is not null && listing.Stats.ValueKind == JsonValueKind.Object
                && listing.Stats.TryGetProperty(field, out var stat) && stat.ValueKind == JsonValueKind.Number
                && stat.TryGetInt32(out var numeric)
                && numeric is >= 0 and <= 1000000)
                return numeric;
            return defaults.GetValueOrDefault(itemId);
        }
        long Sum(string field, IReadOnlyDictionary<string, int> defaults) => assigned.Sum(a =>
            (long)Value(a.ItemId, field, defaults) * a.Quantity);
        var hullBonus = LocalPilotSkillStore.Find("hull")!.Bonus(skills.Ranks.GetValueOrDefault("hull"));
        var shieldBonus = LocalPilotSkillStore.Find("shields")!.Bonus(skills.Ranks.GetValueOrDefault("shields"));
        var laserBonus = LocalPilotSkillStore.Find("lasers")!.Bonus(skills.Ranks.GetValueOrDefault("lasers"));
        return new WorldShipStats(checked((baseSpeed ?? spec.Speed) + (int)Sum("speed", SpeedBonus)),
            spec.HitPoints * (100L + hullBonus) / 100,
            Sum("shield", ShieldStrength) * (100 + shieldBonus) / 100,
            spec.Cargo, checked((int)(Sum("damage", LaserDamage) * (100 + laserBonus) / 100)), assigned);
    }
}

public sealed record WorldPosition(int MapId, int X, int Y);
public sealed record WorldHealEvent(string Id, long Amount, long Hull);
public sealed record WorldRepairBotEvent(string Id, bool Active);
public sealed record WorldRuntimeSnapshot(int MapId, int Configuration, long Shield,
    long LastSwitchUnixMilliseconds, WorldTravel? Travel, long? HitPoints = null,
    long LastHazardUnixMilliseconds = 0, bool RadiationWarning = false, bool RepairBotActive = false,
    string? DeathId = null, bool IsInvisible = false, long LastCloakActivationUnixMs = 0,
    string? LastHealId = null, long LastHealAmount = 0, long LastHealHull = 0,
    WorldHealEvent[]? RecentHeals = null, WorldRepairBotEvent[]? RecentRepairBotStates = null);
public sealed record WorldJumpResult(WorldPilot Pilot, int PortalId, string? TargetNodeId,
    string? TargetTcpHostPort);
public sealed record WorldSwitchResult(bool Changed, int CooldownRemainingMs, WorldMoveResult? Move);
public sealed record WorldLeaseFence(IServerMeshRegistry Registry, string NodeId, long Epoch);
public sealed class WorldLeaseLostException() : Exception("The map lease changed before the world command was applied.");
public sealed class WorldMapChangedException() : Exception("The pilot left the map before the movement command was applied.");
public sealed class WorldMoveChangedException() : Exception("The pilot's destination changed before the movement command was applied.");
public sealed class WorldPilotDestroyedException() : Exception("The pilot must respawn before acting.");
public sealed class WorldConfigurationCooldownException(int remainingMs) : Exception("Configuration switch is cooling down.")
{
    public int RemainingMs { get; } = remainingMs;
}
public sealed class WorldCloakCooldownException(int remainingMs) : Exception("Cloak activation is cooling down.")
{
    public int RemainingMs { get; } = remainingMs;
}
public sealed class WorldCloakBlockedException() : Exception("Cloak activation is blocked on this map.");

public sealed record WorldPresence(WorldPilot Pilot, DateTimeOffset SeenAtUtc);
public sealed record WorldRemoteMove(WorldMoveResult Move, WorldTravel? Travel);

public sealed class WorldSessionStore(IDistributedCache cache, IServerMeshMutex? meshMutex = null,
    IRuntimeVariableRegistry? variables = null, TimeProvider? clock = null, EconomyStore? economy = null,
    IWorldRepairWallet? repairWallet = null, WorldPortalOperations? portalOperations = null,
    IReadOnlySet<int>? combatRestrictedMaps = null, IReadOnlySet<int>? cloakBlockedMaps = null,
    IConfiguration? configuration = null)
{
    private readonly IReadOnlySet<int>? configuredCombatRestrictedMaps = combatRestrictedMaps
        ?? configuration?.GetSection("World:CombatRestrictedMaps").Get<int[]>()?.ToHashSet();
    private readonly IReadOnlySet<int> configuredCloakBlockedMaps = cloakBlockedMaps
        ?? configuration?.GetSection("World:CloakBlockedMaps").Get<int[]>()?.ToHashSet()
        ?? new HashSet<int>();
    public Task<WorldMapInstance> MapAsync(int mapId, CancellationToken ct) => portalOperations is null
        ? Task.FromResult(WorldMapInstance.Starter(mapId)) : portalOperations.MapAsync(mapId, ct);
    private readonly TimeProvider combatClock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, WorldPilot>> connected = new();
    private readonly ConcurrentDictionary<long, WorldConnection> connections = new();
    private readonly ConcurrentDictionary<long, WorldTravel> travel = new();
    private readonly ConcurrentDictionary<long, WorldTravel> announcedTravel = new();
    private readonly ConcurrentDictionary<long, (int X, int Y)> announcedPosition = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, WorldTravel>> remoteAnnounced = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, (int X, int Y)>> remotePositions = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> remoteVisible = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, bool>> remoteCloaks = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>> remoteHeals = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>> connectedHeals = new();
    private readonly ConcurrentDictionary<long, string> announcedOwnHeals = new();
    private readonly ConcurrentDictionary<long, string> announcedRepairBots = new();
    // Map-level fanout: every idle observer on this node shares one presence + saved-state read per map.
    public static readonly TimeSpan RemoteSnapshotTtl = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PresenceHeartbeatInterval = TimeSpan.FromSeconds(15);
    private sealed record RemoteEntry(WorldPresence Presence, string? Saved, WorldRuntimeSnapshot? Runtime);
    private sealed record RemoteSnapshot(DateTimeOffset ReadAtUtc, IReadOnlyList<RemoteEntry> Entries);
    private readonly ConcurrentDictionary<int, Lazy<Task<RemoteSnapshot>>> remoteSnapshots = new();
    private readonly ConcurrentDictionary<long, (int MapId, DateTimeOffset WrittenAtUtc)> presenceHeartbeats = new();
    private readonly ConcurrentDictionary<long, int> configurations = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> lastSwitches = new();
    private readonly ConcurrentDictionary<long, long> shields = new();
    private readonly ConcurrentDictionary<long, long> hitPoints = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> lastHazards = new();
    private readonly ConcurrentDictionary<long, bool> radiationWarnings = new();
    private readonly ConcurrentDictionary<long, bool> repairBots = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> movementGates = new();
    public int ActivePilotCount => connected.Values.Sum(pilots => pilots.Count);
    // Mesh tick: every due heartbeat and departure on one map shares one roster read-modify-write.
    public async Task RefreshLocalPresenceAsync(CancellationToken ct)
    {
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        var present = new Dictionary<int, List<WorldPilot>>();
        var absent = new Dictionary<int, List<long>>();
        foreach (var pilot in connected.Values.SelectMany(p => p.Values).ToArray())
        {
            var current = pilot;
            var runtimeJson = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
            var runtimeOnAnotherMap = false;
            var hasRuntimeMap = false;
            if (runtimeJson is not null)
            {
                try
                {
                    var runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson);
                    hasRuntimeMap = runtime is not null && IsValidWorldMap(runtime.MapId);
                    runtimeOnAnotherMap = hasRuntimeMap && runtime!.MapId != pilot.MapId;
                    if (hasRuntimeMap && runtime!.MapId == pilot.MapId && runtime.IsInvisible != pilot.IsInvisible)
                    {
                        current = pilot with { IsInvisible = runtime.IsInvisible };
                        RefreshConnectedPilot(current);
                        await BroadcastCloakAsync(current, ct);
                    }
                }
                catch (JsonException) { }
            }
            var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
            var positionOnAnotherMap = false;
            if (saved is not null)
            {
                try
                {
                    var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                    positionOnAnotherMap = position is not null && IsValidWorldMap(position.MapId)
                        && position.MapId != pilot.MapId;
                }
                catch (JsonException) { }
            }
            var hasMapOwner = int.TryParse(await cache.GetStringAsync(MapKey(pilot.UserId), ct), out var ownerMap)
                && IsValidWorldMap(ownerMap);
            var mapOnAnotherMap = hasMapOwner && ownerMap != pilot.MapId;
            // Map ownership is written with transfers; the runtime map is the next
            // best signal when a prior position is restored out of order.
            if (hasMapOwner ? mapOnAnotherMap : hasRuntimeMap ? runtimeOnAnotherMap : positionOnAnotherMap)
            {
                if (connected.TryGetValue(pilot.MapId, out var local))
                    local.TryRemove(pilot.UserId, out _);
                (absent.TryGetValue(pilot.MapId, out var gone) ? gone : absent[pilot.MapId] = []).Add(pilot.UserId);
                continue;
            }
            if (HeartbeatDue(current))
                (present.TryGetValue(current.MapId, out var due) ? due : present[current.MapId] = []).Add(current);
        }
        foreach (var mapId in present.Keys.Union(absent.Keys).ToArray())
            await WritePresenceBatchAsync(mapId, present.GetValueOrDefault(mapId) ?? [],
                absent.GetValueOrDefault(mapId) ?? [], ct);
    }
    private static string Key(long userId) => $"world:position:{userId}";
    private static string MapKey(long userId) => $"world:map:{userId}";
    private static bool IsValidWorldMap(int mapId) => mapId is >= 1 and <= 16 || WorldMapInstance.IsGate(mapId);
    private static string RuntimeKey(long userId) => $"world:runtime:{userId}";
    private static string CombatKey(long userId) => $"world:combat:{userId}";
    private static string CloakCooldownKey(long userId) => $"world:cloak-cooldown:{userId}";
    private static string RepairProtectionKey(long userId) => $"world:repair-protection:{userId}";
    private static string DeathKey(long userId) => $"world:death:{userId}";
    private async Task<string?> DeathIdAsync(long userId, CancellationToken ct,
        Guid accountId = default)
    {
        var deathId = await cache.GetStringAsync(DeathKey(userId), ct);
        if (deathId is not null) return deathId;
        var saved = await cache.GetStringAsync(RuntimeKey(userId), ct);
        if (saved is not null)
        {
            try { deathId = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(saved)?.DeathId; }
            catch (JsonException) { /* The durable record can still restore this life. */ }
        }
        if (deathId is null && accountId != Guid.Empty && repairWallet is not null)
            deathId = await repairWallet.FindDeathAsync(accountId, ct);
        if (deathId is not null)
            await cache.SetStringAsync(DeathKey(userId), deathId,
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
        return deathId;
    }
    private static string RespawnCooldownKey(long userId, WorldRespawnLocation location) =>
        $"world:respawn-cooldown:{userId}:{location}";
    private static string ShieldRecoveryKey(long userId) => $"world:shield-recovery:{userId}";
    private static string HullRecoveryKey(long userId) => $"world:hull-recovery:{userId}";
    private static string PresenceKey(int mapId) => $"world:presence:map:{mapId}";
    private static readonly TimeSpan PortalCombatWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloakActivationCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RepairProtectionWindow = TimeSpan.FromSeconds(5);
    private async Task<bool> RepairProtectedAsync(long userId, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(RepairProtectionKey(userId), ct);
        return long.TryParse(saved, out var milliseconds)
            && combatClock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                < RepairProtectionWindow;
    }
    private static TimeSpan RespawnCooldown(WorldRespawnLocation location) => location switch
    {
        WorldRespawnLocation.DeathLocation => TimeSpan.FromSeconds(30),
        WorldRespawnLocation.NearestPortal => TimeSpan.FromSeconds(15),
        _ => TimeSpan.Zero
    };

    public async Task MarkCombatAsync(WorldPilot pilot, CancellationToken ct)
    {
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        await MarkCombatWhileLockedAsync(pilot, ct);
    }

    // NPC attack paths already hold the mesh lock while selecting and damaging a target.
    internal async Task MarkCombatWhileLockedAsync(WorldPilot pilot, CancellationToken ct)
    {
        // Combat can arrive with a target captured before a map transfer.
        pilot = await RefreshAsync(pilot, ct);
        await cache.SetStringAsync(CombatKey(pilot.UserId), combatClock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
        await SetRepairBotAsync(pilot, false, ct);
        var runtimeJson = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
        if (runtimeJson is null) return;
        WorldRuntimeSnapshot? runtime;
        try { runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson); }
        catch (JsonException) { return; }
        if (runtime?.IsInvisible != true) return;
        await cache.SetStringAsync(RuntimeKey(pilot.UserId),
            JsonSerializer.Serialize(runtime with { IsInvisible = false }),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
        var visible = pilot with { IsInvisible = false };
        RefreshConnectedPilot(visible);
        await WritePresenceAsync(visible, true, ct);
        await BroadcastCloakAsync(visible, ct);
    }

    private async Task<bool> PortalCombatLockedAsync(WorldPilot pilot, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(CombatKey(pilot.UserId), ct);
        return long.TryParse(saved, out var milliseconds)
            && combatClock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) < PortalCombatWindow;
    }

    // The map owner advances pilot recovery. Both timestamps and vitals live in shared state
    // so an owner handoff cannot grant an extra tick or erase a recently applied hit.
    public async Task RecoverShieldsAsync(int mapId, CancellationToken ct, WorldLeaseFence? fence = null)
    {
        foreach (var connectedPilot in await ConnectedAsync(mapId, ct))
        {
            var gate = movementGates.GetOrAdd(connectedPilot.UserId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
                await CheckLeaseAsync(mapId, fence, ct);
                var pilot = await RefreshAsync(connectedPilot, ct);
                if (pilot.MapId != mapId || CurrentHitPoints(pilot) <= 0) continue;
                var maxShield = ActiveStats(pilot).MaxShield;
                var shield = CurrentShield(pilot);
                if (maxShield <= 0 || shield >= maxShield) continue;
                var now = combatClock.GetUtcNow();
                var combat = await cache.GetStringAsync(CombatKey(pilot.UserId), ct);
                if (long.TryParse(combat, out var combatMs) &&
                    now - DateTimeOffset.FromUnixTimeMilliseconds(combatMs) < PortalCombatWindow) continue;
                var recovery = await cache.GetStringAsync(ShieldRecoveryKey(pilot.UserId), ct);
                if (long.TryParse(recovery, out var recoveryMs) &&
                    now - DateTimeOffset.FromUnixTimeMilliseconds(recoveryMs) < TimeSpan.FromSeconds(1)) continue;
                shields[pilot.UserId] = Math.Min(maxShield, shield + Math.Max(1, maxShield / 25));
                await PersistRuntimeAsync(pilot, ct);
                await cache.SetStringAsync(ShieldRecoveryKey(pilot.UserId), now.ToUnixTimeMilliseconds().ToString(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
            }
            finally { gate.Release(); }
        }
    }

    // Field repair runs for a living pilot after combat has ended, including in flight.
    // The shared tick marker prevents a lease handoff from granting an extra repair.
    public async Task RecoverHullAsync(int mapId, CancellationToken ct, WorldLeaseFence? fence = null)
    {
        foreach (var connectedPilot in await ConnectedAsync(mapId, ct))
        {
            var gate = movementGates.GetOrAdd(connectedPilot.UserId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
                await CheckLeaseAsync(mapId, fence, ct);
                var pilot = await RefreshAsync(connectedPilot, ct);
                if (pilot.MapId != mapId) continue;
                var now = combatClock.GetUtcNow();
                var hitPointMaximum = ActiveStats(pilot).MaxHitPoints;
                var current = CurrentHitPoints(pilot);
                if (current <= 0 || current >= hitPointMaximum || RadiationWarning(pilot.UserId))
                {
                    await SetRepairBotAsync(pilot, false, ct);
                    continue;
                }
                var combat = await cache.GetStringAsync(CombatKey(pilot.UserId), ct);
                if (long.TryParse(combat, out var combatMs) &&
                    now - DateTimeOffset.FromUnixTimeMilliseconds(combatMs) < PortalCombatWindow)
                {
                    await SetRepairBotAsync(pilot, false, ct);
                    continue;
                }
                await SetRepairBotAsync(pilot, true, ct);
                var recovery = await cache.GetStringAsync(HullRecoveryKey(pilot.UserId), ct);
                if (long.TryParse(recovery, out var recoveryMs) &&
                    now - DateTimeOffset.FromUnixTimeMilliseconds(recoveryMs) < TimeSpan.FromSeconds(1)) continue;
                hitPoints[pilot.UserId] = Math.Min(hitPointMaximum, current + Math.Max(1, hitPointMaximum / 40));
                if (hitPoints[pilot.UserId] >= hitPointMaximum)
                    await SetRepairBotAsync(pilot, false, ct);
                await PersistRuntimeAsync(pilot, ct, healAmount: hitPoints[pilot.UserId] - current);
                await cache.SetStringAsync(HullRecoveryKey(pilot.UserId), now.ToUnixTimeMilliseconds().ToString(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
                await AnnounceHullHealAsync(pilot, ct);
            }
            finally { gate.Release(); }
        }
    }

    private async Task SetRepairBotAsync(WorldPilot pilot, bool active, CancellationToken ct)
    {
        if (repairBots.GetValueOrDefault(pilot.UserId) == active) return;
        repairBots[pilot.UserId] = active;
        var eventId = Guid.NewGuid().ToString("N");
        await PersistRuntimeAsync(pilot, ct, repairBotEventId: eventId);
        if (connections.TryGetValue(pilot.UserId, out var connection) && connection.Pilot.MapId == pilot.MapId)
        {
            try { await ObserveOwnRepairBotAsync(pilot, connection, ct); }
            catch (IOException) { }
        }
    }

    public bool RepairBotActive(long userId) => repairBots.GetValueOrDefault(userId);

    public async Task PrimeOwnRepairBotAsync(WorldPilot pilot, CancellationToken ct)
    {
        var runtime = await ReadOwnHealRuntimeAsync(pilot, ct);
        announcedRepairBots[pilot.UserId] = runtime?.RecentRepairBotStates?.LastOrDefault()?.Id ?? string.Empty;
    }

    public async Task ObserveOwnRepairBotAsync(WorldPilot pilot, WorldConnection connection, CancellationToken ct)
    {
        await connection.RepairBotDeliveryGate.WaitAsync(ct);
        try
        {
            var saved = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
            if (saved is null) return;
            WorldRuntimeSnapshot? runtime;
            try { runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(saved); }
            catch (JsonException) { return; }
            if (runtime is null || runtime.MapId != pilot.MapId) return;
            announcedRepairBots.TryGetValue(pilot.UserId, out var announced);
            var history = runtime.RecentRepairBotStates ?? [];
            if (history.Length == 0 && repairBots.GetValueOrDefault(pilot.UserId) != runtime.RepairBotActive)
            {
                await connection.SendAsync(WorldCommandId.RepairBotStateCommand, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt64(w, pilot.UserId);
                    w.Write(runtime.RepairBotActive);
                }), ct);
            }
            var index = Array.FindLastIndex(history, entry => entry.Id == announced);
            var pending = announced is null || index < 0 && announced.Length > 0
                ? history.TakeLast(1) : history.Skip(index + 1);
            foreach (var entry in pending)
            {
                await connection.SendAsync(WorldCommandId.RepairBotStateCommand, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt64(w, pilot.UserId);
                    w.Write(entry.Active);
                }), ct);
                announcedRepairBots[pilot.UserId] = entry.Id;
            }
            repairBots[pilot.UserId] = runtime.RepairBotActive;
        }
        finally { connection.RepairBotDeliveryGate.Release(); }
    }

    public async Task PrimeOwnHealAsync(WorldPilot pilot, CancellationToken ct)
    {
        var runtime = await ReadOwnHealRuntimeAsync(pilot, ct);
        if (runtime?.LastHealId is { } id) announcedOwnHeals[pilot.UserId] = id;
        else announcedOwnHeals[pilot.UserId] = string.Empty;
    }

    public async Task ObserveOwnHealAsync(WorldPilot pilot, WorldConnection connection, CancellationToken ct)
    {
        await connection.HealDeliveryGate.WaitAsync(ct);
        try
        {
            var runtime = await ReadOwnHealRuntimeAsync(pilot, ct);
            if (runtime is null) return;
            announcedOwnHeals.TryGetValue(pilot.UserId, out var announced);
            foreach (var heal in UnannouncedHeals(runtime, announced))
            {
                await SendHealAsync(connection, pilot.UserId, heal, ct);
                announcedOwnHeals[pilot.UserId] = heal.Id;
            }
        }
        finally { connection.HealDeliveryGate.Release(); }
    }

    private static IEnumerable<WorldHealEvent> UnannouncedHeals(WorldRuntimeSnapshot runtime, string? announced)
    {
        var history = runtime.RecentHeals is { Length: > 0 } recent ? recent
            : runtime.LastHealId is { } id
                ? [new WorldHealEvent(id, runtime.LastHealAmount, runtime.LastHealHull)]
                : [];
        if (announced is null) return history.Length > 0 ? [history[^1]] : [];
        if (announced.Length == 0) return history;
        var index = Array.FindLastIndex(history, heal => heal.Id == announced);
        return index >= 0 ? history.Skip(index + 1) : history;
    }

    private static Task SendHealAsync(WorldConnection connection, long userId, WorldHealEvent heal,
        CancellationToken ct) => connection.SendAsync(WorldCommandId.ShipHealCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, userId);
            WorldPayload.WriteInt64(w, heal.Amount);
            WorldPayload.WriteInt64(w, heal.Hull);
        }), ct);

    private async Task<WorldRuntimeSnapshot?> ReadOwnHealRuntimeAsync(WorldPilot pilot, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
        if (saved is null) return null;
        try
        {
            var runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(saved);
            return runtime?.MapId == pilot.MapId ? runtime : null;
        }
        catch (JsonException) { return null; }
    }

    // The map owner continues the hazard while the pilot remains at the boundary.
    public async Task TickRadiationAsync(int mapId, CancellationToken ct, WorldLeaseFence? fence = null)
    {
        foreach (var connectedPilot in await ConnectedAsync(mapId, ct))
        {
            var gate = movementGates.GetOrAdd(connectedPilot.UserId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
                await CheckLeaseAsync(mapId, fence, ct);
                var pilot = await RefreshAsync(connectedPilot, ct);
                if (pilot.MapId != mapId || CurrentHitPoints(pilot) <= 0)
                    continue;
                var map = WorldMapInstance.Starter(mapId);
                var inside = WorldMovement.IsInsideBoundary(CurrentPosition(pilot, combatClock.GetUtcNow()), map);
                if (!RadiationWarning(pilot.UserId) && inside)
                    continue;
                if (inside && WorldMovement.IsInsideBoundary(Destination(pilot), map))
                {
                    radiationWarnings[pilot.UserId] = false;
                    await PersistRuntimeAsync(pilot, ct);
                    await SendRadiationWarningAsync(pilot.UserId, false, ct);
                    continue;
                }
                if (inside) continue;
                var startedRadiating = !RadiationWarning(pilot.UserId);
                radiationWarnings[pilot.UserId] = true;
                await ApplyBoundaryDamageAsync(pilot, combatClock.GetUtcNow(), ct);
                if (startedRadiating && CurrentHitPoints(pilot) > 0)
                    await SendRadiationWarningAsync(pilot.UserId, true, ct);
            }
            finally { gate.Release(); }
        }
    }
    private static string UnityUiKey(long userId) => $"world:unity-ui:{userId}";

    public Task<string?> UnityUiSettingsAsync(long userId, CancellationToken ct) =>
        cache.GetStringAsync(UnityUiKey(userId), ct);
    public Task SaveUnityUiSettingsAsync(long userId, string json, CancellationToken ct) =>
        cache.SetStringAsync(UnityUiKey(userId), json, ct);

    // Saved 2D/3D choice as the login display2d value: 1 = 3D, 2 = 2D, 0 = never chosen.
    public async Task<int> UnityDisplay2dAsync(long userId, CancellationToken ct) =>
        await cache.GetStringAsync(UnityDisplayKey(userId), ct) switch { "1" => 1, "2" => 2, _ => 0 };
    public Task SaveUnityDisplay2dAsync(long userId, bool use2d, CancellationToken ct) =>
        cache.SetStringAsync(UnityDisplayKey(userId), use2d ? "2" : "1", ct);
    private static string UnityDisplayKey(long userId) => $"world:unity-display2d:{userId}";

    public IReadOnlyList<WorldPilot> Connected(int mapId) => connected.TryGetValue(mapId, out var pilots)
        ? pilots.Values.ToArray() : [];

    public WorldPilot? Find(long userId) => connected.Values
        .SelectMany(pilots => pilots.Values).FirstOrDefault(pilot => pilot.UserId == userId);

    public Guid MoveId(WorldPilot pilot) => travel.GetValueOrDefault(pilot.UserId)?.MoveId ?? Guid.Empty;

    // Refresh on every request: another API replica may have moved or switched this pilot.
    public Task<WorldPilot> GetOrEnterAsync(WorldPilot pilot, CancellationToken ct) =>
        EnterAsync(pilot, ct);

    public async Task<IReadOnlyList<WorldPilot>> ConnectedAsync(int mapId, CancellationToken ct)
    {
        var index = await ReadPresenceAsync(mapId, ct);
        var live = index.Values.Where(item => DateTimeOffset.UtcNow - item.SeenAtUtc < TimeSpan.FromMinutes(2));
        var result = new List<WorldPilot>();
        foreach (var entry in live)
        {
            var pilot = entry.Pilot;
            // A restored position can predate a completed transfer. The independent
            // map entry is written with each position under the transfer lock.
            var mapText = await cache.GetStringAsync(MapKey(pilot.UserId), ct);
            var hasMapOwner = int.TryParse(mapText, out var ownerMap) && IsValidWorldMap(ownerMap);
            if (hasMapOwner && ownerMap != mapId) continue;
            var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
            var positionOnAnotherMap = false;
            if (saved is not null)
            {
                try
                {
                    var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                    var hasValidPosition = position is not null && IsValidWorldMap(position.MapId);
                    positionOnAnotherMap = hasValidPosition && position!.MapId != mapId;
                    if (!hasValidPosition || positionOnAnotherMap)
                        saved = null;
                }
                catch (JsonException) { saved = null; }
            }
            if (saved is not null)
            {
                try
                {
                    // Presence can outlive a transfer if its old roster write is interrupted.
                    // Use a matching saved position for coordinates; the map owner
                    // remains authoritative when an older position is restored.
                    var location = JsonSerializer.Deserialize<WorldPosition>(saved);
                    if (location is not null && location.MapId != mapId) continue;
                    var moving = JsonSerializer.Deserialize<WorldTravel>(saved);
                    if (moving?.MapId == mapId && moving.Speed > 0)
                    {
                        var position = moving.PositionAt(DateTimeOffset.UtcNow);
                        pilot = pilot with { X = position.X, Y = position.Y };
                    }
                    else
                    {
                        var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                        if (position?.MapId == mapId)
                            pilot = pilot with { X = position.X, Y = position.Y };
                    }
                }
                catch (JsonException) { }
            }
            var runtime = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
            var hasRuntimeMap = false;
            if (runtime is not null)
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtime);
                    hasRuntimeMap = snapshot is not null && IsValidWorldMap(snapshot.MapId);
                    // The runtime map also identifies a completed transfer when the
                    // position key has been evicted but an old roster entry survives.
                    if (!hasMapOwner && hasRuntimeMap
                        && snapshot!.MapId != mapId) continue;
                    if (hasRuntimeMap && snapshot!.MapId == mapId)
                        pilot = pilot with { IsInvisible = snapshot.IsInvisible };
                }
                catch (JsonException) { }
            }
            if (!hasMapOwner && !hasRuntimeMap && positionOnAnotherMap) continue;
            result.Add(pilot);
        }
        return result;
    }

    // Browser state polls share the per-map remote snapshot instead of reading every pilot's saved
    // position per request. Admin/mesh views keep using the uncached ConnectedAsync.
    public async Task<IReadOnlyList<WorldPilot>> VisiblePilotsAsync(int mapId, CancellationToken ct,
        Guid? accountId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<WorldPilot>();
        foreach (var (entry, saved, _) in (await RemoteSnapshotAsync(mapId, ct)).Entries)
        {
            if (now - entry.SeenAtUtc >= TimeSpan.FromMinutes(2)) continue;
            if (WorldMapInstance.IsGate(mapId) && entry.Pilot.AccountId != accountId) continue;
            var pilot = entry.Pilot with { MapId = mapId };
            if (saved is null)
            {
                // Local TCP pilots were not read from the cache; their in-memory travel is authoritative.
                var (x, y) = CurrentPosition(pilot, now);
                result.Add(pilot with { X = x, Y = y });
                continue;
            }
            var moving = ParseTravel(saved, mapId);
            if (moving is not null)
            {
                var position = moving.PositionAt(now);
                result.Add(pilot with { X = position.X, Y = position.Y });
                continue;
            }
            try
            {
                var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                // A saved position on another map means the pilot already left this map.
                if (position is null) result.Add(pilot);
                else if (position.MapId == mapId) result.Add(pilot with { X = position.X, Y = position.Y });
            }
            catch (JsonException) { result.Add(pilot); }
        }
        return result;
    }

    public async Task<WorldPilot?> TeleportAsync(long userId, int mapId, int x, int y,
        bool repair, CancellationToken ct)
    {
        var map = WorldMapInstance.Starter(mapId);
        if (userId <= 0 || x < 0 || x > map.Width || y < 0 || y > map.Height)
            throw new ArgumentOutOfRangeException(nameof(x));
        var gate = movementGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            WorldPilot? pilot = null;
            for (var id = 1; id <= 16 && pilot is null; id++)
            {
                var index = await ReadPresenceAsync(id, ct);
                if (index.TryGetValue(userId, out var presence)
                    && DateTimeOffset.UtcNow - presence.SeenAtUtc < TimeSpan.FromMinutes(2))
                    pilot = presence.Pilot;
            }
            if (pilot is null) return null;
            pilot = await RefreshAsync(pilot, ct);
            var next = pilot with { MapId = mapId, X = x, Y = y };
            travel.TryRemove(userId, out _);
            radiationWarnings[userId] = false;
            if (repair)
            {
                hitPoints[userId] = ActiveStats(next).MaxHitPoints;
                shields[userId] = ActiveStats(next).MaxShield;
            }
            await PersistAsync(next, ct);
            await PersistRuntimeAsync(next, ct);
            await WritePresenceAsync(pilot, false, ct);
            await WritePresenceAsync(next, true, ct);
            if (connected.TryGetValue(pilot.MapId, out var old)) old.TryRemove(userId, out _);
            connected.GetOrAdd(mapId, _ => new ConcurrentDictionary<long, WorldPilot>())[userId] = next;
            if (connections.TryGetValue(userId, out var connection)) connection.Pilot = next;
            return next;
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<long, WorldPresence>> ReadPresenceAsync(int mapId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(PresenceKey(mapId), ct);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<Dictionary<long, WorldPresence>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task UpdatePresenceAsync(WorldPilot pilot, bool present, CancellationToken ct)
    {
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        await WritePresenceAsync(pilot, present, ct);
    }

    private bool HeartbeatDue(WorldPilot pilot) =>
        !presenceHeartbeats.TryGetValue(pilot.UserId, out var heartbeat)
        || heartbeat.MapId != pilot.MapId
        || (clock ?? TimeProvider.System).GetUtcNow() - heartbeat.WrittenAtUtc >= PresenceHeartbeatInterval;

    private Task HeartbeatPresenceCoreAsync(WorldPilot pilot, CancellationToken ct) =>
        HeartbeatDue(pilot) ? WritePresenceAsync(pilot, true, ct) : Task.CompletedTask;

    private Task WritePresenceAsync(WorldPilot pilot, bool present, CancellationToken ct) => present
        ? WritePresenceBatchAsync(pilot.MapId, [pilot], [], ct)
        : WritePresenceBatchAsync(pilot.MapId, [], [pilot.UserId], ct);

    private async Task WritePresenceBatchAsync(int mapId, IReadOnlyCollection<WorldPilot> present,
        IReadOnlyCollection<long> absent, CancellationToken ct)
    {
        var index = await ReadPresenceAsync(mapId, ct);
        foreach (var stale in index.Where(item => DateTimeOffset.UtcNow - item.Value.SeenAtUtc
            >= TimeSpan.FromMinutes(2)).Select(item => item.Key).ToArray()) index.Remove(stale);
        foreach (var userId in absent) index.Remove(userId);
        foreach (var pilot in present) index[pilot.UserId] = new(pilot, DateTimeOffset.UtcNow);
        await cache.SetStringAsync(PresenceKey(mapId), JsonSerializer.Serialize(index),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(3) }, ct);
        var stamp = (clock ?? TimeProvider.System).GetUtcNow();
        foreach (var pilot in present) presenceHeartbeats[pilot.UserId] = (mapId, stamp);
        foreach (var userId in absent)
            if (presenceHeartbeats.TryGetValue(userId, out var heartbeat) && heartbeat.MapId == mapId)
                presenceHeartbeats.TryRemove(userId, out _);
    }

    public int Configuration(long userId) => configurations.GetValueOrDefault(userId, 1);
    public WorldShipStats ActiveStats(WorldPilot pilot) => Configuration(pilot.UserId) == 2
        ? pilot.ConfigurationTwoStats ?? pilot.Stats with { Speed = pilot.ConfigurationTwoSpeed }
        : pilot.Stats;
    private async Task<int> EffectiveSpeedAsync(WorldPilot pilot, WorldShipStats stats, CancellationToken ct)
    {
        if (variables is null) return stats.Speed;
        var key = pilot.ShipId == "ship_phoenix" || pilot.ShipId != "ship_pusat-plus" && pilot.HangarSlot == 2
            ? "world.ship.phoenix_base_speed" : "world.ship.pusat_plus_base_speed";
        var currentBase = (int)await variables.NumberAsync(key, ct);
        var loadedBase = pilot.BaseSpeedAtLoad > 0 ? pilot.BaseSpeedAtLoad
            : LocalHangars.SpecForShip(pilot.ShipId, pilot.HangarSlot).Speed;
        var multiplier = await variables.NumberAsync("world.movement.speed_multiplier", ct);
        return Math.Max(1, (int)Math.Round((stats.Speed + currentBase - loadedBase) * multiplier));
    }
    public async Task<WorldShipStats> LiveStatsAsync(WorldPilot pilot, CancellationToken ct)
    {
        var stats = ActiveStats(pilot);
        if (variables is null) return stats;
        return stats with
        {
            Speed = await EffectiveSpeedAsync(pilot, stats, ct),
            LaserDamage = Math.Max(0, (int)Math.Round(stats.LaserDamage *
                await variables.NumberAsync("world.combat.laser_damage_multiplier", ct)))
        };
    }
    public long CurrentShield(WorldPilot pilot) => shields.GetOrAdd(pilot.UserId, ActiveStats(pilot).MaxShield);
    public long CurrentHitPoints(WorldPilot pilot) => hitPoints.GetOrAdd(pilot.UserId, ActiveStats(pilot).MaxHitPoints);
    public void SetCurrentShield(WorldPilot pilot, long value)
    {
        shields[pilot.UserId] = Math.Clamp(value, 0, ActiveStats(pilot).MaxShield);
        // This synchronous API is used by combat callers; publish the change before another
        // replica can serve a state poll. Movement/configuration use the async write path.
        PersistRuntimeAsync(pilot, CancellationToken.None).GetAwaiter().GetResult();
    }
    public IReadOnlyList<WorldCompanionPosition> CompanionPositions(WorldPilot pilot, DateTimeOffset? at = null)
    {
        var (x, y) = CurrentPosition(pilot, at);
        return WorldCompanions.At(Configuration(pilot.UserId) == 2
            ? pilot.ConfigurationTwoCompanions : pilot.Companions, x, y);
    }

    public async Task<WorldPilot> EnterAsync(WorldPilot pilot, CancellationToken ct)
    {
        pilot = await RefreshAsync(pilot, ct);
        pilot = await RestorePendingDeathPositionAsync(pilot, ct);
        pilot = await CompleteStagedRepairAsync(pilot, ct);
        await ReconcilePendingDeathAsync(pilot, ct);
        pilot = await ResumeChargedRepairAsync(pilot, ct);
        if (!travel.ContainsKey(pilot.UserId)) await PersistAsync(pilot, ct);
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        // Cloak may change on another node while entry waits for the shared lock.
        pilot = await RefreshAsync(pilot, ct);
        foreach (var map in connected.Values)
            if (map.TryRemove(pilot.UserId, out var previous) && previous.MapId != pilot.MapId)
                await WritePresenceAsync(previous, false, ct);
        connected.GetOrAdd(pilot.MapId, _ => new ConcurrentDictionary<long, WorldPilot>())[pilot.UserId] = pilot;
        await HeartbeatPresenceCoreAsync(pilot, ct);
        return pilot;
    }

    // Local world ability state is shared with map owners through the runtime snapshot.
    public async Task<int> CloakCooldownRemainingMsAsync(long userId, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(CloakCooldownKey(userId), ct);
        if (!long.TryParse(saved, out var milliseconds))
        {
            var runtimeJson = await cache.GetStringAsync(RuntimeKey(userId), ct);
            if (runtimeJson is null) return 0;
            try { milliseconds = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson)?.LastCloakActivationUnixMs ?? 0; }
            catch (JsonException) { return 0; }
            if (milliseconds <= 0) return 0;
        }
        var remaining = CloakActivationCooldown -
            (combatClock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalMilliseconds) : 0;
    }

    public async Task<WorldPilot> SetCloakAsync(WorldPilot pilot, bool active, CancellationToken ct)
    {
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        var requestedMap = pilot.MapId;
        pilot = await RefreshAsync(pilot, ct);
        if (pilot.MapId != requestedMap) throw new WorldMapChangedException();
        if (CurrentHitPoints(pilot) <= 0) throw new WorldPilotDestroyedException();
        if (pilot.IsInvisible == active) return pilot;
        if (active)
        {
            if (configuredCloakBlockedMaps.Contains(pilot.MapId))
                throw new WorldCloakBlockedException();
            var remaining = await CloakCooldownRemainingMsAsync(pilot.UserId, ct);
            if (remaining > 0) throw new WorldCloakCooldownException(remaining);
        }
        pilot = pilot with { IsInvisible = active };
        await PersistRuntimeAsync(pilot, ct, cloakOverride: active,
            cloakActivatedAtUnixMs: active ? combatClock.GetUtcNow().ToUnixTimeMilliseconds() : null);
        if (active)
            await cache.SetStringAsync(CloakCooldownKey(pilot.UserId),
                combatClock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CloakActivationCooldown }, ct);
        RefreshConnectedPilot(pilot);
        await WritePresenceAsync(pilot, true, ct);
        await BroadcastCloakAsync(pilot, ct);
        return pilot;
    }

    private async Task<WorldPilot> ResumeChargedRepairAsync(WorldPilot pilot, CancellationToken ct)
    {
        if (CurrentHitPoints(pilot) == 0 && repairWallet is not null && pilot.AccountId != Guid.Empty)
        {
            var deathId = await DeathIdAsync(pilot.UserId, ct, pilot.AccountId);
            if (deathId is not null)
            {
                foreach (var paidLocation in new[]
                         { WorldRespawnLocation.DeathLocation, WorldRespawnLocation.NearestPortal })
                {
                    if (await repairWallet.FindChargeAsync(pilot.AccountId, paidLocation, deathId, ct)
                        is not null)
                    {
                        pilot = await RespawnAsync(pilot, paidLocation, ct) ?? pilot;
                        break;
                    }
                }
            }
        }
        return pilot;
    }

    private async Task<WorldPilot> CompleteStagedRepairAsync(WorldPilot pilot, CancellationToken ct)
    {
        if (repairWallet is not null && pilot.AccountId != Guid.Empty
            && await repairWallet.FindPendingRepairAsync(pilot.AccountId, ct) is { } repair)
        {
            var deathId = await repairWallet.FindPendingDeathAsync(pilot.AccountId, ct);
            if (deathId is not null)
            {
                pilot = pilot with { MapId = repair.MapId, X = repair.X, Y = repair.Y };
                hitPoints[pilot.UserId] = Math.Clamp(repair.HitPoints, 1, ActiveStats(pilot).MaxHitPoints);
                shields[pilot.UserId] = Math.Clamp(repair.Shield, 0, ActiveStats(pilot).MaxShield);
                await ClearRepairRuntimeAsync(pilot.UserId, ct);
                if (repair.RepairedAt is { } repairedAt
                    && combatClock.GetUtcNow() - repairedAt < RepairProtectionWindow)
                    await cache.SetStringAsync(RepairProtectionKey(pilot.UserId),
                        repairedAt.ToUnixTimeMilliseconds().ToString(),
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
                var paid = false;
                foreach (var paidLocation in new[]
                         { WorldRespawnLocation.DeathLocation, WorldRespawnLocation.NearestPortal })
                    if (await repairWallet.FindChargeAsync(pilot.AccountId, paidLocation, deathId, ct)
                        is { } charge)
                    {
                        pilot = pilot with { Uridium = charge.Balance };
                        paid = true;
                        break;
                    }
                await PersistAsync(pilot, ct);
                if (!paid) await cache.RemoveAsync(DeathKey(pilot.UserId), ct);
                await PersistRuntimeAsync(pilot, ct, clearDeathId: !paid);
                await repairWallet.CompleteDeathAsync(pilot.AccountId, deathId, ct);
            }
        }
        return pilot;
    }

    // Read persisted travel/map before a TCP node admits a socket. This does not create presence.
    public async Task<WorldPilot> RestoreAsync(WorldPilot pilot, CancellationToken ct)
    {
        pilot = await RefreshAsync(pilot, ct);
        var deathPosition = await RestorePendingDeathPositionAsync(pilot, ct);
        if (deathPosition != pilot)
        {
            pilot = deathPosition;
            await PersistAsync(pilot, ct);
        }
        pilot = await CompleteStagedRepairAsync(pilot, ct);
        await ReconcilePendingDeathAsync(pilot, ct);
        return await ResumeChargedRepairAsync(pilot, ct);
    }

    private async Task<WorldPilot> RestorePendingDeathPositionAsync(WorldPilot pilot,
        CancellationToken ct)
    {
        if (repairWallet is null || pilot.AccountId == Guid.Empty
            || await repairWallet.FindPendingDeathPositionAsync(pilot.AccountId, ct) is not { } position
            || WorldMapInstance.Starter(position.MapId) is not { } map
            || position.X < 0 || position.X > map.Width
            || position.Y < 0 || position.Y > map.Height) return pilot;
        return pilot with { MapId = position.MapId, X = position.X, Y = position.Y };
    }

    private async Task ReconcilePendingDeathAsync(WorldPilot pilot, CancellationToken ct)
    {
        // A request can arrive on another replica before the lethal hit's cache write.
        if (CurrentHitPoints(pilot) <= 0 || repairWallet is null || pilot.AccountId == Guid.Empty
            || await repairWallet.FindPendingDeathAsync(pilot.AccountId, ct) is null) return;
        hitPoints[pilot.UserId] = 0;
        shields[pilot.UserId] = 0;
        await ClearRepairRuntimeAsync(pilot.UserId, ct);
        await PersistRuntimeAsync(pilot, ct);
    }

    // A remote admin can teleport a pilot while this process still holds the old TCP socket.
    // The independently written map key takes precedence if a stale position is restored.
    public async Task<int?> SavedMapAsync(long userId, CancellationToken ct)
    {
        if (int.TryParse(await cache.GetStringAsync(MapKey(userId), ct), out var ownerMap)
            && IsValidWorldMap(ownerMap))
            return ownerMap;
        var runtimeJson = await cache.GetStringAsync(RuntimeKey(userId), ct);
        if (runtimeJson is not null)
        {
            try
            {
                var runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson);
                if (runtime is not null && IsValidWorldMap(runtime.MapId)) return runtime.MapId;
            }
            catch (JsonException) { }
        }
        var saved = await cache.GetStringAsync(Key(userId), ct);
        if (saved is null) return null;
        try
        {
            var position = JsonSerializer.Deserialize<WorldPosition>(saved);
            return position is not null && IsValidWorldMap(position.MapId)
                ? position.MapId : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<WorldPilot> RefreshAsync(WorldPilot pilot, CancellationToken ct)
    {
        travel.TryRemove(pilot.UserId, out _);
        // A prior position write may reappear after a transfer. Use the same
        // map precedence as TCP routing before restoring coordinates or vitals.
        if (await SavedMapAsync(pilot.UserId, ct) is { } currentMap)
            pilot = pilot with { MapId = currentMap };
        var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
        if (saved is not null)
        {
            try
            {
                var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                if (position is not null && position.MapId == pilot.MapId
                    && WorldMapInstance.Starter(position.MapId) is { } map
                    && position.X >= 0 && position.X <= map.Width && position.Y >= 0 && position.Y <= map.Height)
                    pilot = pilot with { MapId = position.MapId, X = position.X, Y = position.Y };
            }
            catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { }
        }
        var runtimeJson = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
        if (runtimeJson is not null)
        {
            try
            {
                var runtime = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson);
                if (runtime is not null && runtime.MapId == pilot.MapId && runtime.Configuration is 1 or 2)
                {
                    configurations[pilot.UserId] = runtime.Configuration;
                    shields[pilot.UserId] = Math.Clamp(runtime.Shield, 0, ActiveStats(pilot).MaxShield);
                    hitPoints[pilot.UserId] = Math.Clamp(runtime.HitPoints ?? ActiveStats(pilot).MaxHitPoints,
                        0, ActiveStats(pilot).MaxHitPoints);
                    if (runtime.LastHazardUnixMilliseconds > 0)
                        lastHazards[pilot.UserId] = DateTimeOffset.FromUnixTimeMilliseconds(runtime.LastHazardUnixMilliseconds);
                    radiationWarnings[pilot.UserId] = runtime.RadiationWarning;
                    repairBots[pilot.UserId] = runtime.RepairBotActive;
                    pilot = pilot with { IsInvisible = runtime.IsInvisible };
                    if (runtime.LastSwitchUnixMilliseconds > 0)
                        lastSwitches[pilot.UserId] = DateTimeOffset.FromUnixTimeMilliseconds(runtime.LastSwitchUnixMilliseconds);
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { }
        }
        if (saved is not null)
        {
            try
            {
                var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                if (position is not null && position.MapId == pilot.MapId
                    && position.X is >= 0 and <= 21000 && position.Y is >= 0 and <= 13100)
                    pilot = pilot with { X = position.X, Y = position.Y };
                var prior = JsonSerializer.Deserialize<WorldTravel>(saved);
                if (prior is not null && prior.MapId == pilot.MapId && prior.Speed > 0
                    && prior.StartX is >= 0 and <= 21000 && prior.StartY is >= 0 and <= 13100
                    && prior.TargetX is >= 0 and <= 21000 && prior.TargetY is >= 0 and <= 13100)
                {
                    pilot = pilot with { X = prior.PositionAt(DateTimeOffset.UtcNow).X,
                        Y = prior.PositionAt(DateTimeOffset.UtcNow).Y };
                    travel[pilot.UserId] = prior;
                }
            }
            catch (JsonException) { }
        }
        return pilot;
    }

    public async Task LeaveAsync(WorldPilot pilot, WorldConnection? connection, CancellationToken ct)
    {
        // An older socket must never clear the newer connection's presence.
        if (connection is not null && connections.TryGetValue(pilot.UserId, out var current)
            && !ReferenceEquals(current, connection)) return;
        Leave(pilot, connection);
        await UpdatePresenceAsync(pilot, false, ct);
    }

    public async Task PersistAsync(WorldPilot pilot, CancellationToken ct)
    {
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) };
        await cache.SetStringAsync(Key(pilot.UserId),
            JsonSerializer.Serialize(new WorldPosition(pilot.MapId, pilot.X, pilot.Y)), options, ct);
        // Keep map ownership independent of the position and runtime snapshots so
        // observers can reject an interrupted old-map roster after both are lost.
        await cache.SetStringAsync(MapKey(pilot.UserId), pilot.MapId.ToString(), options, ct);
    }

    public void Leave(WorldPilot pilot, WorldConnection? connection = null)
    {
        if (connection is not null)
        {
            var removed = ((ICollection<KeyValuePair<long, WorldConnection>>)connections)
                .Remove(new(pilot.UserId, connection));
            if (!removed) return; // a newer connection owns this pilot
        }
        if (connected.TryGetValue(pilot.MapId, out var pilots))
            ((ICollection<KeyValuePair<long, WorldPilot>>)pilots).Remove(new(pilot.UserId, pilot));
        travel.TryRemove(pilot.UserId, out _);
        announcedTravel.TryRemove(pilot.UserId, out _);
        announcedPosition.TryRemove(pilot.UserId, out _);
        remoteAnnounced.TryRemove(pilot.UserId, out _);
        remoteVisible.TryRemove(pilot.UserId, out _);
        remoteCloaks.TryRemove(pilot.UserId, out _);
        remoteHeals.TryRemove(pilot.UserId, out _);
        connectedHeals.TryRemove(pilot.UserId, out _);
        foreach (var heals in connectedHeals.Values) heals.TryRemove(pilot.UserId, out _);
        announcedOwnHeals.TryRemove(pilot.UserId, out _);
        announcedRepairBots.TryRemove(pilot.UserId, out _);
        remotePositions.TryRemove(pilot.UserId, out _);
        configurations.TryRemove(pilot.UserId, out _);
        lastSwitches.TryRemove(pilot.UserId, out _);
        shields.TryRemove(pilot.UserId, out _);
        hitPoints.TryRemove(pilot.UserId, out _);
        lastHazards.TryRemove(pilot.UserId, out _);
        radiationWarnings.TryRemove(pilot.UserId, out _);
        repairBots.TryRemove(pilot.UserId, out _);
    }

    public void Attach(WorldConnection connection)
    {
        connections[connection.Pilot.UserId] = connection;
        announcedRepairBots.TryAdd(connection.Pilot.UserId, string.Empty);
    }

    public void RefreshConnectedPilot(WorldPilot pilot)
    {
        if (connected.TryGetValue(pilot.MapId, out var onMap) && onMap.ContainsKey(pilot.UserId))
            onMap[pilot.UserId] = pilot;
    }

    public async Task PrimeObservedTravelAsync(WorldPilot pilot, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
        var current = ParseTravel(saved, pilot.MapId);
        if (current is null) announcedTravel.TryRemove(pilot.UserId, out _);
        else announcedTravel[pilot.UserId] = current;
        announcedPosition[pilot.UserId] = current is null
            ? SavedStationaryPosition(saved, pilot.MapId) ?? (pilot.X, pilot.Y)
            : (current.TargetX, current.TargetY);
    }

    // A browser request may mutate this pilot on another API replica while its TCP socket is idle here.
    public async Task<WorldMoveResult?> ObserveExternalMoveAsync(WorldPilot pilot, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
        var current = ParseTravel(saved, pilot.MapId);
        if (current is null)
        {
            var stationary = SavedStationaryPosition(saved, pilot.MapId);
            if (stationary is not null && announcedPosition.TryGetValue(pilot.UserId, out var previousPosition)
                && previousPosition != stationary.Value)
            {
                return new WorldMoveResult(pilot.UserId, stationary.Value.X, stationary.Value.Y, 0, false);
            }
            announcedTravel.TryRemove(pilot.UserId, out _);
            return null;
        }
        if (announcedTravel.TryGetValue(pilot.UserId, out var previous) && previous == current)
            return null;
        travel[pilot.UserId] = current;
        var position = current.PositionAt(DateTimeOffset.UtcNow);
        var distance = Math.Sqrt(Math.Pow(current.TargetX - position.X, 2)
            + Math.Pow(current.TargetY - position.Y, 2));
        return new WorldMoveResult(pilot.UserId, current.TargetX, current.TargetY,
            WorldMovement.DurationMilliseconds(distance, current.Speed), false) { Travel = current };
    }

    private static (int X, int Y)? SavedStationaryPosition(string? saved, int mapId)
    {
        if (saved is null) return null;
        try
        {
            var position = JsonSerializer.Deserialize<WorldPosition>(saved);
            return position?.MapId == mapId ? (position.X, position.Y) : null;
        }
        catch (JsonException) { return null; }
    }

    // A socket's visible set is local presentation state; shared presence remains the source of truth.
    public async Task ObserveRemotePilotsAsync(WorldPilot observer, WorldConnection connection,
        CancellationToken ct)
    {
        var snapshot = await RemoteSnapshotAsync(observer.MapId, ct);
        await ObserveConnectedHealsAsync(observer, connection, snapshot, ct);
        var visible = remoteVisible.GetOrAdd(observer.UserId, _ => new());
        var cloaks = remoteCloaks.GetOrAdd(observer.UserId, _ => new());
        var heals = remoteHeals.GetOrAdd(observer.UserId, _ => new());
        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<long>();
        foreach (var (entry, saved, runtime) in snapshot.Entries)
        {
            var remote = entry.Pilot;
            if (remote.UserId == observer.UserId || connections.ContainsKey(remote.UserId)
                || WorldMapInstance.IsGate(observer.MapId) && remote.AccountId != observer.AccountId
                || now - entry.SeenAtUtc >= TimeSpan.FromMinutes(2)) continue;
            if (saved is not null)
            {
                try
                {
                    var travel = JsonSerializer.Deserialize<WorldTravel>(saved);
                    if (travel is { Speed: > 0 })
                    {
                        if (travel.MapId != observer.MapId) continue;
                        var position = travel.PositionAt(now);
                        remote = remote with { X = position.X, Y = position.Y };
                    }
                    else
                    {
                        var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                        if (position is not null)
                        {
                            if (position.MapId != observer.MapId) continue;
                            remote = remote with { X = position.X, Y = position.Y };
                        }
                    }
                }
                catch (JsonException) { }
            }
            await connection.RemoteMoveDeliveryGate.WaitAsync(ct);
            try
            {
                // Cloak changes can arrive while this map snapshot is still cached.
                // Read current membership before sending updates to an announced pilot.
                var currentPresence = await ReadPresenceAsync(observer.MapId, ct);
                if (!currentPresence.TryGetValue(remote.UserId, out var currentEntry)
                    || DateTimeOffset.UtcNow - currentEntry.SeenAtUtc >= TimeSpan.FromMinutes(2))
                    continue;
                if (!visible.ContainsKey(remote.UserId))
                {
                    // A departed pilot may have returned while this map snapshot is cached.
                    // Build the first creation from current presence and position.
                    remote = currentEntry.Pilot;
                    var currentSaved = await cache.GetStringAsync(Key(remote.UserId), ct);
                    if (currentSaved is not null)
                    {
                        try
                        {
                            var currentTravel = JsonSerializer.Deserialize<WorldTravel>(currentSaved);
                            if (currentTravel is { Speed: > 0 })
                            {
                                if (currentTravel.MapId != observer.MapId) continue;
                                var position = currentTravel.PositionAt(DateTimeOffset.UtcNow);
                                remote = remote with { X = position.X, Y = position.Y };
                            }
                            else
                            {
                                var position = JsonSerializer.Deserialize<WorldPosition>(currentSaved);
                                if (position is not null)
                                {
                                    if (position.MapId != observer.MapId) continue;
                                    remote = remote with { X = position.X, Y = position.Y };
                                }
                            }
                        }
                        catch (JsonException) { }
                    }
                }
                else remote = remote with { IsInvisible = currentEntry.Pilot.IsInvisible };
                present.Add(remote.UserId);
                if (visible.TryAdd(remote.UserId, 0))
                {
                    heals[remote.UserId] = runtime?.LastHealId ?? string.Empty;
                    remotePositions.GetOrAdd(observer.UserId, _ => new())[remote.UserId] = (remote.X, remote.Y);
                    try
                    {
                        await connection.SendAsync(WorldCommandId.PilotCreateCommand, WorldPayload.Encode(w =>
                        {
                            WorldPayload.WriteInt64(w, remote.UserId);
                            WorldPayload.WriteString(w, remote.UserName);
                            WorldPayload.WriteString(w, remote.ShipId);
                            WorldPayload.WriteInt32(w, remote.FactionId);
                            WorldPayload.WriteInt32(w, remote.MapId);
                            WorldPayload.WriteInt32(w, remote.X);
                            WorldPayload.WriteInt32(w, remote.Y);
                        }), ct);
                    }
                    catch
                    {
                        visible.TryRemove(remote.UserId, out _);
                        heals.TryRemove(remote.UserId, out _);
                        remotePositions.GetOrAdd(observer.UserId, _ => new()).TryRemove(remote.UserId, out _);
                        throw;
                    }
                }
                await connection.HealDeliveryGate.WaitAsync(ct);
                try
                {
                    // Membership may come from a cached map snapshot while heals keep advancing.
                    var current = await ReadOwnHealRuntimeAsync(remote, ct);
                    if (current is not null)
                    {
                        heals.TryGetValue(remote.UserId, out var announcedHeal);
                        foreach (var heal in UnannouncedHeals(current, announcedHeal))
                        {
                            await SendHealAsync(connection, remote.UserId, heal, ct);
                            heals[remote.UserId] = heal.Id;
                        }
                    }
                }
                finally { connection.HealDeliveryGate.Release(); }
                if (!cloaks.TryGetValue(remote.UserId, out var announced) || announced != remote.IsInvisible)
                {
                    await connection.SendAsync(WorldCommandId.CloakStateCommand, WorldPayload.Encode(w =>
                    {
                        WorldPayload.WriteInt64(w, remote.UserId);
                        w.Write(remote.IsInvisible);
                    }), ct);
                    cloaks[remote.UserId] = remote.IsInvisible;
                }
            }
            finally { connection.RemoteMoveDeliveryGate.Release(); }
        }
        foreach (var id in visible.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            await connection.RemoteMoveDeliveryGate.WaitAsync(ct);
            try
            {
                if (!visible.ContainsKey(id)) continue;
                // A pilot can return while an older absent snapshot waits for this socket.
                // Preserve the announced ship when current shared membership is live.
                var currentPresence = await ReadPresenceAsync(observer.MapId, ct);
                if (currentPresence.TryGetValue(id, out var currentEntry)
                    && DateTimeOffset.UtcNow - currentEntry.SeenAtUtc < TimeSpan.FromMinutes(2))
                    continue;
                await connection.SendAsync(WorldCommandId.PilotRemoveCommand,
                    WorldPayload.Encode(w => WorldPayload.WriteInt64(w, id)), ct);
                visible.TryRemove(id, out _);
                cloaks.TryRemove(id, out _);
                heals.TryRemove(id, out _);
                remoteAnnounced.GetOrAdd(observer.UserId, _ => new()).TryRemove(id, out _);
                remotePositions.GetOrAdd(observer.UserId, _ => new()).TryRemove(id, out _);
            }
            finally { connection.RemoteMoveDeliveryGate.Release(); }
        }
    }

    private async Task ObserveConnectedHealsAsync(WorldPilot observer, WorldConnection connection,
        RemoteSnapshot snapshot, CancellationToken ct)
    {
        await connection.HealDeliveryGate.WaitAsync(ct);
        try
        {
            var announced = connectedHeals.GetOrAdd(observer.UserId, _ => new());
            var present = new HashSet<long>();
            foreach (var (entry, _, _) in snapshot.Entries)
            {
                var target = entry.Pilot;
                if (target.UserId == observer.UserId
                    || !connections.TryGetValue(target.UserId, out var local)
                    || local.Pilot.MapId != observer.MapId
                    || WorldMapInstance.IsGate(observer.MapId) && target.AccountId != observer.AccountId)
                    continue;
                // The map snapshot can predate a heal already delivered by the local socket.
                var runtime = await ReadOwnHealRuntimeAsync(target, ct);
                if (runtime is null) continue;
                present.Add(target.UserId);
                if (!announced.TryGetValue(target.UserId, out var last))
                    announced.TryAdd(target.UserId, last = string.Empty);
                foreach (var heal in UnannouncedHeals(runtime, last))
                {
                    await SendHealAsync(connection, target.UserId, heal, ct);
                    announced[target.UserId] = heal.Id;
                }
            }
            foreach (var id in announced.Keys.Where(id => !present.Contains(id)).ToArray())
                announced.TryRemove(id, out _);
        }
        finally { connection.HealDeliveryGate.Release(); }
    }

    // Pilots whose sockets live on another node reach this observer through shared presence and travel.
    public async Task<IReadOnlyList<WorldRemoteMove>> ObserveRemoteMovesAsync(WorldPilot observer,
        CancellationToken ct)
    {
        var seen = remoteAnnounced.GetOrAdd(observer.UserId, _ => new());
        var positions = remotePositions.GetOrAdd(observer.UserId, _ => new());
        var now = DateTimeOffset.UtcNow;
        var moves = new List<WorldRemoteMove>();
        foreach (var (entry, _, _) in (await RemoteSnapshotAsync(observer.MapId, ct)).Entries)
        {
            var remote = entry.Pilot with { MapId = observer.MapId };
            if (remote.UserId == observer.UserId || connections.ContainsKey(remote.UserId)
                || WorldMapInstance.IsGate(observer.MapId) && remote.AccountId != observer.AccountId
                || now - entry.SeenAtUtc >= TimeSpan.FromMinutes(2)) continue;
            // The map roster can stay cached while its pilot starts or changes course.
            // Select movement from the current shared position; delivery checks it again.
            var saved = await cache.GetStringAsync(Key(remote.UserId), ct);
            var current = ParseTravel(saved, remote.MapId);
            if (current is null)
            {
                seen.TryRemove(remote.UserId, out _);
                if (saved is not null && remoteVisible.GetOrAdd(observer.UserId, _ => new())
                    .ContainsKey(remote.UserId))
                {
                    try
                    {
                        var stationary = JsonSerializer.Deserialize<WorldPosition>(saved);
                        if (stationary?.MapId == observer.MapId
                            && positions.TryGetValue(remote.UserId, out var announced)
                            && announced != (stationary.X, stationary.Y))
                        {
                            moves.Add(new WorldRemoteMove(new WorldMoveResult(remote.UserId,
                                stationary.X, stationary.Y, 0, false), null));
                        }
                    }
                    catch (JsonException) { }
                }
                continue;
            }
            if (seen.TryGetValue(remote.UserId, out var previous) && previous == current) continue;
            var position = current.PositionAt(now);
            var distance = Math.Sqrt(Math.Pow(current.TargetX - position.X, 2)
                + Math.Pow(current.TargetY - position.Y, 2));
            // A short trip may finish between polls. The observer still needs its
            // final position if it last saw this pilot before the trip began.
            if (distance > 0 || positions.TryGetValue(remote.UserId, out var announcedPosition)
                && announcedPosition != (current.TargetX, current.TargetY))
                moves.Add(new WorldRemoteMove(new WorldMoveResult(remote.UserId,
                    current.TargetX, current.TargetY,
                    WorldMovement.DurationMilliseconds(distance, current.Speed), false), current));
        }
        return moves;
    }

    public async Task SendMoveAsync(WorldConnection observer, WorldRemoteMove remoteMove, CancellationToken ct)
    {
        await observer.RemoteMoveDeliveryGate.WaitAsync(ct);
        try
        {
            var observerId = observer.Pilot.UserId;
            var userId = remoteMove.Move.UserId;
            // Movement has no meaning to a client until creation was delivered.
            // Leave the trajectory unannounced so the next poll can retry it.
            if (!remoteVisible.GetOrAdd(observerId, _ => new()).ContainsKey(userId)) return;
            // A selected move may outlive the pilot's shared map membership. The
            // saved position remains after logout, so travel alone is insufficient.
            var present = await ReadPresenceAsync(observer.Pilot.MapId, ct);
            if (!present.TryGetValue(userId, out var membership)
                || DateTimeOffset.UtcNow - membership.SeenAtUtc >= TimeSpan.FromMinutes(2)) return;
            // A cached map snapshot can outlive a newer course or a stop. Do not send
            // its obsolete destination after the owner has published new travel.
            var saved = await cache.GetStringAsync(Key(userId), ct);
            var current = ParseTravel(saved, observer.Pilot.MapId);
            if (remoteMove.Travel is { } selectedTravel)
            {
                if (current != selectedTravel) return;
            }
            else
            {
                if (current is not null) return;
                try
                {
                    var position = saved is null ? null : JsonSerializer.Deserialize<WorldPosition>(saved);
                    if (position?.MapId != observer.Pilot.MapId
                        || (position.X, position.Y) != (remoteMove.Move.X, remoteMove.Move.Y)) return;
                }
                catch (JsonException) { return; }
            }
            if (remoteMove.Travel is { } currentTravel
                && remoteAnnounced.GetOrAdd(observerId, _ => new()).TryGetValue(userId, out var sentTravel)
                && sentTravel == currentTravel) return;
            if (remoteMove.Travel is null
                && remotePositions.GetOrAdd(observerId, _ => new()).TryGetValue(userId, out var sentPosition)
                && sentPosition == (remoteMove.Move.X, remoteMove.Move.Y)) return;
            var move = remoteMove.Move;
            if (remoteMove.Travel is { } activeTravel)
            {
                var position = activeTravel.PositionAt(DateTimeOffset.UtcNow);
                var remaining = Math.Sqrt(Math.Pow(activeTravel.TargetX - position.X, 2)
                    + Math.Pow(activeTravel.TargetY - position.Y, 2));
                move = move with { TimeToTargetMs = WorldMovement.DurationMilliseconds(remaining, activeTravel.Speed) };
            }
            await observer.SendAsync(WorldCommandId.MoveCommand, MovePayload(move), ct);
            if (remoteMove.Travel is { } travel)
                remoteAnnounced.GetOrAdd(observerId, _ => new())[userId] = travel;
            remotePositions.GetOrAdd(observerId, _ => new())[userId] =
                (remoteMove.Move.X, remoteMove.Move.Y);
        }
        catch (IOException) { /* The next observer poll retries an undelivered move. */ }
        finally { observer.RemoteMoveDeliveryGate.Release(); }
    }

    private static byte[] MovePayload(WorldMoveResult move) => WorldPayload.Encode(w =>
    {
        WorldPayload.WriteInt64(w, move.UserId);
        WorldPayload.WriteInt32(w, move.X);
        WorldPayload.WriteInt32(w, move.Y);
        WorldPayload.WriteInt32(w, move.TimeToTargetMs);
    });

    private async Task<WorldTravel?> SavedTravelAsync(WorldPilot pilot, CancellationToken ct) =>
        ParseTravel(await cache.GetStringAsync(Key(pilot.UserId), ct), pilot.MapId);

    private static WorldTravel? ParseTravel(string? saved, int mapId)
    {
        if (saved is null) return null;
        try
        {
            var current = JsonSerializer.Deserialize<WorldTravel>(saved);
            return current is { Speed: > 0 } && current.MapId == mapId ? current : null;
        }
        catch (JsonException) { return null; }
    }

    // One shared-cache pass per map per TTL, however many observers poll on this node. Local sockets are
    // filtered by each caller, so the snapshot itself stays node-agnostic and is never written back.
    private async Task<RemoteSnapshot> RemoteSnapshotAsync(int mapId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lazy = remoteSnapshots.GetOrAdd(mapId, id => new(() => ReadRemoteSnapshotAsync(id)));
            RemoteSnapshot snapshot;
            try { snapshot = await lazy.Value.WaitAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A failed read must not be cached; the next poll retries against the shared cache.
                ((ICollection<KeyValuePair<int, Lazy<Task<RemoteSnapshot>>>>)remoteSnapshots)
                    .Remove(new(mapId, lazy));
                throw;
            }
            if (attempt > 0 || DateTimeOffset.UtcNow - snapshot.ReadAtUtc < RemoteSnapshotTtl)
                return snapshot;
            ((ICollection<KeyValuePair<int, Lazy<Task<RemoteSnapshot>>>>)remoteSnapshots)
                .Remove(new(mapId, lazy));
        }
    }

    private async Task<RemoteSnapshot> ReadRemoteSnapshotAsync(int mapId)
    {
        var entries = new List<RemoteEntry>();
        foreach (var presence in (await ReadPresenceAsync(mapId, CancellationToken.None)).Values)
        {
            var userId = presence.Pilot.UserId;
            var mapText = await cache.GetStringAsync(MapKey(userId));
            var hasMapOwner = int.TryParse(mapText, out var ownerMap) && IsValidWorldMap(ownerMap);
            if (hasMapOwner && ownerMap != mapId) continue;
            // A local socket can lag a transfer performed on another node. Check the
            // shared position even for that socket before announcing map membership.
            var saved = await cache.GetStringAsync(Key(userId));
            var positionOnAnotherMap = false;
            if (saved is not null)
            {
                try
                {
                    var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                    var hasValidPosition = position is not null && IsValidWorldMap(position.MapId);
                    positionOnAnotherMap = hasValidPosition && position!.MapId != mapId;
                    if (!hasValidPosition || positionOnAnotherMap)
                        saved = null;
                }
                catch (JsonException) { saved = null; }
            }
            var hasRuntimeMap = false;
            WorldRuntimeSnapshot? runtimeSnapshot = null;
            var runtime = await cache.GetStringAsync(RuntimeKey(userId));
            if (runtime is not null)
            {
                try
                {
                    runtimeSnapshot = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtime);
                    hasRuntimeMap = runtimeSnapshot is not null && IsValidWorldMap(runtimeSnapshot.MapId);
                    if (!hasMapOwner && hasRuntimeMap && runtimeSnapshot!.MapId != mapId) continue;
                }
                catch (JsonException) { }
            }
            if (!hasMapOwner && !hasRuntimeMap && positionOnAnotherMap) continue;
            entries.Add(new(presence, saved, runtimeSnapshot));
        }
        return new(DateTimeOffset.UtcNow, entries);
    }

    public void DetachForHandoff(WorldPilot pilot, WorldConnection? connection)
    {
        if (connection is not null)
            ((ICollection<KeyValuePair<long, WorldConnection>>)connections)
                .Remove(new(pilot.UserId, connection));
        announcedRepairBots.TryRemove(pilot.UserId, out _);
        if (connected.TryGetValue(pilot.MapId, out var pilots)) pilots.TryRemove(pilot.UserId, out _);
        // The destination node reads the shared position/runtime/presence on reconnect.
    }

    public async Task<WorldMoveResult> MoveAsync(WorldPilot pilot, int targetX, int targetY,
        int? configuration, CancellationToken ct, DateTimeOffset? at = null,
        WorldLeaseFence? fence = null, int? expectedMapId = null,
        Guid? expectedMoveId = null)
    {
        if (configuration is not null)
        {
            var change = await SwitchAsync(pilot, configuration.Value, ct, at, fence, expectedMapId,
                expectedMoveId);
            if (change.CooldownRemainingMs > 0)
                throw new WorldConfigurationCooldownException(change.CooldownRemainingMs);
        }
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (meshMutex is not null || expectedMapId is not null)
            {
                var admittedMap = pilot.MapId;
                pilot = await RefreshAsync(pilot, ct);
                if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            }
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (expectedMoveId is { } revision
                && (travel.GetValueOrDefault(pilot.UserId)?.MoveId ?? Guid.Empty) != revision)
                throw new WorldMoveChangedException();
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (CurrentHitPoints(pilot) <= 0) throw new WorldPilotDestroyedException();
            var now = at ?? DateTimeOffset.UtcNow;
            var selected = Configuration(pilot.UserId);
            var speed = await EffectiveSpeedAsync(pilot, ActiveStats(pilot), ct);
            if (speed <= 0) throw new InvalidDataException("Invalid ship speed.");
            var previous = travel.GetValueOrDefault(pilot.UserId);
            var result = WorldMovement.Begin(pilot, previous, targetX, targetY, speed, now);
            var next = WorldMovement.Travel(pilot, previous, result, speed, now);
            travel[pilot.UserId] = next;
            var atBoundary = !WorldMovement.IsInsideBoundary(CurrentPosition(pilot, now),
                WorldMapInstance.Starter(pilot.MapId));
            var warning = atBoundary;
            radiationWarnings[pilot.UserId] = warning;
            if (warning) await SetRepairBotAsync(pilot, false, ct);
            configurations[pilot.UserId] = selected;
            await cache.SetStringAsync(Key(pilot.UserId), JsonSerializer.Serialize(next),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            await PersistRuntimeAsync(pilot, ct);
            return result with { RadiationWarning = warning, Travel = next };
        }
        finally { gate.Release(); }
    }

    public async Task<WorldSwitchResult> SwitchAsync(WorldPilot pilot, int configuration,
        CancellationToken ct, DateTimeOffset? at = null, WorldLeaseFence? fence = null,
        int? expectedMapId = null, Guid? expectedMoveId = null)
    {
        if (configuration is not (1 or 2)) throw new InvalidDataException("Invalid ship configuration.");
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (meshMutex is not null || expectedMapId is not null)
            {
                var admittedMap = pilot.MapId;
                pilot = await RefreshAsync(pilot, ct);
                if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            }
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (expectedMoveId is { } revision
                && (travel.GetValueOrDefault(pilot.UserId)?.MoveId ?? Guid.Empty) != revision)
                throw new WorldMoveChangedException();
            if (CurrentHitPoints(pilot) <= 0) throw new WorldPilotDestroyedException();
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            var now = at ?? DateTimeOffset.UtcNow;
            if (Configuration(pilot.UserId) == configuration) return new(false, 0, null);
            if (lastSwitches.TryGetValue(pilot.UserId, out var last))
            {
                var cooldown = variables is null ? 5000 : (int)await variables.NumberAsync("world.config.switch_cooldown_ms", ct);
                var remaining = cooldown - (int)(now - last).TotalMilliseconds;
                if (remaining > 0) return new(false, remaining, null);
            }
            var old = ActiveStats(pilot);
            var oldShield = CurrentShield(pilot);
            var next = configuration == 1 ? pilot.Stats
                : pilot.ConfigurationTwoStats ?? pilot.Stats with { Speed = pilot.ConfigurationTwoSpeed };
            if (next.Speed <= 0) throw new InvalidDataException("Invalid ship speed.");
            configurations[pilot.UserId] = configuration;
            shields[pilot.UserId] = old.MaxShield == 0 ? next.MaxShield
                : Math.Clamp((long)Math.Round((double)oldShield / old.MaxShield * next.MaxShield), 0, next.MaxShield);
            lastSwitches[pilot.UserId] = now;
            var prior = travel.GetValueOrDefault(pilot.UserId);
            var target = prior?.MapId == pilot.MapId ? (prior.TargetX, prior.TargetY) : (pilot.X, pilot.Y);
            var switchSpeed = await EffectiveSpeedAsync(pilot, next, ct);
            var move = WorldMovement.Begin(pilot, prior, target.Item1, target.Item2, switchSpeed, now);
            var retimed = WorldMovement.Travel(pilot, prior, move, switchSpeed, now) with
            { MoveId = prior?.MoveId ?? Guid.Empty };
            travel[pilot.UserId] = retimed;
            await cache.SetStringAsync(Key(pilot.UserId), JsonSerializer.Serialize(retimed),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            await PersistRuntimeAsync(pilot, ct);
            return new(true, 0, move with { Travel = retimed });
        }
        finally { gate.Release(); }
    }

    public async Task<WorldMoveResult?> RetimeForLiveSpeedAsync(WorldPilot pilot, CancellationToken ct,
        WorldLeaseFence? fence = null, DateTimeOffset? at = null)
    {
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (meshMutex is not null)
            {
                var admittedMap = pilot.MapId;
                pilot = await RefreshAsync(pilot, ct);
                if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            }
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            var previous = travel.GetValueOrDefault(pilot.UserId);
            if (previous is null || previous.MapId != pilot.MapId) return null;
            var speed = await EffectiveSpeedAsync(pilot, ActiveStats(pilot), ct);
            if (previous.Speed == speed) return null;
            var now = at ?? DateTimeOffset.UtcNow;
            if (previous.PositionAt(now) == (previous.TargetX, previous.TargetY)) return null;
            var move = WorldMovement.Begin(pilot, previous, previous.TargetX, previous.TargetY, speed, now);
            var next = WorldMovement.Travel(pilot, previous, move, speed, now) with
            { MoveId = previous.MoveId };
            travel[pilot.UserId] = next;
            await cache.SetStringAsync(Key(pilot.UserId), JsonSerializer.Serialize(next),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            await PersistRuntimeAsync(pilot, ct);
            return move with { Travel = next };
        }
        finally { gate.Release(); }
    }

    private async Task PersistRuntimeAsync(WorldPilot pilot, CancellationToken ct,
        bool clearDeathId = false, bool? cloakOverride = null, long? cloakActivatedAtUnixMs = null,
        long healAmount = 0, string? repairBotEventId = null)
    {
        var previousJson = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
        WorldRuntimeSnapshot? previous = null;
        if (previousJson is not null)
            try { previous = JsonSerializer.Deserialize<WorldRuntimeSnapshot>(previousJson); }
            catch (JsonException) { }
        var healId = healAmount > 0 ? Guid.NewGuid().ToString("N") : null;
        var recentHeals = previous?.RecentHeals ?? [];
        if (healId is not null)
            recentHeals = [.. recentHeals.TakeLast(15),
                new WorldHealEvent(healId, healAmount, CurrentHitPoints(pilot))];
        var recentRepairBotStates = previous?.RecentRepairBotStates ?? [];
        if (repairBotEventId is not null)
            recentRepairBotStates = [.. recentRepairBotStates.TakeLast(15),
                new WorldRepairBotEvent(repairBotEventId, RepairBotActive(pilot.UserId))];
        await cache.SetStringAsync(RuntimeKey(pilot.UserId), JsonSerializer.Serialize(new WorldRuntimeSnapshot(
            pilot.MapId, Configuration(pilot.UserId), CurrentShield(pilot),
            lastSwitches.TryGetValue(pilot.UserId, out var last) ? last.ToUnixTimeMilliseconds() : 0,
            travel.GetValueOrDefault(pilot.UserId), CurrentHitPoints(pilot),
            lastHazards.TryGetValue(pilot.UserId, out var hazard) ? hazard.ToUnixTimeMilliseconds() : 0,
            RadiationWarning(pilot.UserId), RepairBotActive(pilot.UserId),
            clearDeathId ? null : await DeathIdAsync(pilot.UserId, ct),
            cloakOverride ?? previous?.IsInvisible ?? pilot.IsInvisible,
            cloakActivatedAtUnixMs ?? previous?.LastCloakActivationUnixMs ?? 0,
            healId ?? previous?.LastHealId,
            healAmount > 0 ? healAmount : previous?.LastHealAmount ?? 0,
            healAmount > 0 ? CurrentHitPoints(pilot) : previous?.LastHealHull ?? 0,
            recentHeals, recentRepairBotStates)),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
    }

    private async Task ApplyBoundaryDamageAsync(WorldPilot pilot, DateTimeOffset now, CancellationToken ct)
    {
        if (await RepairProtectedAsync(pilot.UserId, ct)) return;
        if (lastHazards.TryGetValue(pilot.UserId, out var last) && now - last < TimeSpan.FromSeconds(1)) return;
        await SetRepairBotAsync(pilot, false, ct);
        lastHazards[pilot.UserId] = now;
        var damage = variables is null ? Math.Max(1, ActiveStats(pilot).MaxHitPoints / 100)
            : (long)await variables.NumberAsync("world.radiation.damage_per_attempt", ct);
        if (damage == 0) damage = Math.Max(1, ActiveStats(pilot).MaxHitPoints / 100);
        var current = CurrentHitPoints(pilot);
        var remaining = Math.Max(0, current - damage);
        var appliedDamage = current - remaining;
        hitPoints[pilot.UserId] = remaining;
        if (hitPoints[pilot.UserId] == 0)
            pilot = await RecordDestructionAsync(pilot, ct);
        await PersistRuntimeAsync(pilot, ct);
        await BroadcastAsync(pilot, WorldCommandId.RadiationDamageCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            WorldPayload.WriteInt64(w, appliedDamage);
            WorldPayload.WriteInt64(w, CurrentHitPoints(pilot));
            WorldPayload.WriteInt64(w, CurrentShield(pilot));
        }), ct);
    }

    public async Task<bool> RepairAtBaseAsync(WorldPilot pilot, DateTimeOffset now, CancellationToken ct)
    {
        if (WorldMapInstance.IsGate(pilot.MapId)) return false;
        var map = WorldMapInstance.Starter(pilot.MapId);
        if (pilot.FactionId != map.FactionId) return false;
        var station = map.HomeStation;
        var position = CurrentPosition(pilot, now);
        var dx = (long)position.X - station.X;
        var dy = (long)position.Y - station.Y;
        var peaceRadius = variables is null ? station.PeaceRadius
            : (int)await variables.NumberAsync("world.station.peace_radius", ct);
        if (dx * dx + dy * dy > (long)peaceRadius * peaceRadius) return false;
        var stats = ActiveStats(pilot);
        var current = CurrentHitPoints(pilot);
        if (current <= 0) return false;
        if (current >= stats.MaxHitPoints) return false;
        if (await PortalCombatLockedAsync(pilot, ct)) return false;
        var repair = variables is null ? 0 : (long)await variables.NumberAsync("world.station.repair_hp_per_tick", ct);
        hitPoints[pilot.UserId] = repair > 0
            ? Math.Min(stats.MaxHitPoints, current + repair)
            : stats.MaxHitPoints;
        if (hitPoints[pilot.UserId] >= stats.MaxHitPoints)
            await SetRepairBotAsync(pilot, false, ct);
        await PersistRuntimeAsync(pilot, ct, healAmount: hitPoints[pilot.UserId] - current);
        await AnnounceHullHealAsync(pilot, ct);
        return true;
    }

    private async Task AnnounceHullHealAsync(WorldPilot pilot, CancellationToken ct)
    {
        var runtime = await ReadOwnHealRuntimeAsync(pilot, ct);
        if (runtime is null) return;
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == pilot.MapId
            && (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId)).ToArray())
        {
            var own = observer.Pilot.UserId == pilot.UserId;
            if (own)
            {
                try { await ObserveOwnHealAsync(pilot, observer, ct); }
                catch (IOException) { }
                continue;
            }
            await observer.HealDeliveryGate.WaitAsync(ct);
            try
            {
                var observed = connectedHeals.GetOrAdd(observer.Pilot.UserId, _ => new());
                var announced = observed.GetValueOrDefault(pilot.UserId);
                foreach (var heal in UnannouncedHeals(runtime, announced))
                {
                    try { await SendHealAsync(observer, pilot.UserId, heal, ct); }
                    catch (IOException) { break; }
                    observed[pilot.UserId] = heal.Id;
                }
            }
            finally { observer.HealDeliveryGate.Release(); }
        }
    }

    // Station activation is serialized with lease reassignment and shared pilot state.
    public async Task<bool> RepairAtBaseFencedAsync(WorldPilot pilot, DateTimeOffset now,
        CancellationToken ct, WorldLeaseFence? fence)
    {
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (meshMutex is not null)
            {
                var admittedMap = pilot.MapId;
                pilot = await RefreshAsync(pilot, ct);
                if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            }
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            return await RepairAtBaseAsync(pilot, now, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<WorldJumpResult?> JumpAsync(WorldPilot pilot, int portalId,
        IServerMeshRegistry? registry, CancellationToken ct, WorldLeaseFence? fence = null,
        int? expectedMapId = null)
    {
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            var admittedMap = pilot.MapId;
            pilot = await RefreshAsync(pilot, ct);
            if (expectedMapId is not null && pilot.MapId != expectedMapId)
                throw new WorldMapChangedException();
            if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (CurrentHitPoints(pilot) <= 0) throw new WorldPilotDestroyedException();
            var portal = (await MapAsync(pilot.MapId, ct)).Portals.FirstOrDefault(p => p.Id == portalId && p.Working)
                ?? WorldMapInstance.GateEntrances(pilot.MapId).FirstOrDefault(p => p.Id == portalId);
            if (portal is null) return null;
            if (WorldMapInstance.GateId(portal.DestinationMapId) is { } gateId)
            {
                if (economy is null || pilot.AccountId == Guid.Empty) return null;
                var combat = await economy.GateCombatAsync(pilot.AccountId, gateId, ct);
                if (combat is null || combat.CompletedAt is not null || combat.Lives <= 0) return null;
            }
            var position = CurrentPosition(pilot);
            var dx = (long)position.X - portal.X;
            var dy = (long)position.Y - portal.Y;
            var jumpRadius = variables is null ? portal.JumpRadius
                : (int)await variables.NumberAsync("world.portal.jump_radius", ct);
            if (dx * dx + dy * dy > (long)jumpRadius * jumpRadius) return null;
            if ((configuredCombatRestrictedMaps?.Contains(pilot.MapId) ?? !WorldMapInstance.IsGate(pilot.MapId))
                && await PortalCombatLockedAsync(pilot, ct)) return null;
            var next = pilot with { MapId = portal.DestinationMapId,
                X = portal.ArrivalX, Y = portal.ArrivalY };
            travel.TryRemove(pilot.UserId, out _);
            radiationWarnings.TryRemove(pilot.UserId, out _);
            repairBots.TryRemove(pilot.UserId, out _);
            await PersistAsync(next, ct);
            await PersistRuntimeAsync(next, ct);
            await WritePresenceAsync(pilot, false, ct);
            await WritePresenceAsync(next, true, ct);
            if (connected.TryGetValue(pilot.MapId, out var old)) old.TryRemove(pilot.UserId, out _);
            connected.GetOrAdd(next.MapId, _ => new ConcurrentDictionary<long, WorldPilot>())[next.UserId] = next;
            if (connections.TryGetValue(next.UserId, out var connection)) connection.Pilot = next;
            var snapshot = registry is null ? null : await registry.GetSnapshotAsync(ct);
            var lease = snapshot?.Leases.FirstOrDefault(l => l.MapId == next.MapId
                && l.ExpiresAtUtc > DateTimeOffset.UtcNow);
            var owner = snapshot?.Nodes.FirstOrDefault(n => n.NodeId == lease?.NodeId
                && n.Status == ServerMeshNodeStatus.Active);
            return new(next, portalId, owner?.Simulated == true ? null : owner?.NodeId,
                owner is null ? null : $"{owner.WorldTcpHost}:{owner.WorldTcpPort}");
        }
        finally { gate.Release(); }
    }

    private async Task ClearRepairRuntimeAsync(long userId, CancellationToken ct)
    {
        travel.TryRemove(userId, out _);
        radiationWarnings.TryRemove(userId, out _);
        lastHazards.TryRemove(userId, out _);
        repairBots.TryRemove(userId, out _);
        await cache.RemoveAsync(CombatKey(userId), ct);
        await cache.RemoveAsync(HullRecoveryKey(userId), ct);
        await cache.RemoveAsync(ShieldRecoveryKey(userId), ct);
    }

    public Task<WorldPilot?> RespawnAtBaseAsync(WorldPilot pilot, CancellationToken ct,
        WorldLeaseFence? fence = null) => RespawnAsync(pilot, WorldRespawnLocation.Base, ct, fence);

    public async Task<WorldPilot?> RespawnAsync(WorldPilot pilot, WorldRespawnLocation location,
        CancellationToken ct, WorldLeaseFence? fence = null)
    {
        var gate = movementGates.GetOrAdd(pilot.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            var admittedMap = pilot.MapId;
            pilot = await RefreshAsync(pilot, ct);
            if (pilot.MapId != admittedMap) throw new WorldMapChangedException();
            if (CurrentHitPoints(pilot) > 0)
            {
                if (repairWallet is null || pilot.AccountId == Guid.Empty
                    || location is WorldRespawnLocation.Base) return null;
                var completedDeathId = await DeathIdAsync(pilot.UserId, ct, pilot.AccountId);
                if (completedDeathId is null) return null;
                var completed = await repairWallet.FindChargeAsync(pilot.AccountId, location,
                    completedDeathId, ct);
                return completed is null ? null : pilot with { Uridium = completed.Balance };
            }
            if (location is WorldRespawnLocation.DeathLocation && WorldMapInstance.IsGate(pilot.MapId))
                return null;
            var map = await MapAsync(pilot.MapId, ct);
            if (location is WorldRespawnLocation.NearestPortal && !map.AllowsPortalRepair)
                return null;
            // A debit may have committed before the previous request could restore world state.
            // Resume that paid choice even when the retry asks for a different repair location.
            var chargedForThisDeath = false;
            if (repairWallet is not null && pilot.AccountId != Guid.Empty)
            {
                var deathId = await DeathIdAsync(pilot.UserId, ct, pilot.AccountId);
                if (deathId is not null)
                {
                    foreach (var paidLocation in new[]
                             { WorldRespawnLocation.DeathLocation, WorldRespawnLocation.NearestPortal })
                    {
                        if (await repairWallet.FindChargeAsync(pilot.AccountId, paidLocation, deathId, ct)
                            is not null)
                        {
                            location = paidLocation;
                            chargedForThisDeath = true;
                            break;
                        }
                    }
                }
            }
            if (location is WorldRespawnLocation.NearestPortal && !map.AllowsPortalRepair)
                return null;
            var cooldown = RespawnCooldown(location);
            if (cooldown > TimeSpan.Zero && !chargedForThisDeath)
            {
                var last = repairWallet is not null && pilot.AccountId != Guid.Empty
                    ? await repairWallet.FindLastRepairAsync(pilot.AccountId, location, ct)
                    : null;
                if (last is null)
                {
                    var saved = await cache.GetStringAsync(RespawnCooldownKey(pilot.UserId, location), ct);
                    if (long.TryParse(saved, out var milliseconds))
                        last = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                }
                if (last is not null && combatClock.GetUtcNow() - last.Value < cooldown)
                    return null;
            }
            if (cooldown > TimeSpan.Zero && repairWallet is not null)
            {
                if (pilot.AccountId == Guid.Empty) return null;
                var deathId = await DeathIdAsync(pilot.UserId, ct, pilot.AccountId);
                if (deathId is null)
                {
                    deathId = Guid.NewGuid().ToString("N");
                    await cache.SetStringAsync(DeathKey(pilot.UserId), deathId,
                        new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
                }
                var payment = await repairWallet.ChargeAsync(pilot.AccountId, location, deathId, ct);
                if (payment.Charge == WorldRepairCharge.InsufficientFunds) return null;
                pilot = pilot with { Uridium = payment.Balance };
            }
            var baseMapId = (Math.Clamp(pilot.FactionId, 1, 4) - 1) * 4 + 1;
            var station = WorldMapInstance.Starter(baseMapId).HomeStation;
            var respawned = location switch
            {
                WorldRespawnLocation.DeathLocation => pilot,
                WorldRespawnLocation.NearestPortal => AtNearestPortal(pilot, map),
                _ => pilot with { MapId = baseMapId, X = station.X, Y = station.Y }
            };
            hitPoints[pilot.UserId] = Math.Max(1, ActiveStats(pilot).MaxHitPoints
                / (location is WorldRespawnLocation.Base ? 100 : 10));
            shields[pilot.UserId] = 0;
            await ClearRepairRuntimeAsync(pilot.UserId, ct);
            var repairedAt = combatClock.GetUtcNow();
            await cache.SetStringAsync(RepairProtectionKey(pilot.UserId),
                repairedAt.ToUnixTimeMilliseconds().ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
            var repairedDeathId = repairWallet is not null && pilot.AccountId != Guid.Empty
                ? await DeathIdAsync(pilot.UserId, ct, pilot.AccountId) : null;
            if (repairedDeathId is not null)
                await repairWallet!.StageRepairAsync(pilot.AccountId, repairedDeathId,
                    new WorldRepairOutcome(respawned.MapId, respawned.X, respawned.Y,
                        CurrentHitPoints(pilot), CurrentShield(pilot), repairedAt), location, repairedAt, ct);
            await PersistAsync(respawned, ct);
            if (location is WorldRespawnLocation.Base)
                await cache.RemoveAsync(DeathKey(pilot.UserId), ct);
            await PersistRuntimeAsync(respawned, ct, clearDeathId: location is WorldRespawnLocation.Base);
            if (repairedDeathId is not null)
                await repairWallet!.CompleteDeathAsync(pilot.AccountId, repairedDeathId, ct);
            if (cooldown > TimeSpan.Zero)
                await cache.SetStringAsync(RespawnCooldownKey(pilot.UserId, location),
                    combatClock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = cooldown }, ct);
            await WritePresenceAsync(pilot, false, ct);
            await WritePresenceAsync(respawned, true, ct);
            if (connected.TryGetValue(pilot.MapId, out var old)) old.TryRemove(pilot.UserId, out _);
            connected.GetOrAdd(respawned.MapId, _ => new ConcurrentDictionary<long, WorldPilot>())[pilot.UserId] = respawned;
            if (connections.TryGetValue(pilot.UserId, out var connection)) connection.Pilot = respawned;
            return respawned;
        }
        finally { gate.Release(); }
    }

    private static WorldPilot AtNearestPortal(WorldPilot pilot, WorldMapInstance map)
    {
        var portal = map.NearestWorkingPortal(pilot.X, pilot.Y);
        return portal is null ? pilot : pilot with { X = portal.X, Y = portal.Y };
    }

    internal static async Task CheckLeaseAsync(int mapId, WorldLeaseFence? fence, CancellationToken ct)
    {
        if (fence is null) return;
        var snapshot = await fence.Registry.GetSnapshotAsync(ct);
        var lease = snapshot.Leases.FirstOrDefault(item => item.MapId == mapId
            && item.NodeId == fence.NodeId && item.Epoch == fence.Epoch
            && item.ExpiresAtUtc > DateTimeOffset.UtcNow);
        if (lease is null || !snapshot.Nodes.Any(item => item.NodeId == fence.NodeId
            && item.Status == ServerMeshNodeStatus.Active))
            throw new WorldLeaseLostException();
    }

    public async Task SendAttributesAsync(WorldPacketWriter writer, WorldPilot pilot, CancellationToken ct) =>
        await writer.WriteAsync(WorldCommandId.ShipAttributesCommand,
            AttributesPayload(pilot, await LiveStatsAsync(pilot, ct)), ct);

    public Task SendAttributesAsync(WorldConnection connection, WorldPilot pilot, WorldShipStats stats,
        CancellationToken ct) => connection.SendAsync(WorldCommandId.ShipAttributesCommand,
            AttributesPayload(pilot, stats), ct);

    public Task SendCompanionsAsync(WorldPacketWriter writer, WorldPilot pilot, CancellationToken ct) =>
        writer.WriteAsync(WorldCommandId.CompanionStateCommand, CompanionsPayload(pilot), ct);

    public async Task BroadcastAttributesAsync(WorldPilot pilot, CancellationToken ct) =>
        await BroadcastAsync(pilot, WorldCommandId.ShipAttributesCommand,
            AttributesPayload(pilot, await LiveStatsAsync(pilot, ct)), ct);

    public async Task BroadcastCompanionsAsync(WorldPilot pilot, CancellationToken ct) =>
        await BroadcastAsync(pilot, WorldCommandId.CompanionStateCommand, CompanionsPayload(pilot), ct);

    private byte[] AttributesPayload(WorldPilot pilot, WorldShipStats stats)
    {
        return WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            w.Write((byte)Configuration(pilot.UserId));
            WorldPayload.WriteInt32(w, stats.Speed);
            WorldPayload.WriteInt64(w, stats.MaxHitPoints);
            WorldPayload.WriteInt64(w, CurrentShield(pilot));
            WorldPayload.WriteInt64(w, stats.MaxShield);
            WorldPayload.WriteInt32(w, stats.LaserDamage);
        });
    }

    private byte[] CompanionsPayload(WorldPilot pilot)
    {
        var companions = CompanionPositions(pilot);
        var (targetX, targetY) = Destination(pilot);
        var targets = WorldCompanions.At(Configuration(pilot.UserId) == 2
            ? pilot.ConfigurationTwoCompanions : pilot.Companions, targetX, targetY);
        var moving = travel.GetValueOrDefault(pilot.UserId);
        var duration = moving is null ? 0 : WorldMovement.Begin(pilot, moving, targetX, targetY,
            ActiveStats(pilot).Speed, DateTimeOffset.UtcNow).TimeToTargetMs;
        return WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            WorldPayload.WriteInt32(w, companions.Count);
            for (var i = 0; i < companions.Count; i++)
            {
                var item = companions[i];
                WorldPayload.WriteString(w, item.Kind);
                WorldPayload.WriteString(w, item.ItemId);
                WorldPayload.WriteInt32(w, item.Index);
                WorldPayload.WriteString(w, item.Formation);
                WorldPayload.WriteInt32(w, item.X);
                WorldPayload.WriteInt32(w, item.Y);
                WorldPayload.WriteInt32(w, targets[i].X);
                WorldPayload.WriteInt32(w, targets[i].Y);
                WorldPayload.WriteInt32(w, duration);
            }
        });
    }

    private async Task BroadcastAsync(WorldPilot pilot, WorldCommandId command, byte[] payload, CancellationToken ct)
    {
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == pilot.MapId &&
            (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId)).ToArray())
        {
            try { await observer.SendAsync(command, payload, ct); }
            catch (IOException) { }
        }
    }

    private Task BroadcastCloakAsync(WorldPilot pilot, CancellationToken ct) =>
        BroadcastAsync(pilot, WorldCommandId.CloakStateCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            w.Write(pilot.IsInvisible);
        }), ct);

    public (int X, int Y) CurrentPosition(WorldPilot pilot, DateTimeOffset? at = null) =>
        travel.TryGetValue(pilot.UserId, out var moving) && moving.MapId == pilot.MapId
            ? moving.PositionAt(at ?? DateTimeOffset.UtcNow) : (pilot.X, pilot.Y);

    public (int X, int Y) Destination(WorldPilot pilot) =>
        travel.TryGetValue(pilot.UserId, out var moving) && moving.MapId == pilot.MapId
            ? (moving.TargetX, moving.TargetY) : (pilot.X, pilot.Y);

    public bool RadiationWarning(long userId) => radiationWarnings.GetValueOrDefault(userId);

    private async Task SendRadiationWarningAsync(long userId, bool warning, CancellationToken ct)
    {
        if (!connections.TryGetValue(userId, out var connection)) return;
        try { await connection.SendAsync(WorldCommandId.RadiationWarningCommand,
            new byte[] { (byte)(warning ? 1 : 0) }, ct); }
        catch (IOException) { }
    }

    public bool SetRadiationWarning(long userId, bool warning)
    {
        var previous = radiationWarnings.GetValueOrDefault(userId);
        radiationWarnings[userId] = warning;
        return previous != warning;
    }

    public async Task BroadcastMoveAsync(WorldPilot pilot, WorldMoveResult move, CancellationToken ct,
        bool localMutation = true)
    {
        var audience = connections.Values.Where(c => c.Pilot.MapId == pilot.MapId &&
            (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId))
            .OrderBy(c => c.Pilot.UserId == pilot.UserId ? 0 : 1).ToArray();
        foreach (var observer in audience)
        {
            await observer.RemoteMoveDeliveryGate.WaitAsync(ct);
            try
            {
                var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
                var current = ParseTravel(saved, pilot.MapId);
                if (move.Travel is not null && current != move.Travel) continue;
                if (current is not null && (current.TargetX, current.TargetY) != (move.X, move.Y)
                    || current is null && SavedStationaryPosition(saved, pilot.MapId) != (move.X, move.Y))
                    continue;
                if (current is not null && (current.TargetX, current.TargetY) == (move.X, move.Y))
                {
                    var delivered = observer.Pilot.UserId == pilot.UserId
                        ? announcedTravel.TryGetValue(pilot.UserId, out var own) && own == current
                        : remoteAnnounced.GetOrAdd(observer.Pilot.UserId, _ => new())
                            .TryGetValue(pilot.UserId, out var remote) && remote == current;
                    if (delivered) continue;
                }
                await observer.SendAsync(WorldCommandId.MoveCommand, MovePayload(move), ct);
                if (observer.Pilot.UserId == pilot.UserId)
                {
                    if (current is null
                        && SavedStationaryPosition(saved, pilot.MapId) == (move.X, move.Y))
                    {
                        announcedTravel.TryRemove(pilot.UserId, out _);
                        travel.TryRemove(pilot.UserId, out _);
                    }
                    else if (current is not null)
                        announcedTravel[pilot.UserId] = current;
                    announcedPosition[pilot.UserId] = (move.X, move.Y);
                }
                else if (current is not null && (current.TargetX, current.TargetY) == (move.X, move.Y))
                    remoteAnnounced.GetOrAdd(observer.Pilot.UserId, _ => new())[pilot.UserId] = current;
            }
            catch (IOException)
            {
                // Retry a move when its owning socket did not receive it.
                if (observer.Pilot.UserId == pilot.UserId) return;
            }
            finally { observer.RemoteMoveDeliveryGate.Release(); }
        }
        if (CompanionPositions(pilot).Count > 0)
            await BroadcastCompanionsAsync(pilot, ct);
    }

    public async Task BroadcastCollectAsync(WorldPilot pilot, WorldCollectible box, CancellationToken ct)
    {
        await BroadcastAsync(pilot, WorldCommandId.CollectBoxRemoveCommand,
            WorldPayload.Encode(w => WorldPayload.WriteInt32(w, box.Id)), ct);
        if (connections.TryGetValue(pilot.UserId, out var collector))
            await collector.SendAsync(WorldCommandId.CollectRewardCommand, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, box.Id);
                WorldPayload.WriteString(w, box.AssetId);
                WorldPayload.WriteInt32(w, box.Amount);
            }), ct);
    }

    public async Task BroadcastNpcDamageAsync(WorldPilot pilot, WorldNpc npc, int damage, bool destroyed,
        WorldNpcReward? reward, CancellationToken ct, bool rocket = false)
    {
        // The hit packet carries the new vitals; record them so idle sync does not repeat them.
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == pilot.MapId &&
            (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId)))
            observer.NpcVitals[npc.Id] = (npc.Hp, npc.Shield);
        await BroadcastAsync(pilot, rocket ? WorldCommandId.RocketHitCommand : WorldCommandId.LaserHitCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            WorldPayload.WriteInt32(w, npc.Id);
            WorldPayload.WriteInt32(w, damage);
            WorldPayload.WriteInt32(w, npc.Hp);
            WorldPayload.WriteInt32(w, npc.Shield);
        }), ct);
        if (destroyed)
        {
            // Tombstone first so an observer's idle roster sync never re-creates or re-destroys it.
            foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == pilot.MapId &&
                (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId)))
                observer.KnownNpcs[npc.Id] = false;
            await BroadcastAsync(pilot, WorldCommandId.ShipDestroyCommand,
                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, npc.Id)), ct);
            if (connections.TryGetValue(pilot.UserId, out var attacker))
            {
                try { await attacker.SendAsync(WorldCommandId.NpcRewardCommand, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, npc.Id);
                    WorldPayload.WriteInt32(w, reward!.Credits);
                    WorldPayload.WriteInt32(w, reward.Uridium);
                    WorldPayload.WriteInt32(w, reward.Experience);
                    WorldPayload.WriteInt32(w, reward.Honor);
                }), ct); }
                catch (IOException) { }
            }
        }
    }

    public async Task ApplyNpcDamageAsync(WorldPilot pilot, int npcId, int damage, CancellationToken ct,
        double shieldAbsorption = 1)
    {
        await using var held = meshMutex is null ? null : await meshMutex.AcquireAsync(ct);
        await ApplyNpcDamageWhileLockedAsync(pilot, npcId, damage, ct, shieldAbsorption);
    }

    internal async Task ApplyNpcDamageWhileLockedAsync(WorldPilot pilot, int npcId, int damage,
        CancellationToken ct, double shieldAbsorption = 1)
    {
        var expectedMapId = pilot.MapId;
        pilot = await RestoreAsync(pilot, ct);
        if (pilot.MapId != expectedMapId || CurrentHitPoints(pilot) <= 0) return;
        if (await RepairProtectedAsync(pilot.UserId, ct)) return;
        await MarkCombatWhileLockedAsync(pilot, ct);
        var absorbed = Math.Min(CurrentShield(pilot),
            (long)Math.Round(Math.Max(0, damage) * Math.Clamp(shieldAbsorption, 0, 1)));
        shields[pilot.UserId] = CurrentShield(pilot) - absorbed;
        hitPoints[pilot.UserId] = Math.Max(0, CurrentHitPoints(pilot) - (damage - absorbed));
        if (hitPoints[pilot.UserId] == 0)
            pilot = await RecordDestructionAsync(pilot, ct);
        await PersistRuntimeAsync(pilot, ct);
        await BroadcastAsync(pilot, WorldCommandId.NpcFireCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, npcId);
            WorldPayload.WriteInt64(w, pilot.UserId);
            WorldPayload.WriteInt32(w, damage);
            WorldPayload.WriteInt64(w, CurrentHitPoints(pilot));
            WorldPayload.WriteInt64(w, CurrentShield(pilot));
        }), ct);
    }

    private async Task<WorldPilot> RecordDestructionAsync(WorldPilot pilot, CancellationToken ct)
    {
        var deathId = Guid.NewGuid().ToString("N");
        var (x, y) = CurrentPosition(pilot);
        pilot = pilot with { X = x, Y = y };
        if (repairWallet is not null && pilot.AccountId != Guid.Empty)
            await repairWallet.RecordDeathAsync(pilot.AccountId, deathId,
                new WorldPosition(pilot.MapId, x, y), ct);
        await cache.SetStringAsync(DeathKey(pilot.UserId), deathId,
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
        var runtimeJson = await cache.GetStringAsync(RuntimeKey(pilot.UserId), ct);
        var wasCloaked = pilot.IsInvisible;
        if (runtimeJson is not null)
        {
            try { wasCloaked |= JsonSerializer.Deserialize<WorldRuntimeSnapshot>(runtimeJson)?.IsInvisible == true; }
            catch (JsonException) { }
        }
        travel.TryRemove(pilot.UserId, out _);
        radiationWarnings.TryRemove(pilot.UserId, out _);
        if (wasCloaked) pilot = pilot with { IsInvisible = false };
        if (wasCloaked) await PersistRuntimeAsync(pilot, ct, cloakOverride: false);
        await PersistAsync(pilot, ct);
        if (connected.TryGetValue(pilot.MapId, out var occupants))
            occupants[pilot.UserId] = pilot;
        if (wasCloaked)
        {
            RefreshConnectedPilot(pilot);
            await WritePresenceAsync(pilot, true, ct);
            await BroadcastCloakAsync(pilot, ct);
        }
        return pilot;
    }

    public async Task BroadcastNpcMoveAsync(int mapId, WorldNpc npc, CancellationToken ct)
    {
        var payload = WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, npc.Id);
            WorldPayload.WriteInt32(w, npc.X);
            WorldPayload.WriteInt32(w, npc.Y);
            WorldPayload.WriteInt32(w, 1000);
        });
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == mapId).ToArray())
        {
            var (x, y) = CurrentPosition(observer.Pilot);
            if ((long)(x - npc.X) * (x - npc.X) + (long)(y - npc.Y) * (y - npc.Y) > 5000L * 5000) continue;
            try { await observer.SendAsync(WorldCommandId.MoveCommand, payload, ct); }
            catch (IOException) { }
        }
    }

    public async Task BroadcastNpcSpawnAsync(int mapId, WorldNpc npc, CancellationToken ct)
    {
        var payload = WorldNpcPackets.Create(npc);
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == mapId).ToArray())
        {
            try { await observer.SendAsync(WorldCommandId.ShipCreateCommand, payload, ct); }
            catch (IOException) { }
        }
    }

    public async Task BroadcastBoxRemovalAsync(int mapId, int boxId, CancellationToken ct)
    {
        var payload = WorldPayload.Encode(w => WorldPayload.WriteInt32(w, boxId));
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == mapId).ToArray())
        {
            try { await observer.SendAsync(WorldCommandId.CollectBoxRemoveCommand, payload, ct); }
            catch (IOException) { }
        }
    }

    public async Task BroadcastBoxSpawnAsync(int mapId, WorldCollectible box, CancellationToken ct)
    {
        var payload = WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, box.Id);
            WorldPayload.WriteString(w, box.Kind);
            WorldPayload.WriteInt32(w, box.X);
            WorldPayload.WriteInt32(w, box.Y);
        });
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == mapId).ToArray())
        {
            try { await observer.SendAsync(WorldCommandId.CollectBoxCreateCommand, payload, ct); }
            catch (IOException) { }
        }
    }

    public async Task BroadcastPortalOperationAsync(int mapId, int portalId, bool working, CancellationToken ct)
    {
        var payload = WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, mapId);
            WorldPayload.WriteInt32(w, portalId);
            w.Write(working);
        });
        foreach (var observer in connections.Values.Where(c => c.Pilot.MapId == mapId).ToArray())
        {
            try { await observer.SendAsync(WorldCommandId.PortalOperationCommand, payload, ct); }
            catch (IOException) { }
        }
    }

    public async Task BroadcastAnnouncementAsync(string message, CancellationToken ct)
    {
        var payload = WorldPayload.Encode(w => WorldPayload.WriteString(w, message));
        foreach (var observer in connections.Values.ToArray())
        {
            try { await observer.SendAsync(WorldCommandId.AnnouncementCommand, payload, ct); }
            catch (IOException) { }
        }
    }
}
