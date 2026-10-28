using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// The entry modules are emulator extensions. The installed client confirms only the
// version/login module IDs; a captured server burst is needed for native gameplay.
public sealed class NativeWorldHandshake(IWorldPilotResolver resolver, WorldServerOptions options,
    WorldSessionStore? sessions = null)
{
    public const string ClientVersion = "05bcf83b06964a516a8282e10dbf3369";
    public const ushort VersionRequest = 0x029a;
    public const ushort VersionCommand = 0x029b;
    public const ushort LoginRequest = 0x0007;
    public const ushort LoginResponse = 0xa50c;
    public const ushort MapInitialization = 0xf100;
    public const ushort HeroInitialization = 0xf101;

    public async Task RunAsync(Stream stream, byte[] firstBytes, CancellationToken ct)
    {
        var accepted = false;
        var first = true;
        while (true)
        {
            var body = await ReadFrameAsync(stream, first ? firstBytes : null, ct);
            first = false;
            if (body is null) return;
            var offset = body.Length > 2 && body[0] == 0 && body[1] == 0x9a && body[2] == 0x02 ? 1 : 0;
            if (body.Length - offset < 2) return;
            var module = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
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
                if (pilot is null || pilot.FactionId != factionId)
                {
                    await WriteFrameAsync(stream, LoginResponse, new byte[] { 0, 1 }, ct);
                    return;
                }
                pilot = sessions is null ? pilot : await sessions.RestoreAsync(pilot, ct);
                await WriteFrameAsync(stream, LoginResponse, new byte[] { 0, 0 }, ct);
                var map = WorldMapInstance.Starter(pilot.MapId);
                await WriteFrameAsync(stream, MapInitialization, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt32(w, map.MapId);
                    WorldPayload.WriteInt32(w, map.Width);
                    WorldPayload.WriteInt32(w, map.Height);
                    WorldPayload.WriteInt32(w, map.FactionId);
                }), ct);
                await WriteFrameAsync(stream, HeroInitialization, WorldPayload.Encode(w =>
                {
                    WorldPayload.WriteInt64(w, pilot.UserId);
                    WorldPayload.WriteString(w, pilot.UserName);
                    WorldPayload.WriteString(w, pilot.ShipId);
                    WorldPayload.WriteInt32(w, pilot.MapId);
                    WorldPayload.WriteInt32(w, pilot.X);
                    WorldPayload.WriteInt32(w, pilot.Y);
                }), ct);
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
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), module);
        payload.CopyTo(frame, 5);
        return stream.WriteAsync(frame, ct).AsTask();
    }
}
