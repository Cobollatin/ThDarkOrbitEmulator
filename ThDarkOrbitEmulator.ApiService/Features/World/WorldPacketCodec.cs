using System.Buffers.Binary;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public enum WorldCommandId : ushort
{
    VersionRequest = 1,
    VersionCommand = 2,
    LoginRequest = 3,
    LoginResponse = 4,
    LoginErrorCommand = 5,
    MapInitializationCommand = 6,
    SpaceStationCommand = 7,
    JumpgateCreateCommand = 8,
    SafeZoneCommand = 9,
    HeroInitCommand = 10,
    MoveRequest = 11,
    MoveCommand = 12,
    RadiationWarningCommand = 13,
    ConfigurationRequest = 14,
    ShipAttributesCommand = 15,
    ConfigurationCooldownCommand = 16,
    CompanionStateCommand = 17,
    JumpRequest = 18,
    MapChangeCommand = 19,
    CollectBoxRequest = 20,
    CollectBoxRemoveCommand = 21,
    CollectRewardCommand = 22,
    CollectBoxCreateCommand = 23,
    ShipCreateCommand = 24,
    SelectShipRequest = 25,
    TargetLockCommand = 26,
    LaserFireRequest = 27,
    LaserHitCommand = 28,
    ShipDestroyCommand = 29,
    NpcRewardCommand = 30,
    AnnouncementCommand = 31,
    AdminCommandRequest = 32,
    AdminCommandResponse = 33,
    PilotCreateCommand = 34,
    PilotRemoveCommand = 35,
    LaserFireResultCommand = 36,
    NpcHealthCommand = 37,
    RocketFireRequest = 38,
    RocketHitCommand = 39,
    NpcFireCommand = 40,
    LaserStopRequest = 41,
    RadiationDamageCommand = 42,
    PortalOperationCommand = 43,
    CloakStateCommand = 44,
    ShipHealCommand = 45,
    RepairBotStateCommand = 46
}

public enum WorldLoginError : byte
{
    Malformed = 1,
    UnsupportedVersion = 2,
    UnknownSession = 3,
    ExpiredSession = 4,
    CompanyRequired = 5,
    UserMismatch = 6,
    UnknownCommand = 7,
    Unauthorized = 8
}

public sealed class WorldPacketReader(Stream stream, int maximumPacketBytes = 65536)
{
    public async Task<WorldPacket?> ReadAsync(CancellationToken ct = default, bool preLogin = false)
    {
        var header = new byte[4];
        var count = await stream.ReadAsync(header, ct);
        if (count == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(count), ct);
        // The installed Unity client uses a three-byte length and libcom module IDs.
        // Do not interpret its version frame as a large local-protocol allocation.
        if (preLogin && header[0] == 0 && header[1] == 0 && header[2] >= 2
            && (header[3] == 0x02 || header[3] == 0))
            throw new NativeWorldProtocolException(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > maximumPacketBytes || preLogin && length > 4096)
            throw new InvalidDataException("Invalid world packet length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return new WorldPacket((WorldCommandId)BinaryPrimitives.ReadUInt16BigEndian(body), body.AsMemory(2));
    }
}

public sealed class NativeWorldProtocolException(byte[] firstBytes) : IOException("Native Unity world frame detected.")
{
    public byte[] FirstBytes { get; } = firstBytes;
}

public readonly record struct WorldPacket(WorldCommandId Command, ReadOnlyMemory<byte> Payload);

public sealed class WorldPacketWriter(Stream stream, int maximumPacketBytes = 65536)
{
    public async Task WriteAsync(WorldCommandId command, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > maximumPacketBytes - 2)
            throw new InvalidDataException("World packet exceeds configured limit.");
        var frame = new byte[6 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 2);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)command);
        payload.CopyTo(frame.AsMemory(6));
        await stream.WriteAsync(frame, ct);
    }
}

public static class WorldPayload
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        write(writer);
        return stream.ToArray();
    }

    public static T Decode<T>(ReadOnlyMemory<byte> payload, Func<BinaryReader, T> read)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var value = read(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world payload bytes.");
        return value;
    }

    public static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > 1024) throw new InvalidDataException("World string exceeds limit.");
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)bytes.Length));
        writer.Write(bytes);
    }

    public static void WriteAdminResult(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > 60000) throw new InvalidDataException("Admin result exceeds packet limit.");
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)bytes.Length));
        writer.Write(bytes);
    }

    public static string ReadAdminResult(BinaryReader reader)
    {
        var length = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        if (length > 60000) throw new InvalidDataException("Admin result exceeds packet limit.");
        return StrictUtf8.GetString(reader.ReadBytesExactly(length));
    }

    public static string ReadString(BinaryReader reader)
    {
        var length = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        if (length > 1024) throw new InvalidDataException("World string exceeds limit.");
        return StrictUtf8.GetString(reader.ReadBytesExactly(length));
    }

    public static void WriteInt64(BinaryWriter writer, long value) =>
        writer.Write(BinaryPrimitives.ReverseEndianness(value));

    public static long ReadInt64(BinaryReader reader) =>
        BinaryPrimitives.ReverseEndianness(reader.ReadInt64());

    public static void WriteInt32(BinaryWriter writer, int value) =>
        writer.Write(BinaryPrimitives.ReverseEndianness(value));

    public static int ReadInt32(BinaryReader reader) =>
        BinaryPrimitives.ReverseEndianness(reader.ReadInt32());

    private static byte[] ReadBytesExactly(this BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
