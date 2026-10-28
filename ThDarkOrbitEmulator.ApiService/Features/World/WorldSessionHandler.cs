using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;
using System.Text;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public sealed record WorldPilot(long UserId, string Company, int MapId, int HangarSlot, string ShipId,
    long Credits, long Uridium)
{
    public Guid AccountId { get; init; }
    public bool IsAdmin { get; init; }
    public int FactionId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public WorldShipStats Stats { get; init; } = new(0, 0, 0, 0, 0, []);
    public long Experience { get; init; }
    public long Honor { get; init; }
    public int Level { get; init; } = 1;
    public int X { get; init; } = 2000;
    public int Y { get; init; } = 1300;
    public int ConfigurationTwoSpeed { get; init; }
    public WorldShipStats? ConfigurationTwoStats { get; init; }
    public int BaseSpeedAtLoad { get; init; }
    public IReadOnlyList<WorldCompanion> Companions { get; init; } = [];
    public IReadOnlyList<WorldCompanion> ConfigurationTwoCompanions { get; init; } = [];
}

public interface IWorldPilotResolver
{
    Task<(WorldPilot? Pilot, WorldLoginError Error)> ResolveAsync(long userId, string dosid, CancellationToken ct);
}

public interface IWorldAccountState
{
    Task<(int Slot, string ShipId, long Credits, long Uridium)> GetAsync(GameAuthSession session,
        CancellationToken ct);
    Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, CancellationToken ct) =>
        Task.FromResult<WorldAccountDetails?>(null);
    Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, string shipId, CancellationToken ct) =>
        DetailsAsync(session, slot, ct);
}

public sealed record WorldAccountDetails(WorldShipStats Stats, long Experience, long Honor, int Level,
    int ConfigurationTwoSpeed = 0, WorldShipStats? ConfigurationTwoStats = null,
    IReadOnlyList<WorldCompanion>? Companions = null,
    IReadOnlyList<WorldCompanion>? ConfigurationTwoCompanions = null,
    int BaseSpeedAtLoad = 0);

public sealed class WorldAccountState(LocalHangarStore hangars, EconomyStore economy,
    LocalHangarEquipmentStore equipment, LocalPilotSkillStore skills, LocalPetStore pets,
    IRuntimeVariableRegistry? variables = null, CockpitCatalogStore? catalog = null) : IWorldAccountState
{
    public async Task<(int Slot, string ShipId, long Credits, long Uridium)> GetAsync(GameAuthSession session,
        CancellationToken ct)
    {
        var slot = await hangars.GetActiveSlotAsync(session.AccountId, ct);
        if (!LocalHangars.IsValidSlot(slot)) slot = LocalHangars.DefaultSlot;
        var ship = catalog is null ? LocalHangars.For(session, slot)[slot - 1]
            : (await catalog.HangarsAsync(session, slot, ct)).FirstOrDefault(item => item.SlotNumber == slot)
                ?? LocalHangars.For(session, slot)[slot - 1];
        var balances = await economy.BalancesAsync(session.AccountId, ct);
        long Balance(string id) => balances.FirstOrDefault(item => item.AssetId == id)?.Amount ?? 0;
        return (slot, ship.ShipId, Balance("credits"), Balance("uridium"));
    }

    public async Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, CancellationToken ct)
    {
        var ship = catalog is null ? LocalHangars.For(session, slot)[slot - 1].ShipId
            : (await catalog.HangarsAsync(session, slot, ct)).FirstOrDefault(item => item.SlotNumber == slot)?.ShipId
                ?? LocalHangars.For(session, slot)[slot - 1].ShipId;
        return await DetailsAsync(session, slot, ship, ct);
    }

    public async Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, string shipId, CancellationToken ct)
    {
        var assigned = await equipment.GetAsync(session.AccountId, ct);
        var trained = await skills.GetAsync(session.AccountId, ct);
        var catalog = await economy.ListingsAsync(ct);
        var balances = await economy.BalancesAsync(session.AccountId, ct);
        long Balance(string id) => balances.FirstOrDefault(item => item.AssetId == id)?.Amount ?? 0;
        var spec = LocalHangars.SpecForShip(shipId, slot);
        var baseSpeed = variables is null ? (int?)null : (int)await variables.NumberAsync(
            shipId == "ship_phoenix" || shipId != "ship_pusat-plus" && slot == 2
                ? "world.ship.phoenix_base_speed" : "world.ship.pusat_plus_base_speed", ct);
        var first = WorldShipStatCalculator.Calculate(slot, assigned, trained, catalog, baseSpeed: baseSpeed, shipId: shipId);
        var second = WorldShipStatCalculator.Calculate(slot, assigned, trained, catalog, 2, baseSpeed, shipId);
        var pet = await pets.GetAsync(session.AccountId, ct);
        return new WorldAccountDetails(first, Balance("experience"), Balance("honor"), 1, second.Speed, second,
            WorldCompanions.FromEquipment(assigned, slot, pet),
            WorldCompanions.FromEquipment(assigned, slot, pet, 2), baseSpeed ?? spec.Speed);
    }
}

public sealed class WorldPilotResolver(IGameAuthSessionStore sessions, IWorldAccountState state) : IWorldPilotResolver
{
    public async Task<(WorldPilot? Pilot, WorldLoginError Error)> ResolveAsync(long userId, string dosid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dosid) || dosid.Length > 128)
            return (null, WorldLoginError.UnknownSession);
        var session = await sessions.TryGetByDosidAsync(dosid, ct);
        if (session is null) return (null, WorldLoginError.UnknownSession);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) return (null, WorldLoginError.ExpiredSession);
        if (session.GameUserId != userId || userId <= 0) return (null, WorldLoginError.UserMismatch);
        var company = CompanyCatalog.Find(session.Company);
        if (company is null) return (null, WorldLoginError.CompanyRequired);
        var account = await state.GetAsync(session, ct);
        var details = await state.DetailsAsync(session, account.Slot, account.ShipId, ct);
        return (new WorldPilot(userId, company.Code, int.Parse(company.StarterMap), account.Slot,
            account.ShipId, account.Credits, account.Uridium)
        {
            AccountId = session.AccountId,
            IsAdmin = session.IsAdmin || session.Username.Equals("owner", StringComparison.OrdinalIgnoreCase) || session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase),
            FactionId = company.NumericFactionId,
            UserName = session.Username,
            Stats = details?.Stats ?? new WorldShipStats(0, 0, 0, 0, 0, []),
            Experience = details?.Experience ?? 0,
            Honor = details?.Honor ?? 0,
            Level = details?.Level ?? 1,
            ConfigurationTwoSpeed = details?.ConfigurationTwoSpeed ?? 0,
            ConfigurationTwoStats = details?.ConfigurationTwoStats,
            BaseSpeedAtLoad = details?.BaseSpeedAtLoad ?? 0,
            Companions = details?.Companions ?? [],
            ConfigurationTwoCompanions = details?.ConfigurationTwoCompanions ?? []
        }, default);
    }
}

public sealed class WorldSessionHandler(IWorldPilotResolver resolver, WorldServerOptions options,
    WorldSessionStore? sessions = null, IServerMeshRegistry? registry = null, string? localNodeId = null,
    WorldCollectibles? collectibles = null, WorldNpcs? npcs = null,
    IRuntimeVariableRegistry? variables = null, WorldAdminEvents? adminEvents = null,
    IAdminCommandDispatcher? adminCommands = null)
{
    // Wire format is provisional; command IDs and field order need a redacted Unity fixture.
    public async Task RunAsync(Stream stream, CancellationToken ct = default)
    {
        var reader = new WorldPacketReader(stream, options.MaximumPacketBytes);
        var writer = new WorldPacketWriter(stream, options.MaximumPacketBytes);
        var versionAccepted = false;
        WorldPilot? connected = null;
        WorldConnection? connection = null;
        var handedOff = false;
        int? selectedNpcId = null;
        var knownNpcs = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var npcVitals = new System.Collections.Concurrent.ConcurrentDictionary<int, (int Hp, int Shield)>();
        long? admittedEpoch = null;
        (int Speed, int LaserDamage)? advertisedStats = null;
        int? advertisedPeaceRadius = null;
        bool? advertisedSafeZone = null;
        async Task RefreshSafeZoneAsync(bool announce, CancellationToken token)
        {
            if (connected is null || sessions is null || connection is null) return;
            var station = WorldMapInstance.Starter(connected.MapId).HomeStation;
            var radius = variables is null ? station.PeaceRadius
                : (int)await variables.NumberAsync("world.station.peace_radius", token);
            var (x, y) = sessions.CurrentPosition(connected);
            var dx = (long)x - station.X;
            var dy = (long)y - station.Y;
            var safe = dx * dx + dy * dy <= (long)radius * radius;
            if (announce && advertisedPeaceRadius != radius)
                await connection.SendAsync(WorldCommandId.SpaceStationCommand, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, station.Id);
                    WorldPayload.WriteInt32(w, station.X);
                    WorldPayload.WriteInt32(w, station.Y);
                    WorldPayload.WriteInt32(w, radius);
                }), token);
            if (announce && advertisedSafeZone != safe)
                await connection.SendAsync(WorldCommandId.SafeZoneCommand,
                    new byte[] { (byte)(safe ? 1 : 0) }, token);
            advertisedPeaceRadius = radius;
            advertisedSafeZone = safe;
        }
        async Task<bool> RouteChangedAsync()
        {
            if (connected is null) return false;
            var savedMap = sessions is null ? null : await sessions.SavedMapAsync(connected.UserId, ct);
            if (savedMap is int destination && destination != connected.MapId)
            {
                await SendMapChangeAsync(writer, destination,
                    (await CurrentRouteAsync(destination, ct)) ?? (string.Empty, 0, string.Empty), ct, connection);
                handedOff = true;
                return true;
            }
            var route = await CurrentRouteAsync(connected.MapId, ct);
            if (route is null || (route.Value.OwnerId == localNodeId && route.Value.Epoch == admittedEpoch))
                return false;
            await SendMapChangeAsync(writer, connected.MapId, route.Value, ct, connection);
            handedOff = true;
            return true;
        }
        try
        {
        while (!ct.IsCancellationRequested)
        {
            WorldPacket? packet;
            try
            {
                using var readStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pendingRead = reader.ReadAsync(readStop.Token, preLogin: connected is null);
                while (connected is not null && !pendingRead.IsCompleted)
                {
                    await Task.WhenAny(pendingRead, Task.Delay(TimeSpan.FromSeconds(1), ct));
                    if (!pendingRead.IsCompleted && await RouteChangedAsync())
                    {
                        readStop.Cancel();
                        try { await pendingRead; }
                        catch (OperationCanceledException) { }
                        return;
                    }
                    if (!pendingRead.IsCompleted && sessions is not null && connection is not null)
                    {
                        var externalMove = await sessions.ObserveExternalMoveAsync(connected, ct);
                        if (externalMove is not null)
                        {
                            if (externalMove.TimeToTargetMs == 0)
                            {
                                connected = await sessions.RestoreAsync(connected, ct);
                                connection.Pilot = connected;
                                sessions.RefreshConnectedPilot(connected);
                            }
                            await sessions.BroadcastMoveAsync(connected, externalMove, ct, localMutation: false);
                        }
                        await sessions.ObserveRemotePilotsAsync(connected, connection, ct);
                        foreach (var remoteMove in await sessions.ObserveRemoteMovesAsync(connected, ct))
                            await sessions.SendMoveAsync(connection, remoteMove, ct);
                        if (npcs is not null)
                            foreach (var gone in await SyncNpcsAsync(connection, ct))
                                if (selectedNpcId == gone) selectedNpcId = null;
                        var live = await sessions.LiveStatsAsync(connected, ct);
                        var current = (live.Speed, live.LaserDamage);
                        if (advertisedStats != current)
                        {
                            if (advertisedStats?.Speed != current.Speed)
                            {
                                var fence = registry is not null && localNodeId is not null && admittedEpoch is long epoch
                                    ? new WorldLeaseFence(registry, localNodeId, epoch) : null;
                                try
                                {
                                    var move = await sessions.RetimeForLiveSpeedAsync(connected, ct, fence);
                                    if (move is not null) await sessions.BroadcastMoveAsync(connected, move, ct);
                                }
                                catch (WorldLeaseLostException)
                                {
                                    await RouteChangedAsync();
                                    readStop.Cancel();
                                    try { await pendingRead; }
                                    catch (OperationCanceledException) { }
                                    return;
                                }
                            }
                            await sessions.SendAttributesAsync(connection, connected, live, ct);
                            advertisedStats = current;
                        }
                        await RefreshSafeZoneAsync(true, ct);
                    }
                }
                packet = await pendingRead;
            }
            catch (EndOfStreamException) { return; }
            catch (NativeWorldProtocolException) { return; }
            catch (InvalidDataException)
            {
                await ErrorAsync(writer, WorldLoginError.Malformed, ct);
                return;
            }
            if (packet is null) return;
            if (connected is not null)
            {
                if (await RouteChangedAsync()) return;
                var fence = registry is not null && localNodeId is not null && admittedEpoch is long epoch
                    ? new WorldLeaseFence(registry, localNodeId, epoch) : null;
                try
                {
                    if (packet.Value.Command == WorldCommandId.AdminCommandRequest)
                    {
                        if (!connected.IsAdmin || adminCommands is null)
                        {
                            await ErrorAsync(writer, WorldLoginError.Unauthorized, ct);
                            continue;
                        }
                        var commandText = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadString);
                        try
                        {
                            var command = InGameAdmin.Expand(commandText, connected);
                            var result = await adminCommands.ExecuteAsync(new AdminCommand(command, null, null),
                                InGameAdmin.Actor(connected, localNodeId), ct);
                            await connection!.SendAsync(WorldCommandId.AdminCommandResponse,
                                WorldPayload.Encode(w => { w.Write((byte)1);
                                    WorldPayload.WriteAdminResult(w, System.Text.Json.JsonSerializer.Serialize(result)); }), ct);
                            connected = sessions?.Find(connected.UserId) ?? connected;
                            if (sessions is not null)
                            {
                                var stats = await sessions.LiveStatsAsync(connected, ct);
                                await sessions.SendAttributesAsync(connection, connected, stats, ct);
                                advertisedStats = (stats.Speed, stats.LaserDamage);
                                await WorldEntryPackets.SendAsync(writer, connected, ct,
                                    await sessions.LiveStatsAsync(connected, ct), sessions.CurrentShield(connected),
                                    sessions.CurrentPosition(connected), sessions.CurrentHitPoints(connected),
                                    await RuntimeMapAsync(connected.MapId, ct));
                                await RefreshSafeZoneAsync(false, ct);
                            }
                        }
                        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException)
                        {
                            await connection!.SendAsync(WorldCommandId.AdminCommandResponse,
                                WorldPayload.Encode(w => { w.Write((byte)0);
                                    WorldPayload.WriteString(w, error.Message); }), ct);
                        }
                        continue;
                    }
                    if (packet.Value.Command == WorldCommandId.MoveRequest && sessions is not null)
                    {
                        // Native metadata constructor order: positionX,targetY,targetX,positionY.
                        var request = WorldPayload.Decode(packet.Value.Payload, r =>
                            (PositionX: WorldPayload.ReadInt32(r), TargetY: WorldPayload.ReadInt32(r),
                                TargetX: WorldPayload.ReadInt32(r), PositionY: WorldPayload.ReadInt32(r)));
                        // The client's reported position is diagnostic only; server time determines origin.
                        var wasRadiating = sessions.RadiationWarning(connected.UserId);
                        var move = await sessions.MoveAsync(connected, request.TargetX, request.TargetY, null, ct,
                            fence: fence);
                        await sessions.BroadcastMoveAsync(connected, move, ct);
                        if (wasRadiating != move.RadiationWarning || move.RadiationWarning)
                            await connection!.SendAsync(WorldCommandId.RadiationWarningCommand,
                                new byte[] { (byte)(move.RadiationWarning ? 1 : 0) }, ct);
                        continue;
                    }
                    if (packet.Value.Command == WorldCommandId.ConfigurationRequest && sessions is not null)
                    {
                        var configuration = WorldPayload.Decode(packet.Value.Payload, r => r.ReadByte());
                        var change = await sessions.SwitchAsync(connected, configuration, ct, fence: fence);
                        if (change.Changed)
                        {
                            await sessions.BroadcastMoveAsync(connected, change.Move!, ct);
                            await sessions.BroadcastAttributesAsync(connected, ct);
                            var stats = await sessions.LiveStatsAsync(connected, ct);
                            advertisedStats = (stats.Speed, stats.LaserDamage);
                            if (sessions.CompanionPositions(connected).Count == 0
                                && (connected.Companions.Count > 0 || connected.ConfigurationTwoCompanions.Count > 0))
                                await sessions.BroadcastCompanionsAsync(connected, ct);
                        }
                        else if (change.CooldownRemainingMs > 0)
                            await connection!.SendAsync(WorldCommandId.ConfigurationCooldownCommand,
                                WorldPayload.Encode(w => WorldPayload.WriteInt32(w, change.CooldownRemainingMs)), ct);
                        continue;
                    }
                    if (packet.Value.Command == WorldCommandId.JumpRequest && sessions is not null)
                    {
                        var portalId = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadInt32);
                        var jump = await sessions.JumpAsync(connected, portalId, registry, ct, fence);
                        if (jump is null)
                        {
                            await ErrorAsync(writer, WorldLoginError.Malformed, ct);
                            continue;
                        }
                        connected = jump.Pilot;
                        await connection!.SendAsync(WorldCommandId.MapChangeCommand, WorldPayload.Encode(w =>
                        {
                            WorldPayload.WriteInt32(w, connected.MapId);
                            WorldPayload.WriteString(w, jump.TargetNodeId ?? string.Empty);
                            WorldPayload.WriteString(w, jump.TargetTcpHostPort ?? string.Empty);
                        }), ct);
                        // The source TCP listener must release a pilot whose map lease belongs to
                        // another node. Its shared state is already at the destination arrival point.
                        if (jump.TargetNodeId is not null && localNodeId is not null
                            && jump.TargetNodeId != localNodeId)
                        {
                            handedOff = true;
                            return;
                        }
                        admittedEpoch = (await CurrentRouteAsync(connected.MapId, ct))?.Epoch;
                        await WorldEntryPackets.SendAsync(writer, connected, ct,
                            await sessions.LiveStatsAsync(connected, ct), sessions.CurrentShield(connected),
                            sessions.CurrentPosition(connected), sessions.CurrentHitPoints(connected),
                            await RuntimeMapAsync(connected.MapId, ct));
                        await RefreshSafeZoneAsync(false, ct);
                        if (sessions is not null)
                        {
                            var stats = await sessions.LiveStatsAsync(connected, ct);
                            advertisedStats = (stats.Speed, stats.LaserDamage);
                        }
                        await SendBoxesAsync(writer, connected, ct);
                        await SendNpcsAsync(writer, connected, knownNpcs, npcVitals, ct);
                        await SendAnnouncementAsync(writer, ct);
                        continue;
                    }
                    if (packet.Value.Command == WorldCommandId.CollectBoxRequest && sessions is not null
                        && collectibles is not null)
                    {
                        var boxId = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadInt32);
                        var result = await collectibles.CollectAsync(connected, boxId, sessions, ct, fence);
                        if (result.Status != "collected")
                            await connection!.SendAsync(WorldCommandId.CollectRewardCommand,
                                WorldPayload.Encode(w => { WorldPayload.WriteInt32(w, boxId);
                                    WorldPayload.WriteString(w, result.Status); WorldPayload.WriteInt32(w, 0); }), ct);
                        continue;
                    }
                    if (packet.Value.Command == WorldCommandId.SelectShipRequest && npcs is not null)
                    {
                        var id = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadInt32);
                        var target = await npcs.SelectAsync(connected, id, ct, fence, sessions);
                        selectedNpcId = target?.Id;
                        await connection!.SendAsync(WorldCommandId.TargetLockCommand, WorldPayload.Encode(w =>
                        {
                            WorldPayload.WriteInt32(w, id);
                            WorldPayload.WriteInt32(w, target?.Hp ?? 0);
                            WorldPayload.WriteInt32(w, target?.Shield ?? 0);
                            WorldPayload.WriteInt32(w, target?.MaxHp ?? 0);
                            WorldPayload.WriteInt32(w, target?.MaxShield ?? 0);
                        }), ct);
                        continue;
                    }
                    if (packet.Value.Command is WorldCommandId.LaserFireRequest or WorldCommandId.RocketFireRequest &&
                        npcs is not null && sessions is not null)
                    {
                        var id = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadInt32);
                        if (selectedNpcId != id && !await npcs.CanRetryRewardAsync(connected, id, ct))
                        {
                            await SendLaserFireResultAsync(connection!, id, "unselected", ct);
                            continue;
                        }
                        var hit = await npcs.AttackAsync(connected, id, sessions, ct, fence,
                            rocket: packet.Value.Command == WorldCommandId.RocketFireRequest);
                        if (hit.Status != "hit")
                        {
                            await SendLaserFireResultAsync(connection!, id, hit.Status, ct);
                            if (hit.Status is "missing" or "destroyed") selectedNpcId = null;
                        }
                        else await SendLaserFireResultAsync(connection!, id, "hit", ct);
                        if (hit.Destroyed) selectedNpcId = null;
                        continue;
                    }
                }
                catch (WorldLeaseLostException)
                {
                    var destinationMap = await sessions!.SavedMapAsync(connected.UserId, ct) ?? connected.MapId;
                    await SendMapChangeAsync(writer, destinationMap,
                        (await CurrentRouteAsync(destinationMap, ct)) ?? (string.Empty, 0, string.Empty), ct);
                    handedOff = true;
                    return;
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or DecoderFallbackException)
                {
                    await ErrorAsync(writer, WorldLoginError.Malformed, ct);
                    return;
                }
                await ErrorAsync(writer, WorldLoginError.UnknownCommand, ct);
                return;
            }
            try
            {
                switch (packet.Value.Command)
                {
                    case WorldCommandId.VersionRequest:
                    {
                        var requested = WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadString);
                        versionAccepted = requested == options.Version;
                        await writer.WriteAsync(WorldCommandId.VersionCommand,
                            WorldPayload.Encode(w => { WorldPayload.WriteString(w, options.Version); w.Write(versionAccepted); }), ct);
                        break;
                    }
                    case WorldCommandId.LoginRequest:
                    {
                        if (!versionAccepted)
                        {
                            await ErrorAsync(writer, WorldLoginError.UnsupportedVersion, ct);
                            return;
                        }
                        var login = WorldPayload.Decode(packet.Value.Payload, r => new
                        {
                            UserId = WorldPayload.ReadInt64(r),
                            Dosid = WorldPayload.ReadString(r),
                            Version = WorldPayload.ReadString(r),
                            InstanceId = WorldPayload.ReadString(r)
                        });
                        if (login.Version != options.Version || login.InstanceId.Length is 0 or > 128)
                        {
                            await ErrorAsync(writer, WorldLoginError.UnsupportedVersion, ct);
                            return;
                        }
                        var (pilot, error) = await resolver.ResolveAsync(login.UserId, login.Dosid, ct);
                        if (pilot is null)
                        {
                            await ErrorAsync(writer, error, ct);
                            return;
                        }
                        var restored = sessions is null ? pilot : await sessions.RestoreAsync(pilot, ct);
                        var loginRoute = await CurrentRouteAsync(restored.MapId, ct);
                        if (loginRoute is not null && loginRoute.Value.OwnerId != localNodeId)
                        {
                            await SendMapChangeAsync(writer, restored.MapId, loginRoute.Value, ct);
                            return;
                        }
                        connected = sessions is null ? restored : await sessions.EnterAsync(restored, ct);
                        admittedEpoch = loginRoute?.Epoch;
                        await writer.WriteAsync(WorldCommandId.LoginResponse, WorldPayload.Encode(w =>
                        {
                            w.Write((byte)1);
                            WorldPayload.WriteInt64(w, pilot.UserId);
                            WorldPayload.WriteString(w, pilot.Company);
                            w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(connected.MapId));
                            w.Write((byte)pilot.HangarSlot);
                            WorldPayload.WriteString(w, pilot.ShipId);
                            WorldPayload.WriteInt64(w, pilot.Credits);
                            WorldPayload.WriteInt64(w, pilot.Uridium);
                        }), ct);
                        // Baselines are taken before the client can see the entry burst: a runtime
                        // variable change or another replica's move landing after the packets are
                        // flushed must differ from them, otherwise the idle poll never announces it.
                        var entryStats = sessions is null ? null : await sessions.LiveStatsAsync(connected, ct);
                        if (entryStats is not null)
                        {
                            advertisedStats = (entryStats.Speed, entryStats.LaserDamage);
                            await sessions!.PrimeObservedTravelAsync(connected, ct);
                        }
                        await WorldEntryPackets.SendAsync(writer, connected, ct, entryStats,
                            sessions?.CurrentShield(connected), sessions?.CurrentPosition(connected),
                            sessions?.CurrentHitPoints(connected), await RuntimeMapAsync(connected.MapId, ct));
                        await SendBoxesAsync(writer, connected, ct);
                        await SendNpcsAsync(writer, connected, knownNpcs, npcVitals, ct);
                        await SendAnnouncementAsync(writer, ct);
                        if (sessions is not null && sessions.Configuration(connected.UserId) != 1)
                            await sessions.SendAttributesAsync(writer, connected, ct);
                        if (sessions is not null && sessions.CompanionPositions(connected).Count > 0)
                            await sessions.SendCompanionsAsync(writer, connected, ct);
                        if (sessions is not null)
                        {
                            connection = new WorldConnection(connected, writer) { KnownNpcs = knownNpcs, NpcVitals = npcVitals };
                            sessions.Attach(connection);
                            await RefreshSafeZoneAsync(false, ct);
                        }
                        break;
                    }
                    default:
                        await ErrorAsync(writer, WorldLoginError.UnknownCommand, ct);
                        return;
                }
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or DecoderFallbackException)
            {
                await ErrorAsync(writer, WorldLoginError.Malformed, ct);
                return;
            }
        }
        }
        finally { if (connected is not null && sessions is not null)
        {
            if (handedOff) sessions.DetachForHandoff(connected, connection);
            else await sessions.LeaveAsync(connected, connection, CancellationToken.None);
        } }
    }

    private async Task<WorldMapInstance> RuntimeMapAsync(int mapId, CancellationToken ct)
    {
        var map = WorldMapInstance.Starter(mapId);
        if (variables is null) return map;
        var peace = (int)await variables.NumberAsync("world.station.peace_radius", ct);
        var jump = (int)await variables.NumberAsync("world.portal.jump_radius", ct);
        return map with { HomeStation = map.HomeStation with { PeaceRadius = peace },
            Portals = map.Portals.Select(portal => portal with { JumpRadius = jump }).ToArray() };
    }

    private static Task ErrorAsync(WorldPacketWriter writer, WorldLoginError error, CancellationToken ct) =>
        writer.WriteAsync(WorldCommandId.LoginErrorCommand, new byte[] { (byte)error }, ct);

    private async Task<(string OwnerId, long Epoch, string Endpoint)?> CurrentRouteAsync(int mapId,
        CancellationToken ct)
    {
        if (registry is null || localNodeId is null) return null;
        var snapshot = await registry.GetSnapshotAsync(ct);
        if (snapshot.Nodes.Length == 0) return (string.Empty, 0, string.Empty);
        var lease = snapshot.Leases.FirstOrDefault(item => item.MapId == mapId
            && item.ExpiresAtUtc > DateTimeOffset.UtcNow);
        if (lease is null) return (string.Empty, 0, string.Empty);
        var owner = snapshot.Nodes.FirstOrDefault(item => item.NodeId == lease.NodeId
            && item.Status == ServerMeshNodeStatus.Active);
        if (owner is null) return (string.Empty, 0, string.Empty);
        // Development simulations share only their physical host's listener.
        var local = snapshot.Nodes.FirstOrDefault(item => item.NodeId == localNodeId);
        if (owner.Simulated && local is not null && !local.Simulated
            && local.Status == ServerMeshNodeStatus.Active
            && local.WorldTcpHost == owner.WorldTcpHost && local.WorldTcpPort == owner.WorldTcpPort)
            return (localNodeId, lease.Epoch, $"{owner.WorldTcpHost}:{owner.WorldTcpPort}");
        return (owner.NodeId, lease.Epoch, $"{owner.WorldTcpHost}:{owner.WorldTcpPort}");
    }

    private static Task SendLaserFireResultAsync(WorldConnection connection, int npcId, string status,
        CancellationToken ct) =>
        connection.SendAsync(WorldCommandId.LaserFireResultCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, npcId);
            WorldPayload.WriteString(w, status);
        }), ct);

    private static Task SendMapChangeAsync(WorldPacketWriter writer, int mapId,
        (string OwnerId, long Epoch, string Endpoint) route, CancellationToken ct,
        WorldConnection? connection = null)
    {
        var payload = WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, mapId);
            WorldPayload.WriteString(w, route.OwnerId);
            WorldPayload.WriteString(w, route.Endpoint);
        });
        return connection is null
            ? writer.WriteAsync(WorldCommandId.MapChangeCommand, payload, ct)
            : connection.SendAsync(WorldCommandId.MapChangeCommand, payload, ct);
    }

    private async Task SendBoxesAsync(WorldPacketWriter writer, WorldPilot pilot, CancellationToken ct)
    {
        if (collectibles is null) return;
        foreach (var box in await collectibles.VisibleAsync(pilot.MapId, ct))
            await writer.WriteAsync(WorldCommandId.CollectBoxCreateCommand, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, box.Id);
                WorldPayload.WriteString(w, box.Kind);
                WorldPayload.WriteInt32(w, box.X);
                WorldPayload.WriteInt32(w, box.Y);
            }), ct);
    }

    private async Task SendNpcsAsync(WorldPacketWriter writer, WorldPilot pilot,
        System.Collections.Concurrent.ConcurrentDictionary<int, bool> known,
        System.Collections.Concurrent.ConcurrentDictionary<int, (int Hp, int Shield)> vitals, CancellationToken ct)
    {
        if (npcs is null) return;
        known.Clear();
        vitals.Clear();
        foreach (var npc in await npcs.VisibleForObserversAsync(pilot.MapId, ct))
        {
            await writer.WriteAsync(WorldCommandId.ShipCreateCommand, WorldNpcPackets.Create(npc), ct);
            known[npc.Id] = true;
            vitals[npc.Id] = (npc.Hp, npc.Shield);
        }
    }

    // Idle roster sync: shared NPC state changes without this socket's involvement (respawn
    // cycle, runtime spawn-count variables, admin events, kills handled by another replica).
    // Returns the ids announced as destroyed so the caller can drop a stale target lock.
    private async Task<IReadOnlyList<int>> SyncNpcsAsync(WorldConnection connection, CancellationToken ct)
    {
        var visible = await npcs!.VisibleForObserversAsync(connection.Pilot.MapId, ct);
        var visibleIds = visible.Select(n => n.Id).ToHashSet();
        foreach (var npc in visible)
        {
            // A tombstoned id stays hidden until a poll sees it absent; a later respawn is then new.
            if (connection.KnownNpcs.TryGetValue(npc.Id, out var shownNow))
            {
                // Damage applied on another replica, or a new spawn cycle that reset a survivor.
                if (shownNow && connection.NpcVitals.TryGetValue(npc.Id, out var last)
                    && last != (npc.Hp, npc.Shield))
                {
                    await connection.SendAsync(WorldCommandId.NpcHealthCommand, WorldNpcPackets.Health(npc), ct);
                    connection.NpcVitals[npc.Id] = (npc.Hp, npc.Shield);
                }
                continue;
            }
            await connection.SendAsync(WorldCommandId.ShipCreateCommand, WorldNpcPackets.Create(npc), ct);
            connection.KnownNpcs[npc.Id] = true;
            connection.NpcVitals[npc.Id] = (npc.Hp, npc.Shield);
        }
        var gone = new List<int>();
        foreach (var (id, shown) in connection.KnownNpcs.ToArray())
        {
            if (visibleIds.Contains(id)) continue;
            if (shown)
            {
                await connection.SendAsync(WorldCommandId.ShipDestroyCommand,
                    WorldPayload.Encode(w => WorldPayload.WriteInt32(w, id)), ct);
                gone.Add(id);
            }
            connection.KnownNpcs.TryRemove(id, out _);
            connection.NpcVitals.TryRemove(id, out _);
        }
        return gone;
    }

    private async Task SendAnnouncementAsync(WorldPacketWriter writer, CancellationToken ct)
    {
        var latest = adminEvents is null ? null : await adminEvents.LatestAsync(ct);
        if (latest is not null)
            await writer.WriteAsync(WorldCommandId.AnnouncementCommand,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, latest.Message)), ct);
    }
}
