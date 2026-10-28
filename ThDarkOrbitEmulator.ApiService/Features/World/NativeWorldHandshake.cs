using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Native module IDs and field order are from the installed 2026-10-28 IL2CPP client.
public sealed class NativeWorldHandshake(IWorldPilotResolver resolver, WorldServerOptions options,
    WorldSessionStore? sessions = null, IGameAuthSessionStore? auth = null)
{
    public const string ClientVersion = "05bcf83b06964a516a8282e10dbf3369";
    public const ushort VersionRequest = 0x029a;
    public const ushort VersionCommand = 0x029b;
    public const ushort LoginRequest = 0x0007;
    public const ushort LoginResponse = 0xa50c;
    public const ushort MapChanged = 0x008a;
    public const ushort ShipInitialization = 0x0031;
    public const ushort JumpgateCreate = 0x0d3d;
    public const ushort MoveCommand = 0x005a;
    public const ushort HeroMoveCommand = 0x001d;
    public const ushort AttackLaserRun = 0x0003;
    public const ushort AttackRocket = 0x0006;
    public const ushort AttackHit = 0x001c;
    public const ushort JumpInitiated = 0x0013;

    public async Task RunAsync(Stream stream, byte[] firstBytes, CancellationToken ct)
    {
        var accepted = false;
        var first = true;
        while (true)
        {
            var body = await ReadFrameAsync(stream, first ? firstBytes : null, ct);
            first = false;
            if (body is null) return;
            var offset = body.Length > 2 && body[0] == 0 && body[1] == 0x02 && body[2] == 0x9a ? 1 : 0;
            if (body.Length - offset < 2) return;
            var module = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset));
            try
            {
                using var payload = new MemoryStream(body, offset + 2, body.Length - offset - 2, false);
                using var reader = new BinaryReader(payload, Encoding.UTF8);
                if (module == VersionRequest)
                {
                    var requestedVersion = WorldPayload.ReadString(reader);
                    if (payload.Position != payload.Length) return;
                    accepted = requestedVersion == ClientVersion;
                    await WriteFrameAsync(stream, VersionCommand, WorldPayload.Encode(w =>
                    {
                        WorldPayload.WriteString(w, ClientVersion);
                        w.Write(accepted);
                    }), ct);
                    continue;
                }
                if (module != LoginRequest || !accepted) return;
                var userId = BitOperations.RotateRight((uint)WorldPayload.ReadInt32(reader), 3);
                var factionId = BitOperations.RotateRight((ushort)BinaryPrimitives.ReverseEndianness(reader.ReadUInt16()), 7);
                var sessionId = WorldPayload.ReadString(reader);
                var version = WorldPayload.ReadString(reader);
                var instanceId = BitOperations.RotateRight((uint)WorldPayload.ReadInt32(reader), 12);
                _ = reader.ReadBoolean();
                if (payload.Position != payload.Length || version != ClientVersion || instanceId > int.MaxValue)
                    return;
                var (pilot, _) = await resolver.ResolveAsync(userId, sessionId, ct);
                var session = auth is null ? null : await auth.TryGetByDosidAsync(sessionId, ct);
                if (pilot is null || pilot.FactionId != factionId ||
                    (auth is not null && (session is null || session.GameUserId != userId ||
                    session.InstanceId <= 0 || session.InstanceId != (int)instanceId)))
                {
                    await WriteFrameAsync(stream, LoginResponse, new byte[] { 0, 1 }, ct);
                    return;
                }
                pilot = sessions is null ? pilot : await sessions.RestoreAsync(pilot, ct);
                await WriteFrameAsync(stream, LoginResponse, new byte[] { 0, 0 }, ct);
                var map = WorldMapInstance.Starter(pilot.MapId);
                await WriteFrameAsync(stream, MapChanged, NativeWorldPackets.MapChanged(map.MapId), ct);
                await WriteFrameAsync(stream, ShipInitialization, NativeWorldPackets.ShipInitialization(pilot), ct);
                foreach (var portal in map.Portals)
                    await WriteFrameAsync(stream, JumpgateCreate,
                        NativeWorldPackets.JumpgateCreate(portal, map.FactionId), ct);
                return;
            }
            catch (Exception error) when (error is EndOfStreamException or InvalidDataException or DecoderFallbackException)
            {
                return;
            }
        }
    }

    private async Task<byte[]?> ReadFrameAsync(Stream stream, byte[]? firstBytes, CancellationToken ct)
    {
        var header = new byte[3];
        if (firstBytes is null)
        {
            var count = await stream.ReadAsync(header, ct);
            if (count == 0) return null;
            await stream.ReadExactlyAsync(header.AsMemory(count), ct);
        }
        else firstBytes.AsSpan(0, 3).CopyTo(header);
        var length = header[0] << 16 | header[1] << 8 | header[2];
        if (length < 2 || length > Math.Min(options.MaximumPacketBytes, 4096))
            throw new InvalidDataException("Invalid native world frame length.");
        var body = new byte[length];
        if (firstBytes is not null) body[0] = firstBytes[3];
        await stream.ReadExactlyAsync(body.AsMemory(firstBytes is null ? 0 : 1), ct);
        return body;
    }

    private static Task WriteFrameAsync(Stream stream, ushort module, byte[] payload, CancellationToken ct)
    {
        var length = payload.Length + 2;
        var frame = new byte[length + 3];
        frame[0] = (byte)(length >> 16);
        frame[1] = (byte)(length >> 8);
        frame[2] = (byte)length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), module);
        payload.CopyTo(frame, 5);
        return stream.WriteAsync(frame, ct).AsTask();
    }
}

public static class NativeWorldPackets
{
    private static void Rotated(BinaryWriter writer, long value, int left) =>
        WorldPayload.WriteInt32(writer, (int)BitOperations.RotateLeft(checked((uint)value), left));

    private static void DoubleNumber(BinaryWriter writer, long value) =>
        WorldPayload.WriteInt64(writer, BitConverter.DoubleToInt64Bits(value));

    public static byte[] MapChanged(int mapId) => WorldPayload.Encode(w => Rotated(w, mapId, 4));

    // ShipInitializationCommand.read at RVA 0x158B640, in reader order.
    public static byte[] ShipInitialization(WorldPilot pilot) => WorldPayload.Encode(w =>
    {
        var stats = pilot.Stats;
        Rotated(w, pilot.UserId, 14);
        WorldPayload.WriteString(w, pilot.UserName);
        WorldPayload.WriteString(w, pilot.ShipId);
        Rotated(w, stats.Speed, 20);
        Rotated(w, stats.MaxShield, 18);
        Rotated(w, stats.MaxShield, 19);
        DoubleNumber(w, stats.MaxHitPoints);
        DoubleNumber(w, stats.MaxHitPoints);
        Rotated(w, stats.CargoCapacity, 22);
        Rotated(w, stats.CargoCapacity, 14);
        Rotated(w, 0, 11); // nano hull
        Rotated(w, 0, 15);
        Rotated(w, pilot.X, 14);
        Rotated(w, pilot.Y, 4);
        Rotated(w, pilot.MapId, 21);
        Rotated(w, pilot.FactionId, 27);
        Rotated(w, 0, 29); // clan ID
        Rotated(w, 0, 5); // expansion stage
        w.Write(false); // premium
        DoubleNumber(w, pilot.Experience);
        DoubleNumber(w, pilot.Honor);
        Rotated(w, pilot.Level, 15);
        DoubleNumber(w, pilot.Credits);
        DoubleNumber(w, pilot.Uridium);
        WorldPayload.WriteInt32(w, BitConverter.SingleToInt32Bits(0)); // jackpot
        Rotated(w, 0, 18); // rank
        WorldPayload.WriteString(w, string.Empty); // clan tag
        Rotated(w, 0, 27); // gates completed
        w.Write(false); // system font
        w.Write(false); // cloaked
        w.Write(true); // refresh map
        Rotated(w, 0, 23); // captain energy
        Rotated(w, 0, 31);
        w.Write((byte)0); // VisualModifierCommand list count
    });

    public static byte[] Move(int userId, int x, int y, int durationMs) => WorldPayload.Encode(w =>
    {
        Rotated(w, userId, 16);
        Rotated(w, x, 13);
        Rotated(w, y, 16);
        Rotated(w, durationMs, 18);
    });

    public static byte[] HeroMove(int x, int y) => WorldPayload.Encode(w =>
    {
        Rotated(w, x, 9);
        Rotated(w, y, 25);
    });

    public static byte[] JumpgateCreate(WorldPortal portal, int factionId) => WorldPayload.Encode(w =>
    {
        Rotated(w, portal.Id, 10);
        Rotated(w, factionId, 30);
        Rotated(w, 0, 17); // design ID
        Rotated(w, portal.X, 11);
        Rotated(w, portal.Y, 9);
        w.Write(true);
        w.Write(true);
    });

    public static byte[] Laser(int attackerId, int targetId, int laserColor, int visualType,
        bool diminished = false, bool skilled = false) => WorldPayload.Encode(w =>
    {
        Rotated(w, attackerId, 2);
        Rotated(w, targetId, 31);
        Rotated(w, laserColor, 16);
        w.Write(diminished);
        w.Write(skilled);
        Rotated(w, visualType, 23);
    });

    public static byte[] Rocket(int attackerId, int targetId, int rocketType, int smokeId,
        bool hit, bool heatSeeking = false) => WorldPayload.Encode(w =>
    {
        Rotated(w, attackerId, 16);
        Rotated(w, targetId, 11);
        Rotated(w, rocketType, 3);
        Rotated(w, smokeId, 3);
        w.Write(hit);
        w.Write(heatSeeking);
    });

    public static byte[] Hit(short attackType, int attackerId, int victimId, long victimHitPoints,
        int victimShield, int victimNanoHull, int damage, bool skilled, bool strike,
        short cannonType) => WorldPayload.Encode(w =>
    {
        w.Write(BinaryPrimitives.ReverseEndianness((ushort)0x001b)); // AttackTypeModule
        w.Write(BinaryPrimitives.ReverseEndianness(attackType));
        Rotated(w, attackerId, 25);
        Rotated(w, victimId, 3);
        DoubleNumber(w, victimHitPoints);
        Rotated(w, victimShield, 19);
        Rotated(w, victimNanoHull, 8);
        Rotated(w, damage, 8);
        w.Write(skilled);
        w.Write(strike);
        w.Write(BinaryPrimitives.ReverseEndianness(cannonType));
    });

    public static byte[] JumpInitiated(int mapId, int gateId) => WorldPayload.Encode(w =>
    {
        Rotated(w, mapId, 25);
        Rotated(w, gateId, 26);
    });
}
