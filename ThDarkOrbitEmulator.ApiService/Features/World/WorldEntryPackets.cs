namespace ThDarkOrbitEmulator.ApiService.Features.World;

public static class WorldEntryPackets
{
    // Local diagnostic command order and fields; not a recovered libcom serialization.
    public static async Task SendAsync(WorldPacketWriter writer, WorldPilot pilot, CancellationToken ct,
        WorldShipStats? activeStats = null, long? currentShield = null, (int X, int Y)? position = null,
        long? currentHitPoints = null, WorldMapInstance? runtimeMap = null,
        bool repairBotActive = false)
    {
        var stats = activeStats ?? pilot.Stats;
        var shield = currentShield ?? stats.MaxShield;
        var (pilotX, pilotY) = position ?? (pilot.X, pilot.Y);
        var map = runtimeMap ?? WorldMapInstance.Starter(pilot.MapId);
        await writer.WriteAsync(WorldCommandId.MapInitializationCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, map.MapId);
            WorldPayload.WriteInt32(w, map.Width);
            WorldPayload.WriteInt32(w, map.Height);
            WorldPayload.WriteInt32(w, map.FactionId);
        }), ct);
        await writer.WriteAsync(WorldCommandId.SpaceStationCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt32(w, map.HomeStation.Id);
            WorldPayload.WriteInt32(w, map.HomeStation.X);
            WorldPayload.WriteInt32(w, map.HomeStation.Y);
            WorldPayload.WriteInt32(w, map.HomeStation.PeaceRadius);
        }), ct);
        foreach (var portal in map.Portals)
            await writer.WriteAsync(WorldCommandId.JumpgateCreateCommand, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt32(w, portal.Id);
                WorldPayload.WriteInt32(w, portal.X);
                WorldPayload.WriteInt32(w, portal.Y);
                WorldPayload.WriteInt32(w, portal.DestinationMapId);
                w.Write(portal.Working);
            }), ct);
        var dx = (Int128)pilotX - map.HomeStation.X;
        var dy = (Int128)pilotY - map.HomeStation.Y;
        var radius = (Int128)map.HomeStation.PeaceRadius;
        var safe = dx * dx + dy * dy <= radius * radius;
        await writer.WriteAsync(WorldCommandId.SafeZoneCommand, new byte[] { (byte)(safe ? 1 : 0) }, ct);
        await writer.WriteAsync(WorldCommandId.HeroInitCommand, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, pilot.UserId);
            WorldPayload.WriteString(w, pilot.UserName);
            WorldPayload.WriteString(w, pilot.ShipId);
            WorldPayload.WriteInt32(w, pilot.FactionId);
            WorldPayload.WriteInt32(w, pilot.MapId);
            WorldPayload.WriteInt32(w, pilotX);
            WorldPayload.WriteInt32(w, pilotY);
            WorldPayload.WriteInt32(w, stats.Speed);
            WorldPayload.WriteInt64(w, currentHitPoints ?? stats.MaxHitPoints);
            WorldPayload.WriteInt64(w, stats.MaxHitPoints);
            WorldPayload.WriteInt64(w, shield);
            WorldPayload.WriteInt64(w, stats.MaxShield);
            WorldPayload.WriteInt32(w, 0); // current cargo load
            WorldPayload.WriteInt32(w, stats.CargoCapacity);
            WorldPayload.WriteInt32(w, stats.LaserDamage);
            WorldPayload.WriteInt64(w, pilot.Experience);
            WorldPayload.WriteInt64(w, pilot.Honor);
            WorldPayload.WriteInt32(w, pilot.Level);
            WorldPayload.WriteInt64(w, pilot.Credits);
            WorldPayload.WriteInt64(w, pilot.Uridium);
            w.Write((byte)pilot.HangarSlot);
            WorldPayload.WriteInt32(w, stats.Equipment.Count);
            foreach (var item in stats.Equipment)
            {
                WorldPayload.WriteString(w, item.ItemId);
                WorldPayload.WriteInt32(w, item.Quantity);
            }
        }), ct);
        if (repairBotActive)
            await writer.WriteAsync(WorldCommandId.RepairBotStateCommand, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt64(w, pilot.UserId);
                w.Write(true);
            }), ct);
    }
}
