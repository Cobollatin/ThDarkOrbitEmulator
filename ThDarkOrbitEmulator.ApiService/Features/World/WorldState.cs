using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Emulator world geometry pending a redacted native map fixture.
public sealed record WorldStation(int Id, int X, int Y, int PeaceRadius);
public sealed record WorldPortal(int Id, int X, int Y, int DestinationMapId, int ArrivalX, int ArrivalY,
    int JumpRadius = 500);
public enum WorldRespawnLocation { Base, DeathLocation, NearestPortal }
public sealed record WorldMapInstance(int MapId, int FactionId, int Width, int Height,
    WorldStation HomeStation, IReadOnlyList<WorldPortal> Portals)
{
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

    private static WorldMapInstance Create(int map, int faction, bool adjacent) =>
        new(map, faction, 21000, 13100,
            new WorldStation(map * 100, adjacent ? 19000 : 2000, 1300, 1000),
            adjacent
                ? [new WorldPortal(map * 100 + 1, 1500, 1300, map - 1, 18500, 1300)]
                : [new WorldPortal(map * 100 + 1, 19500, 1300, map + 1, 2500, 1300),
                   new WorldPortal(map * 100 + 2, 19500, 11800, map + 2, 2500, 11800),
                   .. Enumerable.Range(0, 5).Select(index => new WorldPortal(map * 100 + 3 + index,
                       17500 + index * 500, 6500, GateMap(faction, index), 2000, 1300))]);
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
public sealed record WorldRuntimeSnapshot(int MapId, int Configuration, long Shield,
    long LastSwitchUnixMilliseconds, WorldTravel? Travel, long? HitPoints = null,
    long LastHazardUnixMilliseconds = 0, bool RadiationWarning = false, bool RepairBotActive = false,
    string? DeathId = null);
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

public sealed record WorldPresence(WorldPilot Pilot, DateTimeOffset SeenAtUtc);

public sealed class WorldSessionStore(IDistributedCache cache, IServerMeshMutex? meshMutex = null,
    IRuntimeVariableRegistry? variables = null, TimeProvider? clock = null, EconomyStore? economy = null,
    IWorldRepairWallet? repairWallet = null)
{
    private readonly TimeProvider combatClock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, WorldPilot>> connected = new();
    private readonly ConcurrentDictionary<long, WorldConnection> connections = new();
    private readonly ConcurrentDictionary<long, WorldTravel> travel = new();
    private readonly ConcurrentDictionary<long, WorldTravel> announcedTravel = new();
    private readonly ConcurrentDictionary<long, (int X, int Y)> announcedPosition = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, WorldTravel>> remoteAnnounced = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, (int X, int Y)>> remotePositions = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> remoteVisible = new();
    // Map-level fanout: every idle observer on this node shares one presence + saved-state read per map.
    public static readonly TimeSpan RemoteSnapshotTtl = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PresenceHeartbeatInterval = TimeSpan.FromSeconds(15);
    private sealed record RemoteEntry(WorldPresence Presence, string? Saved);
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
            var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
            if (saved is not null)
            {
                try
                {
                    var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                    if (position is not null && position.MapId != pilot.MapId)
                    {
                        if (connected.TryGetValue(pilot.MapId, out var local))
                            ((ICollection<KeyValuePair<long, WorldPilot>>)local)
                                .Remove(new(pilot.UserId, pilot));
                        (absent.TryGetValue(pilot.MapId, out var gone) ? gone : absent[pilot.MapId] = []).Add(pilot.UserId);
                        continue;
                    }
                }
                catch (JsonException) { }
            }
            if (HeartbeatDue(pilot))
                (present.TryGetValue(pilot.MapId, out var due) ? due : present[pilot.MapId] = []).Add(pilot);
        }
        foreach (var mapId in present.Keys.Union(absent.Keys).ToArray())
            await WritePresenceBatchAsync(mapId, present.GetValueOrDefault(mapId) ?? [],
                absent.GetValueOrDefault(mapId) ?? [], ct);
    }
    private static string Key(long userId) => $"world:position:{userId}";
    private static string RuntimeKey(long userId) => $"world:runtime:{userId}";
    private static string CombatKey(long userId) => $"world:combat:{userId}";
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
    private static TimeSpan RespawnCooldown(WorldRespawnLocation location) => location switch
    {
        WorldRespawnLocation.DeathLocation => TimeSpan.FromSeconds(30),
        WorldRespawnLocation.NearestPortal => TimeSpan.FromSeconds(15),
        _ => TimeSpan.Zero
    };

    public async Task MarkCombatAsync(WorldPilot pilot, CancellationToken ct)
    {
        await cache.SetStringAsync(CombatKey(pilot.UserId), combatClock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
        await SetRepairBotAsync(pilot, false, ct);
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
                await PersistRuntimeAsync(pilot, ct);
                await cache.SetStringAsync(HullRecoveryKey(pilot.UserId), now.ToUnixTimeMilliseconds().ToString(),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, ct);
            }
            finally { gate.Release(); }
        }
    }

    private async Task SetRepairBotAsync(WorldPilot pilot, bool active, CancellationToken ct)
    {
        if (repairBots.GetValueOrDefault(pilot.UserId) == active) return;
        repairBots[pilot.UserId] = active;
        await PersistRuntimeAsync(pilot, ct);
    }

    public bool RepairBotActive(long userId) => repairBots.GetValueOrDefault(userId);

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
                var startedRadiating = !RadiationWarning(pilot.UserId);
                radiationWarnings[pilot.UserId] = true;
                await ApplyBoundaryDamageAsync(pilot, combatClock.GetUtcNow(), ct);
                if (startedRadiating) await SendRadiationWarningAsync(pilot.UserId, true, ct);
            }
            finally { gate.Release(); }
        }
    }

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
            var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
            if (saved is not null)
            {
                try
                {
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
        foreach (var (entry, saved) in (await RemoteSnapshotAsync(mapId, ct)).Entries)
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
        foreach (var map in connected.Values)
            if (map.TryRemove(pilot.UserId, out var previous) && previous.MapId != pilot.MapId)
                await WritePresenceAsync(previous, false, ct);
        connected.GetOrAdd(pilot.MapId, _ => new ConcurrentDictionary<long, WorldPilot>())[pilot.UserId] = pilot;
        await HeartbeatPresenceCoreAsync(pilot, ct);
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
    public async Task<int?> SavedMapAsync(long userId, CancellationToken ct)
    {
        var saved = await cache.GetStringAsync(Key(userId), ct);
        if (saved is null) return null;
        try
        {
            var position = JsonSerializer.Deserialize<WorldPosition>(saved);
            return position is not null && (position.MapId is >= 1 and <= 16 || WorldMapInstance.IsGate(position.MapId))
                ? position.MapId : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<WorldPilot> RefreshAsync(WorldPilot pilot, CancellationToken ct)
    {
        travel.TryRemove(pilot.UserId, out _);
        var saved = await cache.GetStringAsync(Key(pilot.UserId), ct);
        if (saved is not null)
        {
            try
            {
                var position = JsonSerializer.Deserialize<WorldPosition>(saved);
                if (position is not null && WorldMapInstance.Starter(position.MapId) is { } map
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

    public Task PersistAsync(WorldPilot pilot, CancellationToken ct) => cache.SetStringAsync(Key(pilot.UserId),
        JsonSerializer.Serialize(new WorldPosition(pilot.MapId, pilot.X, pilot.Y)),
        new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);

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
        remotePositions.TryRemove(pilot.UserId, out _);
        configurations.TryRemove(pilot.UserId, out _);
        lastSwitches.TryRemove(pilot.UserId, out _);
        shields.TryRemove(pilot.UserId, out _);
        hitPoints.TryRemove(pilot.UserId, out _);
        lastHazards.TryRemove(pilot.UserId, out _);
        radiationWarnings.TryRemove(pilot.UserId, out _);
        repairBots.TryRemove(pilot.UserId, out _);
    }

    public void Attach(WorldConnection connection) => connections[connection.Pilot.UserId] = connection;

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
            announcedTravel.TryRemove(pilot.UserId, out _);
            var stationary = SavedStationaryPosition(saved, pilot.MapId);
            if (stationary is not null && announcedPosition.TryGetValue(pilot.UserId, out var previousPosition)
                && previousPosition != stationary.Value)
            {
                announcedPosition[pilot.UserId] = stationary.Value;
                travel.TryRemove(pilot.UserId, out _);
                return new WorldMoveResult(pilot.UserId, stationary.Value.X, stationary.Value.Y, 0, false);
            }
            return null;
        }
        if (announcedTravel.TryGetValue(pilot.UserId, out var previous) && previous == current)
            return null;
        announcedTravel[pilot.UserId] = current;
        announcedPosition[pilot.UserId] = (current.TargetX, current.TargetY);
        travel[pilot.UserId] = current;
        var position = current.PositionAt(DateTimeOffset.UtcNow);
        var distance = Math.Sqrt(Math.Pow(current.TargetX - position.X, 2)
            + Math.Pow(current.TargetY - position.Y, 2));
        return new WorldMoveResult(pilot.UserId, current.TargetX, current.TargetY,
            (int)Math.Ceiling(distance * 1000d / current.Speed), false);
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
        var visible = remoteVisible.GetOrAdd(observer.UserId, _ => new());
        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<long>();
        foreach (var (entry, saved) in (await RemoteSnapshotAsync(observer.MapId, ct)).Entries)
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
            present.Add(remote.UserId);
            if (!visible.TryAdd(remote.UserId, 0)) continue;
            remotePositions.GetOrAdd(observer.UserId, _ => new())[remote.UserId] = (remote.X, remote.Y);
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
        foreach (var id in visible.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            if (!visible.TryRemove(id, out _)) continue;
            remoteAnnounced.GetOrAdd(observer.UserId, _ => new()).TryRemove(id, out _);
            remotePositions.GetOrAdd(observer.UserId, _ => new()).TryRemove(id, out _);
            await connection.SendAsync(WorldCommandId.PilotRemoveCommand,
                WorldPayload.Encode(w => WorldPayload.WriteInt64(w, id)), ct);
        }
    }

    // Pilots whose sockets live on another node reach this observer through shared presence and travel.
    public async Task<IReadOnlyList<WorldMoveResult>> ObserveRemoteMovesAsync(WorldPilot observer,
        CancellationToken ct)
    {
        var seen = remoteAnnounced.GetOrAdd(observer.UserId, _ => new());
        var positions = remotePositions.GetOrAdd(observer.UserId, _ => new());
        var now = DateTimeOffset.UtcNow;
        var moves = new List<WorldMoveResult>();
        foreach (var (entry, saved) in (await RemoteSnapshotAsync(observer.MapId, ct)).Entries)
        {
            var remote = entry.Pilot with { MapId = observer.MapId };
            if (remote.UserId == observer.UserId || connections.ContainsKey(remote.UserId)
                || WorldMapInstance.IsGate(observer.MapId) && remote.AccountId != observer.AccountId
                || now - entry.SeenAtUtc >= TimeSpan.FromMinutes(2)) continue;
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
                            positions[remote.UserId] = (stationary.X, stationary.Y);
                            moves.Add(new WorldMoveResult(remote.UserId, stationary.X,
                                stationary.Y, 0, false));
                        }
                    }
                    catch (JsonException) { }
                }
                continue;
            }
            if (seen.TryGetValue(remote.UserId, out var previous) && previous == current) continue;
            seen[remote.UserId] = current;
            positions[remote.UserId] = (current.TargetX, current.TargetY);
            var position = current.PositionAt(now);
            var distance = Math.Sqrt(Math.Pow(current.TargetX - position.X, 2)
                + Math.Pow(current.TargetY - position.Y, 2));
            if (distance > 0)
                moves.Add(new WorldMoveResult(remote.UserId, current.TargetX, current.TargetY,
                    (int)Math.Ceiling(distance * 1000d / current.Speed), false));
        }
        return moves;
    }

    public async Task SendMoveAsync(WorldConnection observer, WorldMoveResult move, CancellationToken ct)
    {
        try { await observer.SendAsync(WorldCommandId.MoveCommand, MovePayload(move), ct); }
        catch (IOException) { /* A disconnected observer is removed by its handler. */ }
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
            entries.Add(new(presence, connections.ContainsKey(presence.Pilot.UserId) ? null
                : await cache.GetStringAsync(Key(presence.Pilot.UserId))));
        return new(DateTimeOffset.UtcNow, entries);
    }

    public void DetachForHandoff(WorldPilot pilot, WorldConnection? connection)
    {
        if (connection is not null)
            ((ICollection<KeyValuePair<long, WorldConnection>>)connections)
                .Remove(new(pilot.UserId, connection));
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
                if (pilot.MapId != admittedMap) throw new WorldLeaseLostException();
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
            var warning = result.RadiationWarning || (RadiationWarning(pilot.UserId)
                && !WorldMovement.IsInsideBoundary(CurrentPosition(pilot, now),
                    WorldMapInstance.Starter(pilot.MapId)));
            radiationWarnings[pilot.UserId] = warning;
            if (warning) repairBots[pilot.UserId] = false;
            configurations[pilot.UserId] = selected;
            await cache.SetStringAsync(Key(pilot.UserId), JsonSerializer.Serialize(next),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            await PersistRuntimeAsync(pilot, ct);
            if (warning) await ApplyBoundaryDamageAsync(pilot, now, ct);
            else await RepairAtBaseAsync(pilot, now, ct);
            return result with { RadiationWarning = warning };
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
                if (pilot.MapId != admittedMap)
                {
                    if (expectedMapId is not null) throw new WorldMapChangedException();
                    throw new WorldLeaseLostException();
                }
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
            return new(true, 0, move);
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
                if (pilot.MapId != admittedMap) throw new WorldLeaseLostException();
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
            return move;
        }
        finally { gate.Release(); }
    }

    private async Task PersistRuntimeAsync(WorldPilot pilot, CancellationToken ct,
        bool clearDeathId = false) =>
        await cache.SetStringAsync(RuntimeKey(pilot.UserId), JsonSerializer.Serialize(new WorldRuntimeSnapshot(
            pilot.MapId, Configuration(pilot.UserId), CurrentShield(pilot),
            lastSwitches.TryGetValue(pilot.UserId, out var last) ? last.ToUnixTimeMilliseconds() : 0,
            travel.GetValueOrDefault(pilot.UserId), CurrentHitPoints(pilot),
            lastHazards.TryGetValue(pilot.UserId, out var hazard) ? hazard.ToUnixTimeMilliseconds() : 0,
            RadiationWarning(pilot.UserId), RepairBotActive(pilot.UserId),
            clearDeathId ? null : await DeathIdAsync(pilot.UserId, ct))),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);

    private async Task ApplyBoundaryDamageAsync(WorldPilot pilot, DateTimeOffset now, CancellationToken ct)
    {
        if (lastHazards.TryGetValue(pilot.UserId, out var last) && now - last < TimeSpan.FromSeconds(1)) return;
        repairBots[pilot.UserId] = false;
        lastHazards[pilot.UserId] = now;
        var damage = variables is null ? Math.Max(1, ActiveStats(pilot).MaxHitPoints / 100)
            : (long)await variables.NumberAsync("world.radiation.damage_per_attempt", ct);
        if (damage == 0) damage = Math.Max(1, ActiveStats(pilot).MaxHitPoints / 100);
        hitPoints[pilot.UserId] = Math.Max(0, CurrentHitPoints(pilot) - damage);
        if (hitPoints[pilot.UserId] == 0)
            pilot = await RecordDestructionAsync(pilot, ct);
        await PersistRuntimeAsync(pilot, ct);
    }

    public async Task RepairAtBaseAsync(WorldPilot pilot, DateTimeOffset now, CancellationToken ct)
    {
        var station = WorldMapInstance.Starter(pilot.MapId).HomeStation;
        var position = CurrentPosition(pilot, now);
        var target = Destination(pilot);
        var dx = (long)position.X - station.X;
        var dy = (long)position.Y - station.Y;
        var peaceRadius = variables is null ? station.PeaceRadius
            : (int)await variables.NumberAsync("world.station.peace_radius", ct);
        if (position != target || dx * dx + dy * dy > (long)peaceRadius * peaceRadius) return;
        var stats = ActiveStats(pilot);
        var current = CurrentHitPoints(pilot);
        if (current <= 0) return;
        var shield = CurrentShield(pilot);
        if (current >= stats.MaxHitPoints && shield >= stats.MaxShield) return;
        if (current < stats.MaxHitPoints)
        {
            var repair = variables is null ? 0 : (long)await variables.NumberAsync("world.station.repair_hp_per_tick", ct);
            hitPoints[pilot.UserId] = Math.Min(stats.MaxHitPoints,
                current + (repair > 0 ? repair : Math.Max(1, stats.MaxHitPoints / 50)));
        }
        if (shield < stats.MaxShield)
            shields[pilot.UserId] = Math.Min(stats.MaxShield,
                shield + Math.Max(1, stats.MaxShield / 50));
        await PersistRuntimeAsync(pilot, ct);
    }

    // State polling can repair hull. Serialize that write with lease reassignment
    // and reload the shared pilot state before applying it.
    public async Task RepairAtBaseFencedAsync(WorldPilot pilot, DateTimeOffset now,
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
                if (pilot.MapId != admittedMap) throw new WorldLeaseLostException();
            }
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            await RepairAtBaseAsync(pilot, now, ct);
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
            if (pilot.MapId != admittedMap) throw new WorldLeaseLostException();
            await CheckLeaseAsync(pilot.MapId, fence, ct);
            if (CurrentHitPoints(pilot) <= 0) throw new WorldPilotDestroyedException();
            var portal = WorldMapInstance.Starter(pilot.MapId).Portals.FirstOrDefault(p => p.Id == portalId);
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
            if (!WorldMapInstance.IsGate(pilot.MapId) && await PortalCombatLockedAsync(pilot, ct)) return null;
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
            if (pilot.MapId != admittedMap) throw new WorldLeaseLostException();
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
            if ((location is WorldRespawnLocation.DeathLocation or WorldRespawnLocation.NearestPortal)
                && WorldMapInstance.IsGate(pilot.MapId))
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
                WorldRespawnLocation.NearestPortal => AtNearestPortal(pilot),
                _ => pilot with { MapId = baseMapId, X = station.X, Y = station.Y }
            };
            hitPoints[pilot.UserId] = Math.Max(1, ActiveStats(pilot).MaxHitPoints / 10);
            shields[pilot.UserId] = 0;
            await ClearRepairRuntimeAsync(pilot.UserId, ct);
            var repairedDeathId = repairWallet is not null && pilot.AccountId != Guid.Empty
                ? await DeathIdAsync(pilot.UserId, ct, pilot.AccountId) : null;
            if (repairedDeathId is not null)
                await repairWallet!.StageRepairAsync(pilot.AccountId, repairedDeathId,
                    new WorldRepairOutcome(respawned.MapId, respawned.X, respawned.Y,
                        CurrentHitPoints(pilot), CurrentShield(pilot)), location, combatClock.GetUtcNow(), ct);
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

    private static WorldPilot AtNearestPortal(WorldPilot pilot)
    {
        var portal = WorldMapInstance.Starter(pilot.MapId).Portals
            .MinBy(p => (long)(p.X - pilot.X) * (p.X - pilot.X)
                + (long)(p.Y - pilot.Y) * (p.Y - pilot.Y));
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
        if (localMutation && travel.TryGetValue(pilot.UserId, out var current))
        {
            announcedTravel[pilot.UserId] = current;
            announcedPosition[pilot.UserId] = (current.TargetX, current.TargetY);
        }
        var payload = MovePayload(move);
        var audience = connections.Values.Where(c => c.Pilot.MapId == pilot.MapId &&
            (!WorldMapInstance.IsGate(pilot.MapId) || c.Pilot.AccountId == pilot.AccountId)).ToArray();
        foreach (var observer in audience)
        {
            try { await observer.SendAsync(WorldCommandId.MoveCommand, payload, ct); }
            catch (IOException) { /* A disconnected observer is removed by its handler. */ }
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
        var expectedMapId = pilot.MapId;
        pilot = await RestoreAsync(pilot, ct);
        if (pilot.MapId != expectedMapId || CurrentHitPoints(pilot) <= 0) return;
        await MarkCombatAsync(pilot, ct);
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
        travel.TryRemove(pilot.UserId, out _);
        await PersistAsync(pilot, ct);
        if (connected.TryGetValue(pilot.MapId, out var occupants))
            occupants[pilot.UserId] = pilot;
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
