using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Wire format recovered from the Unity client (GameAssembly.dll, darkorbit-protocol):
// the client's first VersionRequest frame uses a 4-byte big-endian length; every later frame,
// in both directions, uses a 3-byte big-endian length (DarkOrbit.TcpChannel.Send / PacketParser).
// Every module body starts with its 2-byte libcom module id; integer fields are
// bit-rotated per field and 64-bit fields travel as big-endian doubles.
public static class UnityWorldModuleId
{
    public const ushort LoginRequest = 7;
    public const ushort ShipInitializationCommand = 49;
    public const ushort VersionRequest = 666;
    public const ushort VersionCommand = 667;
}

public sealed class UnityWorldFrameWriter(Stream stream)
{
    public const int MaximumBodyBytes = 0xFFFFFF;

    public async Task WriteAsync(byte[] body, CancellationToken ct = default)
    {
        if (body.Length is < 2 or > MaximumBodyBytes)
            throw new InvalidDataException("Unity world frame has an invalid length.");
        var frame = new byte[3 + body.Length];
        frame[0] = (byte)(body.Length >> 16);
        frame[1] = (byte)(body.Length >> 8);
        frame[2] = (byte)body.Length;
        body.CopyTo(frame, 3);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }
}

public sealed class UnityWorldFrameReader(Stream stream, int maximumBodyBytes = 65536)
{
    // After the initial VersionRequest, TcpChannel.Send frames bodies with a 3-byte big-endian length.
    public async Task<WorldPacket?> ReadAsync(CancellationToken ct = default)
    {
        var header = new byte[3];
        var count = await stream.ReadAsync(header, ct);
        if (count == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(count), ct);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        if (length < 2 || length > maximumBodyBytes)
            throw new InvalidDataException("Invalid Unity world frame length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return new WorldPacket((WorldCommandId)BinaryPrimitives.ReadUInt16BigEndian(body), body.AsMemory(2));
    }
}

public sealed class UnityModuleWriter
{
    private readonly MemoryStream _stream = new();

    public UnityModuleWriter(ushort moduleId) => Short((short)moduleId);

    public UnityModuleWriter Short(short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Int(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    // The client decodes with rotate-right(raw, bits), so the server rotates left.
    public UnityModuleWriter RotatedInt(int value, int bits) =>
        Int((int)BitOperations.RotateLeft((uint)value, bits));

    public UnityModuleWriter Long(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Float(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Bool(bool value)
    {
        _stream.WriteByte(value ? (byte)1 : (byte)0);
        return this;
    }

    public UnityModuleWriter Byte(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public UnityModuleWriter String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > short.MaxValue) throw new InvalidDataException("Unity world string is too long.");
        Short((short)bytes.Length);
        _stream.Write(bytes);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}

public sealed class UnityModuleReader(ReadOnlyMemory<byte> payload)
{
    private int _offset;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _offset + count > payload.Length) throw new EndOfStreamException();
        var span = payload.Span.Slice(_offset, count);
        _offset += count;
        return span;
    }

    public short Short() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public int Int() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public int RotatedInt(int bits) => (int)BitOperations.RotateRight((uint)Int(), bits);

    public short RotatedShort(int bits)
    {
        var raw = (ushort)Short();
        return (short)((raw >> bits) | (raw << (16 - bits)));
    }

    public bool Bool() => Take(1)[0] != 0;
    public string String() => Encoding.UTF8.GetString(Take((ushort)Short()));
}

public sealed record UnityLoginRequest(int UserId, short FactionId, string SessionId, string Version,
    int InstanceId, bool IsMiniClient)
{
    // LoginRequest.write rotates userID left by 3, factionID (16-bit) left by 7 and instanceId left by 12.
    public static UnityLoginRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        return new UnityLoginRequest(reader.RotatedInt(3), reader.RotatedShort(7), reader.String(),
            reader.String(), reader.RotatedInt(12), reader.Bool());
    }
}

public static class UnityWorldModules
{
    public static byte[] Version(string version, bool equal) =>
        new UnityModuleWriter(UnityWorldModuleId.VersionCommand).String(version).Bool(equal).ToArray();

    // Field order and rotations follow ShipInitializationCommand.read.
    public static byte[] ShipInitialization(WorldPilot pilot, WorldShipStats stats, long shield, long hitPoints,
        int x, int y) =>
        new UnityModuleWriter(UnityWorldModuleId.ShipInitializationCommand)
            .RotatedInt((int)pilot.UserId, 14)
            .String(pilot.UserName)
            .String(pilot.ShipId)
            .RotatedInt(stats.Speed, 20)
            .RotatedInt((int)Math.Min(shield, int.MaxValue), 18)
            .RotatedInt((int)Math.Min(stats.MaxShield, int.MaxValue), 19)
            .Long(hitPoints)
            .Long(stats.MaxHitPoints)
            .RotatedInt(0, 22)
            .RotatedInt(stats.CargoCapacity, 14)
            .RotatedInt(0, 11)
            .RotatedInt(0, 15)
            .RotatedInt(x, 14)
            .RotatedInt(y, 4)
            .RotatedInt(pilot.MapId, 21)
            .RotatedInt(pilot.FactionId, 27)
            .RotatedInt(0, 3)
            .RotatedInt(3, 5)
            .Bool(true)
            .Long(pilot.Experience)
            .Long(pilot.Honor)
            .RotatedInt(pilot.Level, 15)
            .Long(pilot.Credits)
            .Long(pilot.Uridium)
            .Float(0)
            .RotatedInt(0, 18)
            .String(string.Empty)
            .RotatedInt(0, 27)
            .Bool(false)
            .Bool(false)
            // refreshMap: the handler only creates the hero and loads the scene when true.
            .Bool(true)
            .RotatedInt(0, 23)
            .RotatedInt(0, 31)
            .Byte(0)
            .ToArray();
}
