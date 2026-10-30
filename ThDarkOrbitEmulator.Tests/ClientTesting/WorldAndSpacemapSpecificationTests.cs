using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldAndSpacemapSpecificationTests
{
    [Test]
    public async Task PacketFrame_IsBigEndian_AndRoundTripsSplitReads()
    {
        using var stream = new MemoryStream();
        var payload = WorldPayload.Encode(w => { WorldPayload.WriteInt32(w, -1234); WorldPayload.WriteString(w, "é"); });
        await new WorldPacketWriter(stream).WriteAsync(WorldCommandId.MoveRequest, payload);
        var frame = stream.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(frame), Is.EqualTo(payload.Length + 2));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)), Is.EqualTo((ushort)WorldCommandId.MoveRequest));
        });
        stream.Position = 0;
        var packet = await new WorldPacketReader(stream).ReadAsync();
        Assert.That(packet?.Command, Is.EqualTo(WorldCommandId.MoveRequest));
        Assert.That(WorldPayload.Decode(packet!.Value.Payload, r =>
            (WorldPayload.ReadInt32(r), WorldPayload.ReadString(r))), Is.EqualTo((-1234, "é")));
    }

    [Test]
    public async Task HttpWorldMutations_RejectNonJsonBeforeReadingSessionOrWorldState()
    {
        var context = new DefaultHttpContext();
        await WorldHttpBridge.MoveAsync(context, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.JumpAsync(context, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.RespawnAsync(context, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.CollectAsync(context, null!, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.TargetAsync(context, null!, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.FireAsync(context, null!, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
        context.Response.StatusCode = 200;
        await WorldHttpBridge.PulseAsync(context, null!, null!, null!, null!, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(415));
    }

    [Test]
    public async Task HttpWorldMutations_RejectJsonPrefixLookalikesBeforeAuthentication()
    {
        foreach (var contentType in new[] { "application/jsonp", "application/json+xml", "application/json garbage" })
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = contentType;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            await WorldHttpBridge.MoveAsync(context, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            Assert.That(context.Request.Body.Position, Is.Zero, contentType);

            context.Response.StatusCode = 200;
            await WorldHttpBridge.JumpAsync(context, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            context.Response.StatusCode = 200;
            await WorldHttpBridge.RespawnAsync(context, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            context.Response.StatusCode = 200;
            await WorldHttpBridge.CollectAsync(context, null!, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            context.Response.StatusCode = 200;
            await WorldHttpBridge.TargetAsync(context, null!, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            context.Response.StatusCode = 200;
            await WorldHttpBridge.FireAsync(context, null!, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            context.Response.StatusCode = 200;
            await WorldHttpBridge.PulseAsync(context, null!, null!, null!, null!, default);
            Assert.That(context.Response.StatusCode, Is.EqualTo(415), contentType);
            Assert.That(context.Request.Body.Position, Is.Zero, contentType);
        }
    }

    [Test]
    public void PacketCodec_RejectsInvalidLengths_ExcessPayload_AndTrailingBytes()
    {
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketReader(new MemoryStream([0, 0, 0, 1, 0])).ReadAsync());
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketWriter(new MemoryStream(), 4).WriteAsync(WorldCommandId.MoveRequest, new byte[3]));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Decode<int>(new byte[] { 0, 0, 0, 1, 9 }, WorldPayload.ReadInt32));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Encode(w => WorldPayload.WriteString(w, new string('x', 1025))));
        Assert.Throws<DecoderFallbackException>(() => WorldPayload.Decode<string>(new byte[] { 0, 1, 0xff }, WorldPayload.ReadString));
    }

    [Test]
    public async Task PacketFrame_AcceptsEmptyPayloadAndExactConfiguredMaximum()
    {
        using var stream = new MemoryStream();
        var writer = new WorldPacketWriter(stream, 6);
        await writer.WriteAsync(WorldCommandId.VersionRequest, Array.Empty<byte>());
        await writer.WriteAsync(WorldCommandId.LoginRequest, new byte[4]);
        stream.Position = 0;
        var reader = new WorldPacketReader(stream, 6);
        Assert.That((await reader.ReadAsync())!.Value.Payload.Length, Is.Zero);
        Assert.That((await reader.ReadAsync())!.Value.Payload.Length, Is.EqualTo(4));
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task PacketFrame_DefaultMaximumIncludesCommandAndLeavesFollowingFrameIntact()
    {
        var payload = Enumerable.Range(0, 65_534).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream();
        var writer = new WorldPacketWriter(stream);
        await writer.WriteAsync(WorldCommandId.MoveCommand, payload);
        await writer.WriteAsync(WorldCommandId.VersionCommand, new byte[] { 0x7f });
        var bytes = stream.ToArray();
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(bytes), Is.EqualTo(65_536));
        Assert.That(bytes.Length, Is.EqualTo(4 + 65_536 + 7));

        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var first = (await reader.ReadAsync())!.Value;
        var second = (await reader.ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(first.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(first.Payload.ToArray(), Is.EqualTo(payload));
            Assert.That(second.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(second.Payload.ToArray(), Is.EqualTo(new byte[] { 0x7f }));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public void PacketFrame_DefaultMaximumRejectsNextByteBeforeWritingOrReadingBody()
    {
        using var output = new MemoryStream();
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketWriter(output).WriteAsync(WorldCommandId.MoveCommand, new byte[65_535]));
        Assert.That(output.Length, Is.Zero);

        using var input = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 65_537);
        input.Write(header);
        input.WriteByte(0x7f);
        input.Position = 0;
        Assert.ThrowsAsync<InvalidDataException>(async () => await new WorldPacketReader(input).ReadAsync());
        Assert.That(input.Position, Is.EqualTo(4));
    }

    [TestCase(64)]
    [TestCase(1_048_576)]
    public async Task PacketFrame_ConfiguredBoundsRoundTripExactBodyAndFollowingFrame(int maximum)
    {
        var payload = Enumerable.Range(0, maximum - 2).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream();
        var writer = new WorldPacketWriter(stream, maximum);
        await writer.WriteAsync(WorldCommandId.MoveCommand, payload);
        await writer.WriteAsync(WorldCommandId.VersionCommand, new byte[] { 0x5a });
        var bytes = stream.ToArray();
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(bytes), Is.EqualTo(maximum));
        Assert.That(bytes.Length, Is.EqualTo(maximum + 11));

        stream.Position = 0;
        var reader = new WorldPacketReader(stream, maximum);
        var first = (await reader.ReadAsync())!.Value;
        var second = (await reader.ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(first.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(first.Payload.ToArray(), Is.EqualTo(payload));
            Assert.That(second.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(second.Payload.ToArray(), Is.EqualTo(new byte[] { 0x5a }));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [TestCase(64)]
    [TestCase(1_048_576)]
    public void PacketFrame_ConfiguredBoundsRejectNextByteWithoutConsumingBody(int maximum)
    {
        using var output = new MemoryStream();
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketWriter(output, maximum).WriteAsync(
                WorldCommandId.MoveCommand, new byte[maximum - 1]));
        Assert.That(output.Length, Is.Zero);

        using var input = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, maximum + 1);
        input.Write(header);
        input.WriteByte(0x5a);
        input.Position = 0;
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketReader(input, maximum).ReadAsync());
        Assert.That(input.Position, Is.EqualTo(4));
    }

    [Test]
    public async Task PacketFrame_ReassemblesOneByteReadsWithoutConsumingNextFrame()
    {
        using var encoded = new MemoryStream();
        var writer = new WorldPacketWriter(encoded);
        await writer.WriteAsync(WorldCommandId.VersionRequest, new byte[] { 0, 1, (byte)'a' });
        await writer.WriteAsync(WorldCommandId.MoveRequest, new byte[] { 0x80, 0, 0, 1 });

        using var fragmented = new OneByteReadStream(encoded.ToArray());
        var reader = new WorldPacketReader(fragmented);
        var first = (await reader.ReadAsync())!.Value;
        var second = (await reader.ReadAsync())!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(first.Command, Is.EqualTo(WorldCommandId.VersionRequest));
            Assert.That(first.Payload.ToArray(), Is.EqualTo(new byte[] { 0, 1, (byte)'a' }));
            Assert.That(second.Command, Is.EqualTo(WorldCommandId.MoveRequest));
            Assert.That(second.Payload.ToArray(), Is.EqualTo(new byte[] { 0x80, 0, 0, 1 }));
            Assert.That(fragmented.Position, Is.EqualTo(fragmented.Length));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [TestCase(1)]
    [TestCase(7)]
    public void PacketFrame_RejectsOutOfRangeLengthBeforeReadingBody(int declaredLength)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, declaredLength);
        stream.Write(header);
        stream.WriteByte(0x7f);
        stream.Position = 0;

        Assert.ThrowsAsync<InvalidDataException>(async () => await new WorldPacketReader(stream, 6).ReadAsync());
        Assert.That(stream.Position, Is.EqualTo(4), "Invalid lengths must fail before consuming a body.");
    }

    [Test]
    public void PacketFrame_OversizedWriteLeavesStreamUntouched()
    {
        using var stream = new MemoryStream();
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WorldPacketWriter(stream, 6).WriteAsync(WorldCommandId.VersionRequest, new byte[5]));
        Assert.That(stream.Length, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void PacketFrame_RejectsPartialHeaderRatherThanTreatingItAsCleanEof(int headerBytes)
    {
        using var stream = new MemoryStream(new byte[headerBytes]);
        if (headerBytes == 0)
            Assert.That(new WorldPacketReader(stream).ReadAsync().GetAwaiter().GetResult(), Is.Null);
        else
            Assert.ThrowsAsync<EndOfStreamException>(async () => await new WorldPacketReader(stream).ReadAsync());
    }

    [Test]
    public void PacketFrame_RejectsTruncatedBodyAtExactLimit()
    {
        using var stream = new MemoryStream([0, 0, 0, 6, 0, 11, 1, 2, 3]);
        Assert.ThrowsAsync<EndOfStreamException>(async () => await new WorldPacketReader(stream, 6).ReadAsync());
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(6)]
    public async Task PacketFrame_CancellationDuringHeaderOrBodyPropagates(int bytesBeforeBlock)
    {
        using var stream = new BlockingReadStream([0, 0, 0, 4, 0, 11, 0x12, 0x34], bytesBeforeBlock);
        using var cancelled = new CancellationTokenSource();
        var read = new WorldPacketReader(stream).ReadAsync(cancelled.Token);
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();

        Assert.That(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(stream.Position, Is.EqualTo(bytesBeforeBlock));
    }

    [Test]
    public void PacketFrame_PreCancelledWriteLeavesStreamUntouched()
    {
        using var stream = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.That(async () => await new WorldPacketWriter(stream).WriteAsync(WorldCommandId.MoveRequest,
            new byte[] { 0x12, 0x34 }, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(stream.Length, Is.Zero);
    }

    [Test]
    public async Task PacketFrame_PreservesUnknownCommandNumberForSessionHandler()
    {
        using var stream = new MemoryStream();
        var unknown = (WorldCommandId)ushort.MaxValue;
        await new WorldPacketWriter(stream).WriteAsync(unknown, new byte[] { 0x7f });
        stream.Position = 0;
        var packet = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        Assert.That((ushort)packet.Command, Is.EqualTo(ushort.MaxValue));
        Assert.That(packet.Payload.ToArray(), Is.EqualTo(new byte[] { 0x7f }));
    }

    [Test]
    public async Task PacketFrame_WriteSnapshotsCallerPayloadBeforeAwaitingStream()
    {
        using var stream = new BlockingFirstWriteStream();
        var source = new byte[] { 0x11, 0x22 };
        var write = new WorldPacketWriter(stream).WriteAsync(WorldCommandId.MoveRequest, source);
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source[0] = 0x99;
        source[1] = 0x88;
        stream.ReleaseFirstWrite();
        await write.WaitAsync(TimeSpan.FromSeconds(5));

        stream.Position = 0;
        var packet = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.MoveRequest));
            Assert.That(packet.Payload.ToArray(), Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(stream.WriteCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PacketFrame_ReadPayloadRemainsStableAfterNextFrame()
    {
        using var stream = new MemoryStream();
        var writer = new WorldPacketWriter(stream);
        await writer.WriteAsync(WorldCommandId.VersionCommand, new byte[] { 0x11, 0x22 });
        await writer.WriteAsync(WorldCommandId.MoveCommand, new byte[] { 0x33 });
        stream.Position = 0;

        var reader = new WorldPacketReader(stream);
        var first = (await reader.ReadAsync())!.Value;
        var second = (await reader.ReadAsync())!.Value;
        var end = await reader.ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(first.Payload.ToArray(), Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(second.Payload.ToArray(), Is.EqualTo(new byte[] { 0x33 }));
            Assert.That(end, Is.Null);
        });
    }

    [Test]
    public async Task ConnectionSend_SerializesFramesWhileFirstWriteIsBlocked()
    {
        using var stream = new BlockingFirstWriteStream();
        var connection = new WorldConnection(Pilot(), new WorldPacketWriter(stream));
        var first = connection.SendAsync(WorldCommandId.VersionCommand, new byte[] { 0x11 }, default);
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = connection.SendAsync(WorldCommandId.MoveCommand, new byte[] { 0x22, 0x33 }, default);

        Assert.That(stream.WriteCalls, Is.EqualTo(1), "The second frame must wait for the first write.");
        stream.ReleaseFirstWrite();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var firstFrame = (await reader.ReadAsync())!.Value;
        var secondFrame = (await reader.ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(firstFrame.Command, Is.EqualTo(WorldCommandId.VersionCommand));
            Assert.That(firstFrame.Payload.ToArray(), Is.EqualTo(new byte[] { 0x11 }));
            Assert.That(secondFrame.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(secondFrame.Payload.ToArray(), Is.EqualTo(new byte[] { 0x22, 0x33 }));
            Assert.That(stream.WriteCalls, Is.EqualTo(2));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConnectionSend_ReleasesWriteGateAfterWriterFailure()
    {
        using var stream = new FailFirstWriteStream();
        var connection = new WorldConnection(Pilot(), new WorldPacketWriter(stream));
        Assert.ThrowsAsync<IOException>(async () =>
            await connection.SendAsync(WorldCommandId.VersionCommand, new byte[] { 0x11 }, default));

        await connection.SendAsync(WorldCommandId.MoveCommand, new byte[] { 0x22 }, default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var packet = (await reader.ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(packet.Payload.ToArray(), Is.EqualTo(new byte[] { 0x22 }));
            Assert.That(stream.WriteCalls, Is.EqualTo(2));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConnectionSend_CancelledWaiterNeverWritesAndDoesNotBlockNextFrame()
    {
        using var stream = new BlockingFirstWriteStream();
        var connection = new WorldConnection(Pilot(), new WorldPacketWriter(stream));
        var first = connection.SendAsync(WorldCommandId.VersionCommand, new byte[] { 0x11 }, default);
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var waiting = connection.SendAsync(WorldCommandId.MoveCommand, new byte[] { 0x22 }, cancelled.Token);
        cancelled.Cancel();

        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(stream.WriteCalls, Is.EqualTo(1));
        stream.ReleaseFirstWrite();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.SendAsync(WorldCommandId.LoginResponse, new byte[] { 0x33 }, default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        Assert.That((await reader.ReadAsync())!.Value.Command, Is.EqualTo(WorldCommandId.VersionCommand));
        var next = (await reader.ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(next.Command, Is.EqualTo(WorldCommandId.LoginResponse));
            Assert.That(next.Payload.ToArray(), Is.EqualTo(new byte[] { 0x33 }));
            Assert.That(stream.WriteCalls, Is.EqualTo(2));
        });
        Assert.That(await reader.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ConnectionSend_CancelledActiveWriteReleasesGateForLaterFrame()
    {
        using var stream = new BlockingFirstWriteStream();
        var connection = new WorldConnection(Pilot(), new WorldPacketWriter(stream));
        using var cancelled = new CancellationTokenSource();
        var active = connection.SendAsync(WorldCommandId.VersionCommand, new byte[] { 0x11 }, cancelled.Token);
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();

        Assert.That(async () => await active, Throws.InstanceOf<OperationCanceledException>());
        await connection.SendAsync(WorldCommandId.MoveCommand, new byte[] { 0x22 }, default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        stream.Position = 0;
        var packet = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.MoveCommand));
            Assert.That(packet.Payload.ToArray(), Is.EqualTo(new byte[] { 0x22 }));
            Assert.That(stream.WriteCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public void PayloadIntegers_UseBigEndianForSignedExtremes()
    {
        var bytes = WorldPayload.Encode(writer =>
        {
            WorldPayload.WriteInt32(writer, int.MinValue);
            WorldPayload.WriteInt64(writer, long.MaxValue);
        });
        Assert.That(bytes, Is.EqualTo(new byte[]
        {
            0x80, 0, 0, 0, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff
        }));
        Assert.That(WorldPayload.Decode(bytes, reader =>
            (WorldPayload.ReadInt32(reader), WorldPayload.ReadInt64(reader))),
            Is.EqualTo((int.MinValue, long.MaxValue)));
    }

    [Test]
    public void PayloadIntegers_RoundTripBothSignedBoundsAndZeroWithoutPadding()
    {
        var values32 = new[] { int.MinValue, -1, 0, 1, int.MaxValue };
        var values64 = new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue };
        var bytes = WorldPayload.Encode(writer =>
        {
            foreach (var value in values32) WorldPayload.WriteInt32(writer, value);
            foreach (var value in values64) WorldPayload.WriteInt64(writer, value);
        });

        Assert.That(bytes.Length, Is.EqualTo(values32.Length * 4 + values64.Length * 8));
        Assert.That(bytes.AsSpan(4, 4).ToArray(), Is.EqualTo(new byte[] { 0xff, 0xff, 0xff, 0xff }));
        Assert.That(bytes.AsSpan(20, 8).ToArray(), Is.EqualTo(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }));
        var decoded = WorldPayload.Decode(bytes, reader =>
        {
            var decoded32 = Enumerable.Range(0, values32.Length).Select(_ => WorldPayload.ReadInt32(reader)).ToArray();
            var decoded64 = Enumerable.Range(0, values64.Length).Select(_ => WorldPayload.ReadInt64(reader)).ToArray();
            return (decoded32, decoded64);
        });
        Assert.Multiple(() =>
        {
            Assert.That(decoded.decoded32, Is.EqualTo(values32));
            Assert.That(decoded.decoded64, Is.EqualTo(values64));
        });
    }

    [Test]
    public void PayloadIntegers_RejectEveryTruncatedWidth()
    {
        for (var length = 0; length < 4; length++)
            Assert.Throws<EndOfStreamException>(() =>
                WorldPayload.Decode(new byte[length], WorldPayload.ReadInt32), $"i32 with {length} bytes");
        for (var length = 0; length < 8; length++)
            Assert.Throws<EndOfStreamException>(() =>
                WorldPayload.Decode(new byte[length], WorldPayload.ReadInt64), $"i64 with {length} bytes");
    }

    [Test]
    public void PayloadDecode_RequiresCompleteConsumptionIncludingEmptyPayload()
    {
        Assert.That(WorldPayload.Decode(Array.Empty<byte>(), _ => 7), Is.EqualTo(7));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Decode(new byte[] { 0x7f }, _ => 7));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Decode(
            new byte[] { 0, 0, 0, 1, 0x7f }, WorldPayload.ReadInt32));
    }

    [Test]
    public void PayloadStrings_ApplyLimitToUtf8BytesAndRejectTruncation()
    {
        var atLimit = new string('é', 512);
        var bytes = WorldPayload.Encode(writer => WorldPayload.WriteString(writer, atLimit));
        Assert.That(bytes.Length, Is.EqualTo(1026));
        Assert.That(bytes[..2], Is.EqualTo(new byte[] { 4, 0 }));
        Assert.That(WorldPayload.Decode(bytes, WorldPayload.ReadString), Is.EqualTo(atLimit));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Encode(writer =>
            WorldPayload.WriteString(writer, atLimit + "é")));
        Assert.Throws<EndOfStreamException>(() => WorldPayload.Decode(new byte[] { 0, 2, 0x61 }, WorldPayload.ReadString));
        Assert.Throws<EndOfStreamException>(() => WorldPayload.Decode(new byte[] { 0 }, WorldPayload.ReadString));
    }

    [Test]
    public void PayloadStrings_RejectUnpairedSurrogatesBeforeWritingAnyBytes()
    {
        foreach (var malformed in new[] { "\uD800", "\uDC00", "ok\uD800tail", "ok\uDC00tail" })
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            Assert.Throws<EncoderFallbackException>(() => WorldPayload.WriteString(writer, malformed), malformed);
            Assert.That(stream.Length, Is.Zero, malformed);
            Assert.Throws<EncoderFallbackException>(() => WorldPayload.WriteAdminResult(writer, malformed), malformed);
            Assert.That(stream.Length, Is.Zero, malformed);
        }
    }

    [Test]
    public void PayloadStrings_AcceptEmptyAndCompleteFourByteScalarsAtTheByteLimits()
    {
        const string scalar = "\U0001F680";
        var ordinary = scalar + new string('a', 1020);
        var admin = scalar + new string('a', 59_996);
        var empty = WorldPayload.Encode(w => WorldPayload.WriteString(w, ""));
        var ordinaryBytes = WorldPayload.Encode(w => WorldPayload.WriteString(w, ordinary));
        var adminBytes = WorldPayload.Encode(w => WorldPayload.WriteAdminResult(w, admin));

        Assert.Multiple(() =>
        {
            Assert.That(empty, Is.EqualTo(new byte[] { 0, 0 }));
            Assert.That(ordinaryBytes[..2], Is.EqualTo(new byte[] { 4, 0 }));
            Assert.That(adminBytes[..2], Is.EqualTo(new byte[] { 0xea, 0x60 }));
        });
        Assert.That(WorldPayload.Decode(empty, WorldPayload.ReadString), Is.Empty);
        Assert.That(WorldPayload.Decode(ordinaryBytes, WorldPayload.ReadString), Is.EqualTo(ordinary));
        Assert.That(WorldPayload.Decode(adminBytes, WorldPayload.ReadAdminResult), Is.EqualTo(admin));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Encode(w => WorldPayload.WriteString(w, ordinary + "a")));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Encode(w => WorldPayload.WriteAdminResult(w, admin + "a")));
    }

    [Test]
    public void PayloadStrings_RejectNonCanonicalUtf8ForOrdinaryAndAdminFields()
    {
        byte[][] malformed =
        [
            [0xc0, 0xaf],       // overlong slash
            [0xed, 0xa0, 0x80], // UTF-8 encoding of a surrogate
            [0xf4, 0x90, 0x80, 0x80], // above the Unicode maximum
            [0xe2, 0x82]        // truncated multi-byte scalar
        ];
        foreach (var bytes in malformed)
        {
            var payload = new byte[2 + bytes.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)bytes.Length);
            bytes.CopyTo(payload, 2);
            Assert.Throws<DecoderFallbackException>(() => WorldPayload.Decode(payload, WorldPayload.ReadString));
            Assert.Throws<DecoderFallbackException>(() => WorldPayload.Decode(payload, WorldPayload.ReadAdminResult));
        }
    }

    [Test]
    public void AdminResult_HasSeparateSixtyThousandByteLimit()
    {
        var atLimit = new string('x', 60_000);
        var bytes = WorldPayload.Encode(writer => WorldPayload.WriteAdminResult(writer, atLimit));
        Assert.That(bytes[..2], Is.EqualTo(new byte[] { 0xea, 0x60 }));
        Assert.That(WorldPayload.Decode(bytes, WorldPayload.ReadAdminResult), Is.EqualTo(atLimit));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Encode(writer =>
            WorldPayload.WriteAdminResult(writer, atLimit + "x")));
        Assert.Throws<InvalidDataException>(() => WorldPayload.Decode(new byte[] { 0xea, 0x61 }, WorldPayload.ReadAdminResult));
        Assert.Throws<EndOfStreamException>(() => WorldPayload.Decode(new byte[] { 0, 2, 0x61 }, WorldPayload.ReadAdminResult));
        Assert.Throws<DecoderFallbackException>(() => WorldPayload.Decode(new byte[] { 0, 1, 0xff }, WorldPayload.ReadAdminResult));
    }

    [TestCase(1, 1, 2, 3)]
    [TestCase(2, 1, 1, -1)]
    [TestCase(5, 2, 6, 7)]
    [TestCase(6, 2, 5, -1)]
    [TestCase(9, 3, 10, 11)]
    [TestCase(10, 3, 9, -1)]
    [TestCase(13, 4, 14, 15)]
    public void MapGraph_HasSyntheticFactionGeometry(int mapId, int faction, int firstDestination,
        int secondDestination)
    {
        var map = WorldMapInstance.Starter(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(map.FactionId, Is.EqualTo(faction));
            Assert.That((map.Width, map.Height), Is.EqualTo((21000, 13100)));
            Assert.That(map.Portals[0].DestinationMapId, Is.EqualTo(firstDestination));
            Assert.That(map.Portals[0].Id, Is.EqualTo(mapId * 100 + 1));
            Assert.That(map.HomeStation.PeaceRadius, Is.EqualTo(1000));
            Assert.That(map.Portals.Count, Is.EqualTo(secondDestination < 0 ? 1 : 7));
        });
        if (secondDestination > 0) Assert.That(map.Portals[1].DestinationMapId, Is.EqualTo(secondDestination));
    }

    [Test]
    public void MapGraph_RejectsOutsideSupportedIds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapInstance.Starter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapInstance.Starter(17));
    }

    [Test]
    public async Task TcpEntryBurst_AdvertisesMapStationPortalsSafeZoneAndHeroInOrder()
    {
        using var stream = new MemoryStream();
        var pilot = Pilot() with
        {
            UserName = "pilot", FactionId = 1, X = 2000, Y = 1300,
            Stats = new WorldShipStats(370, 128000, 5000, 1500, 300, [])
        };
        await WorldEntryPackets.SendAsync(new WorldPacketWriter(stream), pilot, default);
        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var packets = new List<WorldPacket>();
        while (await reader.ReadAsync() is { } packet) packets.Add(packet);
        Assert.That(packets.Select(p => p.Command), Is.EqualTo(new[]
        {
            WorldCommandId.MapInitializationCommand, WorldCommandId.SpaceStationCommand,
            WorldCommandId.JumpgateCreateCommand, WorldCommandId.JumpgateCreateCommand,
            WorldCommandId.JumpgateCreateCommand, WorldCommandId.JumpgateCreateCommand,
            WorldCommandId.JumpgateCreateCommand, WorldCommandId.JumpgateCreateCommand,
            WorldCommandId.JumpgateCreateCommand,
            WorldCommandId.SafeZoneCommand, WorldCommandId.HeroInitCommand
        }));
        Assert.That(WorldPayload.Decode(packets[0].Payload, r =>
            (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r))), Is.EqualTo((1, 21000, 13100, 1)));
        Assert.That(WorldPayload.Decode(packets[1].Payload, r =>
            (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r))), Is.EqualTo((100, 2000, 1300, 1000)));
        Assert.That(packets[9].Payload.ToArray(), Is.EqualTo(new byte[] { 1 }));
        using var heroStream = new MemoryStream(packets[10].Payload.ToArray());
        using var heroReader = new BinaryReader(heroStream);
        Assert.That((WorldPayload.ReadInt64(heroReader), WorldPayload.ReadString(heroReader),
            WorldPayload.ReadString(heroReader)), Is.EqualTo((7L, "pilot", "ship_pusat-plus")));
    }

    [Test]
    public async Task PortalJump_UsesInclusiveRadius_AndPersistsArrival()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var outside = await sessions.EnterAsync(Pilot() with
        {
            X = 20001, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        Assert.That(await sessions.JumpAsync(outside, 101, null, default), Is.Null);
        var inside = await sessions.TeleportAsync(outside.UserId, 1, 20000, 1300, false, default);
        Assert.That(inside, Is.Not.Null);
        var jump = await sessions.JumpAsync(inside!, 101, null, default);
        Assert.That(jump, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That((jump!.Pilot.MapId, jump.Pilot.X, jump.Pilot.Y), Is.EqualTo((2, 2500, 1300)));
            Assert.That(jump.PortalId, Is.EqualTo(101));
            Assert.That(sessions.MoveId(jump.Pilot), Is.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public async Task PortalJump_FromPreviousMap_DoesNotUseDestinationPortal()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var previous = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var stale = await previous.EnterAsync(Pilot() with
        {
            X = 20000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var current = await destination.TeleportAsync(stale.UserId, 2, 1500, 1300, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.JumpAsync(stale, 201, null, default));
        Assert.That(await destination.SavedMapAsync(stale.UserId, default), Is.EqualTo(2));
        Assert.That((await destination.JumpAsync(current!, 201, null, default))?.Pilot.MapId,
            Is.EqualTo(1));
    }

    [Test]
    public async Task Movement_FromPreviousMap_DoesNotChangeDestinationTravelOrConfiguration()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var previous = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var stale = await previous.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var current = await destination.TeleportAsync(stale.UserId, 2, 3000, 1300, false, default);
        Assert.That(current, Is.Not.Null);
        var moveId = destination.MoveId(current!);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.MoveAsync(stale, 4000, 1300, null, default));
        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.SwitchAsync(stale, 2, default));

        var saved = (await destination.ConnectedAsync(2, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That((saved.X, saved.Y), Is.EqualTo((3000, 1300)));
            Assert.That(destination.MoveId(saved), Is.EqualTo(moveId));
            Assert.That(destination.Configuration(saved.UserId), Is.EqualTo(1));
        });
        Assert.That((await destination.MoveAsync(current!, 4000, 1300, null, default)).X,
            Is.EqualTo(4000));
    }

    [Test]
    public async Task Repair_FromPreviousMap_DoesNotSpendDestinationRepairChoice()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var previous = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var stale = await previous.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        await previous.ApplyNpcDamageAsync(stale, 1, 1200, default);
        var current = await destination.TeleportAsync(stale.UserId, 2, 3000, 1300, false, default);
        Assert.That(current, Is.Not.Null);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.RespawnAsync(stale, WorldRespawnLocation.Base, default));
        Assert.That(await destination.SavedMapAsync(stale.UserId, default), Is.EqualTo(2));
        var repaired = await destination.RespawnAsync(current!, WorldRespawnLocation.Base, default);
        Assert.That(repaired?.MapId, Is.EqualTo(1));
        Assert.That(await destination.RespawnAsync(repaired!, WorldRespawnLocation.Base, default), Is.Null);
    }

    [Test]
    public async Task StatePoll_FromPreviousMap_DoesNotRetimeOrRepairDestination()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var previous = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var stale = await previous.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var current = await destination.TeleportAsync(stale.UserId, 2, 3000, 1300, false, default);
        Assert.That(current, Is.Not.Null);
        var moveId = destination.MoveId(current!);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.RetimeForLiveSpeedAsync(stale, default));
        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await previous.RepairAtBaseFencedAsync(stale, DateTimeOffset.UtcNow, default, null));

        var saved = (await destination.ConnectedAsync(2, default)).Single();
        Assert.That((saved.X, saved.Y, destination.MoveId(saved)),
            Is.EqualTo((3000, 1300, moveId)));
    }

    [Test]
    public async Task PortalJump_RecentNpcCombatBlocksAcrossReplicas_UntilWindowExpires()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var replica = new WorldSessionStore(cache, mutex, clock: clock);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(),
            Options.Create(new WorldNpcOptions { Templates =
                [new("Streuner", "npc_streuner", 1, 1000, 0, 0, 0, 0, 0, 0)] }), clock);
        var npc = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, default)).Status, Is.EqualTo("hit"));
        var nearPortal = await sessions.TeleportAsync(pilot.UserId, 1, 20000, 1300, false, default);
        Assert.That(nearPortal, Is.Not.Null);
        Assert.That(await replica.JumpAsync(nearPortal!, 101, null, default), Is.Null);
        Assert.That(await replica.SavedMapAsync(pilot.UserId, default), Is.EqualTo(1));

        clock.Advance(TimeSpan.FromSeconds(10));
        var jump = await replica.JumpAsync(nearPortal!, 101, null, default);
        Assert.That(jump?.Pilot.MapId, Is.EqualTo(2));
    }

    [Test]
    public async Task PortalJump_CombatLockReadsConfiguredMapsAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["World:CombatRestrictedMaps:0"] = "2" }).Build();
        var first = new WorldSessionStore(cache, mutex, clock: clock,
            configuration: configuration);
        var replica = new WorldSessionStore(cache, mutex, clock: clock,
            configuration: configuration);
        var pilot = await first.EnterAsync(Pilot() with
        {
            X = 19500, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        await first.ApplyNpcDamageAsync(pilot, 10001, 40, default);

        var firstJump = await replica.JumpAsync(pilot, 101, null, default);
        Assert.That(firstJump?.Pilot.MapId, Is.EqualTo(2));
        var nearReturn = await first.TeleportAsync(pilot.UserId, 2, 1500, 1300, false, default);
        Assert.That(nearReturn, Is.Not.Null);
        Assert.That(await replica.JumpAsync(nearReturn!, 201, null, default), Is.Null);
        Assert.That(await replica.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That((await first.JumpAsync(nearReturn!, 201, null, default))?.Pilot.MapId,
            Is.EqualTo(1));
    }

    [Test]
    public async Task PortalJump_RecentNpcHitBlocksJumpWithoutChangingSavedMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new WorldSessionStore(cache);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            X = 20000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        await sessions.ApplyNpcDamageAsync(pilot, 10001, 40, default);

        Assert.That(await sessions.JumpAsync(pilot, 101, null, default), Is.Null);
        Assert.That(await sessions.SavedMapAsync(pilot.UserId, default), Is.EqualTo(1));
    }

    [Test]
    public void Movement_ClampsBoundary_ComputesRoundedDuration_AndUsesCurrentTravelOrigin()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pilot = Pilot() with { X = 100, Y = 200 };
        var prior = new WorldTravel(1, 100, 200, 300, 200, 100, 9_000);
        var move = WorldMovement.Begin(pilot, prior, 21100, -10, 300, now);
        Assert.Multiple(() =>
        {
            Assert.That((move.X, move.Y), Is.EqualTo((21000, 0)));
            Assert.That(move.RadiationWarning, Is.True);
            Assert.That(move.TimeToTargetMs, Is.EqualTo((int)Math.Round(Math.Sqrt(20800d * 20800 + 200d * 200) * 1000 / 300)));
        });
        var travel = WorldMovement.Travel(pilot, prior, move, 300, now);
        Assert.That((travel.StartX, travel.StartY), Is.EqualTo((200d, 200d)));
        Assert.That(travel.MoveId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void Movement_DurationRoundsToNearestMillisecond()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pilot = Pilot() with { X = 100, Y = 100 };

        Assert.Multiple(() =>
        {
            Assert.That(WorldMovement.Begin(pilot, null, 101, 100, 300, now).TimeToTargetMs,
                Is.EqualTo(3));
            Assert.That(WorldMovement.Begin(pilot, null, 102, 100, 300, now).TimeToTargetMs,
                Is.EqualTo(7));
            Assert.That(WorldMovement.DurationMilliseconds(1, 400), Is.EqualTo(2));
        });
    }

    [Test]
    public void Movement_ExtremeSavedCoordinates_UseWidenedDistanceAndSaturateDuration()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pilot = Pilot() with { X = int.MinValue, Y = int.MaxValue };
        var fast = WorldMovement.Begin(pilot, null, 21_000, 0, int.MaxValue, now);
        var slow = WorldMovement.Begin(pilot, null, 21_000, 0, 1, now);
        var stopped = WorldMovement.Begin(pilot, null, 21_000, 0, 0, now);
        var distance = Math.Sqrt(Math.Pow((double)21_000 - int.MinValue, 2) +
            Math.Pow((double)0 - int.MaxValue, 2));

        Assert.Multiple(() =>
        {
            Assert.That(fast.TimeToTargetMs,
                Is.EqualTo((int)Math.Round(distance * 1000d / int.MaxValue)));
            Assert.That(fast.TimeToTargetMs, Is.GreaterThan(0));
            Assert.That(slow.TimeToTargetMs, Is.EqualTo(int.MaxValue));
            Assert.That(stopped.TimeToTargetMs, Is.Zero);
            Assert.That((fast.X, fast.Y, fast.RadiationWarning), Is.EqualTo((21_000, 0, false)));
        });
    }

    [Test]
    public void Movement_ExtremeSameMapTravelOrigin_UsesWidenedDistance()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pilot = Pilot() with { X = 100, Y = 100 };
        var previous = new WorldTravel(pilot.MapId, int.MaxValue, int.MinValue,
            int.MaxValue, int.MinValue, 0, now.ToUnixTimeMilliseconds());
        var move = WorldMovement.Begin(pilot, previous, 0, 13_100, int.MaxValue, now);
        var distance = Math.Sqrt(Math.Pow((double)int.MaxValue, 2) +
            Math.Pow((double)13_100 - int.MinValue, 2));

        Assert.That(move.TimeToTargetMs,
            Is.EqualTo((int)Math.Round(distance * 1000d / int.MaxValue)));
        Assert.That(move.RadiationWarning, Is.False);
    }

    [Test]
    public void TravelInterpolation_ClampsTime_RoundsCoordinates_AndCompletesZeroDistance()
    {
        var origin = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var travel = new WorldTravel(1, 0, 0, 3, 4, 1, 5_000);
        Assert.That(travel.PositionAt(origin.AddSeconds(-2)), Is.EqualTo((0, 0)));
        Assert.That(travel.PositionAt(origin.AddSeconds(2)), Is.EqualTo((1, 2)));
        Assert.That(travel.PositionAt(origin.AddSeconds(6)), Is.EqualTo((3, 4)));
        Assert.That(new WorldTravel(1, 8, 9, 8, 9, 0, 5_000).PositionAt(origin), Is.EqualTo((8, 9)));
    }

    [Test]
    public void TravelInterpolation_UsesAnnouncedRoundedDuration()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pilot = Pilot() with { X = 0, Y = 100 };
        var move = WorldMovement.Begin(pilot, null, 1, 100, 167, start);
        var travel = WorldMovement.Travel(pilot, null, move, 167, start);

        Assert.Multiple(() =>
        {
            Assert.That(move.TimeToTargetMs, Is.EqualTo(6));
            Assert.That(travel.PositionAt(start.AddMilliseconds(3)), Is.EqualTo((0, 100)));
            Assert.That(travel.PositionAt(start.AddMilliseconds(4)), Is.EqualTo((1, 100)));
            Assert.That(travel.PositionAt(start.AddMilliseconds(6)), Is.EqualTo((1, 100)));
        });
    }

    [Test]
    public void TravelInterpolation_UsesMidpointToEvenForBothCoordinates()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        // Use horizontal travel so the exact half-unit samples are representable in milliseconds.
        var horizontal = new WorldTravel(1, 0, 0, 3, 0, 1, 10_000);
        var vertical = new WorldTravel(1, 0, 0, 0, 3, 1, 10_000);
        var descending = new WorldTravel(1, 3, 3, 0, 3, 1, 10_000);

        Assert.Multiple(() =>
        {
            Assert.That(horizontal.PositionAt(start.AddMilliseconds(500)), Is.EqualTo((0, 0)));
            Assert.That(horizontal.PositionAt(start.AddMilliseconds(1500)), Is.EqualTo((2, 0)));
            Assert.That(horizontal.PositionAt(start.AddMilliseconds(2500)), Is.EqualTo((2, 0)));
            Assert.That(vertical.PositionAt(start.AddMilliseconds(500)), Is.EqualTo((0, 0)));
            Assert.That(vertical.PositionAt(start.AddMilliseconds(1500)), Is.EqualTo((0, 2)));
            Assert.That(descending.PositionAt(start.AddMilliseconds(500)), Is.EqualTo((2, 3)));
            Assert.That(descending.PositionAt(start.AddMilliseconds(1500)), Is.EqualTo((2, 3)));
        });
    }

    [TestCase(0.5, -0.5, 0, 0)]
    [TestCase(1.5, -1.5, 2, -2)]
    [TestCase(2.5, -2.5, 2, -2)]
    public void TravelInterpolation_FractionalRecordedOriginUsesMidpointToEven(
        double startX, double startY, int expectedX, int expectedY)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var travel = new WorldTravel(1, startX, startY, 100, 100, 0, 10_000);

        Assert.Multiple(() =>
        {
            Assert.That(travel.PositionAt(start.AddMilliseconds(-1)), Is.EqualTo((expectedX, expectedY)));
            Assert.That(travel.PositionAt(start), Is.EqualTo((expectedX, expectedY)));
            Assert.That(travel.PositionAt(start.AddDays(1)), Is.EqualTo((expectedX, expectedY)));
        });
    }

    [Test]
    public void Movement_RetargetsFromRoundedFractionalPositionForBothDurationAndTravel()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(20_000);
        var pilot = Pilot() with { X = 500, Y = 500 };
        var previous = new WorldTravel(pilot.MapId, 1.5, 2.5, 100, 100, 0, 10_000);
        var move = WorldMovement.Begin(pilot, previous, 5, 6, 1, now);
        var next = WorldMovement.Travel(pilot, previous, move, 1, now);

        Assert.Multiple(() =>
        {
            Assert.That(previous.PositionAt(now), Is.EqualTo((2, 2)));
            Assert.That(move.TimeToTargetMs, Is.EqualTo(5_000));
            Assert.That((next.StartX, next.StartY), Is.EqualTo((2d, 2d)));
            Assert.That(next.PositionAt(now), Is.EqualTo((2, 2)));
            Assert.That(previous.StartX, Is.EqualTo(1.5));
            Assert.That(previous.StartY, Is.EqualTo(2.5));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void TravelInterpolation_NonpositiveSpeedKeepsTheRecordedOrigin(int speed)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var travel = new WorldTravel(1, 25, 35, 125, 135, speed, 10_000);

        Assert.Multiple(() =>
        {
            Assert.That(travel.PositionAt(start.AddSeconds(-1)), Is.EqualTo((25, 35)));
            Assert.That(travel.PositionAt(start), Is.EqualTo((25, 35)));
            Assert.That(travel.PositionAt(start.AddDays(1)), Is.EqualTo((25, 35)));
            Assert.That(WorldMovement.Begin(Pilot() with { X = 25, Y = 35 }, travel,
                125, 135, speed, start).TimeToTargetMs, Is.Zero);
        });
    }

    [Test]
    public void Movement_RetargetsFromStationarySameMapTravelButIgnoresForeignMapTravel()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(15_000);
        var pilot = Pilot() with { X = 500, Y = 600 };
        var stationary = new WorldTravel(pilot.MapId, 25, 35, 125, 135, 0, 10_000);
        var foreign = stationary with { MapId = pilot.MapId + 1, Speed = 100 };

        var sameMapMove = WorldMovement.Begin(pilot, stationary, 125, 35, 100, now);
        var sameMapTravel = WorldMovement.Travel(pilot, stationary, sameMapMove, 100, now);
        var foreignMapMove = WorldMovement.Begin(pilot, foreign, 500, 700, 100, now);
        var foreignMapTravel = WorldMovement.Travel(pilot, foreign, foreignMapMove, 100, now);

        Assert.Multiple(() =>
        {
            Assert.That(sameMapMove.TimeToTargetMs, Is.EqualTo(1_000));
            Assert.That((sameMapTravel.StartX, sameMapTravel.StartY), Is.EqualTo((25d, 35d)));
            Assert.That(foreignMapMove.TimeToTargetMs, Is.EqualTo(1_000));
            Assert.That((foreignMapTravel.StartX, foreignMapTravel.StartY), Is.EqualTo((500d, 600d)));
            Assert.That(stationary.PositionAt(now), Is.EqualTo((25, 35)));
        });
    }

    [Test]
    public void Movement_RetargetsFromInterpolatedPositionWithoutMutatingPreviousTravel()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(4_000);
        var pilot = Pilot() with { X = 0, Y = 100 };
        var previous = new WorldTravel(pilot.MapId, 0, 100, 100, 100, 10, 0, Guid.NewGuid());
        var move = WorldMovement.Begin(pilot, previous, 70, 100, 10, now);
        var next = WorldMovement.Travel(pilot, previous, move, 10, now);

        Assert.Multiple(() =>
        {
            Assert.That(move.TimeToTargetMs, Is.EqualTo(3_000));
            Assert.That((next.StartX, next.StartY), Is.EqualTo((40d, 100d)));
            Assert.That(next.PositionAt(now.AddMilliseconds(1500)), Is.EqualTo((55, 100)));
            Assert.That(next.MoveId, Is.Not.EqualTo(previous.MoveId));
            Assert.That(previous.PositionAt(now.AddMilliseconds(1500)), Is.EqualTo((55, 100)));
        });
    }

    [Test]
    public void Movement_FutureTravelStartsAtItsRecordedOrigin()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(4_000);
        var pilot = Pilot() with { X = 900, Y = 900 };
        var future = new WorldTravel(pilot.MapId, 20, 30, 120, 30, 10, 5_000);
        var move = WorldMovement.Begin(pilot, future, 50, 30, 10, now);
        var next = WorldMovement.Travel(pilot, future, move, 10, now);

        Assert.Multiple(() =>
        {
            Assert.That(move.TimeToTargetMs, Is.EqualTo(3_000));
            Assert.That((next.StartX, next.StartY), Is.EqualTo((20d, 30d)));
            Assert.That(next.StartedAtUnixMilliseconds, Is.EqualTo(4_000));
        });
    }

    [Test]
    public void ShipStats_IsolateConfigurations_RejectUnownedAssignments_AndUseShipBases()
    {
        var equipment = new LocalEquipmentState(
            [new("laser_lf3", "", "", 2), new("generator_g3n_3310", "", "", 1),
             new("shield_sg3n_b02", "", "", 1)],
            [new(1, 1, "laser_lf3", 2), new(1, 1, "generator_g3n_3310", 1),
             new(1, 2, "shield_sg3n_b02", 1), new(1, 1, "laser_lf2", 1)]);
        var skills = new LocalPilotSkillState([]);
        var first = WorldShipStatCalculator.Calculate(1, equipment, skills);
        var second = WorldShipStatCalculator.Calculate(1, equipment, skills, configuration: 2);
        var phoenix = WorldShipStatCalculator.Calculate(2, equipment, skills);
        Assert.Multiple(() =>
        {
            Assert.That((first.Speed, first.MaxHitPoints, first.MaxShield, first.CargoCapacity, first.LaserDamage),
                Is.EqualTo((410, 128000L, 0L, 1500, 300)));
            Assert.That((second.Speed, second.MaxShield, second.LaserDamage), Is.EqualTo((370, 5000L, 0)));
            Assert.That((phoenix.Speed, phoenix.MaxHitPoints, phoenix.CargoCapacity), Is.EqualTo((320, 4000L, 100)));
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldShipStatCalculator.Calculate(1, equipment, skills, configuration: 3));
    }

    [Test]
    public void ShipStats_UseAssignedShipForHullCargoAndSpeed_WhileEquipmentFollowsSlot()
    {
        var equipment = new LocalEquipmentState(
            [new("laser_lf3", "", "", 1)], [new(1, 1, "laser_lf3", 1)]);
        var stats = WorldShipStatCalculator.Calculate(1, equipment, new LocalPilotSkillState([]),
            shipId: "ship_phoenix");

        Assert.That((stats.Speed, stats.MaxHitPoints, stats.CargoCapacity, stats.LaserDamage),
            Is.EqualTo((320, 4000L, 100, 150)));
    }

    [Test]
    public void MapGraph_StaysInsideFaction_AndLeavesFourthMapWithoutInboundPortal()
    {
        var maps = Enumerable.Range(1, 16).Select(WorldMapInstance.Starter).ToArray();
        var destinations = maps.SelectMany(m => m.Portals).Select(p => p.DestinationMapId).ToHashSet();
        Assert.Multiple(() =>
        {
            foreach (var map in maps)
            {
                Assert.That(map.HomeStation.Id, Is.EqualTo(map.MapId * 100));
                Assert.That((map.Width, map.Height, map.HomeStation.PeaceRadius), Is.EqualTo((21000, 13100, 1000)));
                Assert.That(map.Portals.Select(p => p.Id),
                    Is.EqualTo(Enumerable.Range(1, map.Portals.Count).Select(i => map.MapId * 100 + i)));
                Assert.That(map.Portals.All(p => WorldMapInstance.Starter(p.DestinationMapId).FactionId == map.FactionId));
                Assert.That(map.Portals.All(p => p.JumpRadius == 500));
                Assert.That(destinations.Contains(map.MapId), Is.EqualTo((map.MapId - 1) % 4 != 3), $"map {map.MapId}");
            }
            Assert.That(WorldMapInstance.Starter(4).Portals.Single().DestinationMapId, Is.EqualTo(3));
            Assert.That(WorldMapInstance.Starter(16).FactionId, Is.EqualTo(4));
        });
    }

    [Test]
    public void Movement_InclusiveBounds_NegativeTargets_AndZeroSpeed()
    {
        var pilot = new WorldPilot(7, "mmo", 1, 1, "ship", 0, 0) { X = 100, Y = 100 };
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        var edge = WorldMovement.Begin(pilot, null, 21000, 13100, 370, now);
        var negative = WorldMovement.Begin(pilot, null, -1, 50, 370, now);
        var tooHigh = WorldMovement.Begin(pilot, null, 50, 13101, 370, now);
        var still = WorldMovement.Begin(pilot, null, 5000, 5000, 0, now);
        var sameSpot = WorldMovement.Begin(pilot, null, 100, 100, 370, now);
        Assert.Multiple(() =>
        {
            Assert.That((edge.X, edge.Y, edge.RadiationWarning), Is.EqualTo((21000, 13100, false)));
            Assert.That((negative.X, negative.Y, negative.RadiationWarning), Is.EqualTo((0, 50, true)));
            Assert.That((tooHigh.X, tooHigh.Y, tooHigh.RadiationWarning), Is.EqualTo((50, 13100, true)));
            Assert.That((still.TimeToTargetMs, still.RadiationWarning), Is.EqualTo((0, false)));
            Assert.That(sameSpot.TimeToTargetMs, Is.Zero);
        });
    }

    [Test]
    public void Movement_IgnoresTravelFromAnotherMap_AndIssuesFreshMoveIds()
    {
        var pilot = new WorldPilot(7, "mmo", 2, 1, "ship", 0, 0) { X = 1000, Y = 1000 };
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var oldMap = new WorldTravel(1, 0, 0, 20000, 0, 1000, 0, Guid.NewGuid());
        var move = WorldMovement.Begin(pilot, oldMap, 1000, 2000, 100, now);
        var first = WorldMovement.Travel(pilot, oldMap, move, 100, now);
        var second = WorldMovement.Travel(pilot, oldMap, move, 100, now);
        Assert.Multiple(() =>
        {
            Assert.That(move.TimeToTargetMs, Is.EqualTo(10000));
            Assert.That((first.MapId, first.StartX, first.StartY, first.StartedAtUnixMilliseconds),
                Is.EqualTo((2, 1000d, 1000d, 5000L)));
            Assert.That(first.MoveId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(second.MoveId, Is.Not.EqualTo(first.MoveId));
            Assert.That(first.PositionAt(now.AddSeconds(5)), Is.EqualTo((1000, 1500)));
            Assert.That(first.PositionAt(now.AddSeconds(-5)), Is.EqualTo((1000, 1000)));
        });
    }

    [Test]
    public async Task Radiation_MoveCommandsDoNotAccelerateOneSecondTicks_AndPilotDiesAtZeroHull()
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = new WorldPilot(901, "mmo", 1, 1, "ship", 0, 0)
            { X = 0, Y = 1300,
              Stats = new WorldShipStats(300, 100, 0, 0, 0, []) };
        await store.EnterAsync(pilot, default);
        await store.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(100));
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(99));
        for (var repeat = 0; repeat < 3; repeat++)
            await store.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(99));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(98));

        for (var second = 2; second <= 99; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await store.TickRadiationAsync(pilot.MapId, default);
        }
        Assert.That(store.CurrentHitPoints(pilot), Is.Zero);
        Assert.ThrowsAsync<WorldPilotDestroyedException>(async () =>
            await store.MoveAsync(pilot, 100, 1300, null, default, clock.GetUtcNow()));
    }

    [Test]
    public async Task RadiationDeath_RecordsDeathAndCanRepairAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet();
        var clock = new AdvancingClock();
        var accountId = Guid.NewGuid();
        var owner = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var pilot = await owner.EnterAsync(Pilot() with
        {
            AccountId = accountId, UserId = 908, X = 0, Y = 1300,
            Stats = new WorldShipStats(300, 2, 0, 0, 0, [])
        }, default);

        await owner.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.Zero);
        Assert.That(await wallet.FindPendingDeathAsync(accountId, default), Is.Not.Null);

        var replica = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var dead = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(dead), Is.Zero);
        var repaired = await replica.RespawnAtBaseAsync(dead, default);
        Assert.That(repaired, Is.Not.Null);
        Assert.That(replica.CurrentHitPoints(repaired!), Is.GreaterThan(0));
        Assert.That(await wallet.FindPendingDeathAsync(accountId, default), Is.Null);
    }

    [Test]
    public async Task Radiation_ContinuesWithoutMoveCommands_AcrossReplicaHandoff_AndStopsOnSafeMove()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var pilot = Pilot() with
        {
            UserId = 907, X = 0, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        };
        var owner = new WorldSessionStore(cache, clock: clock);
        pilot = await owner.EnterAsync(pilot, default);
        await owner.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(990));

        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(990));
        clock.Advance(TimeSpan.FromSeconds(1));
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(980));

        var replica = new WorldSessionStore(cache, clock: clock);
        pilot = await replica.EnterAsync(pilot, default);
        await replica.TickRadiationAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(980));
        clock.Advance(TimeSpan.FromSeconds(10));
        await replica.TickRadiationAsync(pilot.MapId, default);
        await replica.RecoverHullAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(970));

        await replica.MoveAsync(pilot, 100, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await replica.TickRadiationAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(970));
    }

    [Test]
    public async Task Radiation_SafeDestinationKeepsHazardUntilShipLeavesBoundary()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var owner = new WorldSessionStore(cache, clock: clock);
        var pilot = await owner.EnterAsync(Pilot() with
        {
            UserId = 909, X = 0, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);

        await owner.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        await owner.TickRadiationAsync(pilot.MapId, default);
        var returnMove = await owner.MoveAsync(pilot, 100, 1300, null, default, clock.GetUtcNow());
        Assert.That(returnMove.RadiationWarning, Is.True);
        Assert.That(owner.RadiationWarning(pilot.UserId), Is.True);

        var replica = new WorldSessionStore(cache, clock: clock);
        pilot = await replica.EnterAsync(pilot, default);
        Assert.That(replica.RadiationWarning(pilot.UserId), Is.True);
        clock.Advance(TimeSpan.FromSeconds(1));
        await replica.TickRadiationAsync(pilot.MapId, default);
        Assert.That(replica.RadiationWarning(pilot.UserId), Is.False);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(990));
    }

    [Test]
    public async Task Radiation_ExactBoundaryArrivalStartsHazardAcrossReplicaHandoff()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var owner = new WorldSessionStore(cache, clock: clock);
        var pilot = await owner.EnterAsync(Pilot() with
        {
            UserId = 910, X = 300, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);

        var move = await owner.MoveAsync(pilot, 0, 1300, null, default, clock.GetUtcNow());
        Assert.That(move.RadiationWarning, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(1000));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await owner.TickRadiationAsync(pilot.MapId, default);
        Assert.That(owner.RadiationWarning(pilot.UserId), Is.True);
        Assert.That(owner.CurrentHitPoints(pilot), Is.EqualTo(990));

        var replica = new WorldSessionStore(cache, clock: clock);
        pilot = await replica.EnterAsync(pilot, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await replica.TickRadiationAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(980));
    }

    [Test]
    public async Task Radiation_OutboundTravelDoesNotDamagePilotBeforeBoundaryArrival()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 911, X = 300, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);

        var move = await store.MoveAsync(pilot, -1, 1300, null, default, clock.GetUtcNow());
        Assert.That(move.RadiationWarning, Is.False);
        Assert.That(store.RadiationWarning(pilot.UserId), Is.False);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(1000));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.RadiationWarning(pilot.UserId), Is.False);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(1000));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await store.TickRadiationAsync(pilot.MapId, default);
        Assert.That(store.RadiationWarning(pilot.UserId), Is.True);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));
    }

    [Test]
    public async Task Radiation_TickSendsWarningTransitionsToConnectedPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 911, X = 300, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        using var stream = new MemoryStream();
        store.Attach(new WorldConnection(pilot, new WorldPacketWriter(stream)));

        await store.MoveAsync(pilot, 0, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.TickRadiationAsync(pilot.MapId, default);
        await store.TickRadiationAsync(pilot.MapId, default);
        await store.MoveAsync(pilot, 300, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.TickRadiationAsync(pilot.MapId, default);
        await store.TickRadiationAsync(pilot.MapId, default);

        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var damage = await reader.ReadAsync();
        var started = await reader.ReadAsync();
        var cleared = await reader.ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(damage?.Command, Is.EqualTo(WorldCommandId.RadiationDamageCommand));
            Assert.That(WorldPayload.Decode(damage!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r),
                    WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((pilot.UserId, 10L, 990L, 0L)));
            Assert.That(started?.Command, Is.EqualTo(WorldCommandId.RadiationWarningCommand));
            Assert.That(started?.Payload.ToArray(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(cleared?.Command, Is.EqualTo(WorldCommandId.RadiationWarningCommand));
            Assert.That(cleared?.Payload.ToArray(), Is.EqualTo(new byte[] { 0 }));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        });
    }

    [Test]
    public async Task Radiation_LethalTickReportsOnlyAppliedHullDamage()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(cache, clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 912, X = 300, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 995, default);
        using var stream = new MemoryStream();
        store.Attach(new WorldConnection(pilot, new WorldPacketWriter(stream)));

        await store.MoveAsync(pilot, 0, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.TickRadiationAsync(pilot.MapId, default);

        stream.Position = 0;
        var damage = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(damage.Command, Is.EqualTo(WorldCommandId.RadiationDamageCommand));
            Assert.That(WorldPayload.Decode(damage.Payload, r =>
                (WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r),
                    WorldPayload.ReadInt64(r), WorldPayload.ReadInt64(r))),
                Is.EqualTo((pilot.UserId, 5L, 0L, 0L)));
            Assert.That(store.CurrentHitPoints(pilot), Is.Zero);
            Assert.That(store.RadiationWarning(pilot.UserId), Is.False);
        });
        Assert.That(await new WorldPacketReader(stream).ReadAsync(), Is.Null);
        var replica = new WorldSessionStore(cache, clock: clock);
        var restored = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(restored), Is.Zero);
        Assert.That(replica.RadiationWarning(restored.UserId), Is.False);
    }

    [Test]
    public async Task RadiationDestruction_ClearsCloakForObserversAndRepair()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var owner = new WorldSessionStore(cache, clock: clock);
        var pilot = await owner.EnterAsync(Pilot() with
        {
            UserId = 913, X = 300, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await owner.ApplyNpcDamageAsync(pilot, 1, 995, default);
        pilot = await owner.SetCloakAsync(pilot, true, default);
        using var wire = new MemoryStream();
        owner.Attach(new WorldConnection(pilot with { UserId = 914 }, new WorldPacketWriter(wire)));

        await owner.MoveAsync(pilot, 0, 1300, null, default, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await owner.TickRadiationAsync(pilot.MapId, default);

        wire.Position = 0;
        var reader = new WorldPacketReader(wire);
        var cloak = await reader.ReadAsync();
        Assert.That(cloak?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
        Assert.That(WorldPayload.Decode(cloak!.Value.Payload, r =>
            (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, false)));
        Assert.That((await owner.ConnectedAsync(pilot.MapId, default)).Single().IsInvisible, Is.False);
        var replica = new WorldSessionStore(cache, clock: clock);
        var restored = await replica.EnterAsync(pilot, default);
        Assert.That(restored.IsInvisible, Is.False);
        Assert.That(replica.CurrentHitPoints(restored), Is.Zero);
        var repaired = await replica.RespawnAtBaseAsync(restored, default);
        Assert.That(repaired?.IsInvisible, Is.False);
    }

    [TestCase(3000, true)]
    [TestCase(3001, false)]
    public async Task StationRepair_UsesInclusiveRadiusAndRequiresStoppedTravel(int x, bool inside)
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(902, "mmo", 1, 1, "ship", 0, 0)
            { X = x, Y = 1300, Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        await store.EnterAsync(pilot, default);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(200_000);
        await store.MoveAsync(pilot, -1, 1300, null, default, now);
        await store.ApplyNpcDamageAsync(pilot, 1, 10, default, shieldAbsorption: 0);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));

        await store.MoveAsync(pilot, x, 1300, null, default, now);
        Assert.That(store.CurrentPosition(pilot, now), Is.EqualTo((x, 1300)));
        Assert.That(store.Destination(pilot), Is.EqualTo((x, 1300)));
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(inside ? 1000 : 990));
    }

    [Test]
    public async Task StationRepair_WaitsUntilTravelReachesItsDestination()
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(903, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(300, 1000, 0, 0, 0, []) };
        await store.EnterAsync(pilot, default);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(300_000);
        await store.MoveAsync(pilot, -1, 1300, null, default, now);
        await store.ApplyNpcDamageAsync(pilot, 1, 10, default, shieldAbsorption: 0);
        await store.MoveAsync(pilot, 2100, 1300, null, default, now);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));
        await store.RepairAtBaseAsync(pilot, now.AddMilliseconds(100), default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));
        await store.RepairAtBaseAsync(pilot, now.AddSeconds(1), default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(1000));
    }

    [Test]
    public async Task StationRepair_RechargesShieldWhenHullIsAlreadyFull()
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = new WorldPilot(904, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
            { Stats = new WorldShipStats(300, 1000, 500, 0, 0, []) };
        await store.EnterAsync(pilot, default);
        store.SetCurrentShield(pilot, 400);

        await store.RepairAtBaseAsync(pilot, DateTimeOffset.UtcNow, default);

        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(1000));
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(410));
        var replica = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var restored = await replica.RestoreAsync(pilot, default);
        Assert.That(restored.ShipId, Is.EqualTo("ship_pusat-plus"));
        Assert.That(replica.CurrentShield(restored), Is.EqualTo(410));
    }

    [Test]
    public async Task PilotShieldRecovery_WaitsForCombatQuiet_AndPersistsCappedTicks()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(cache, clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 905, Stats = new WorldShipStats(300, 1000, 500, 0, 0, [])
        }, default);
        store.SetCurrentShield(pilot, 470);
        await store.MarkCombatAsync(pilot, default);

        clock.Advance(TimeSpan.FromSeconds(9));
        await store.RecoverShieldsAsync(pilot.MapId, default);
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(470));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.RecoverShieldsAsync(pilot.MapId, default);
        await store.RecoverShieldsAsync(pilot.MapId, default);
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(490));

        clock.Advance(TimeSpan.FromSeconds(1));
        await store.RecoverShieldsAsync(pilot.MapId, default);
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(500));
        var replica = new WorldSessionStore(cache, clock: clock);
        var restored = await replica.RestoreAsync(pilot, default);
        Assert.That(replica.CurrentShield(restored), Is.EqualTo(500));

        store.SetCurrentShield(pilot, 400);
        await store.MarkCombatAsync(pilot, default);
        clock.Advance(TimeSpan.FromSeconds(9));
        await store.RecoverShieldsAsync(pilot.MapId, default);
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(400));
    }

    [Test]
    public async Task PilotHullRecovery_RequiresQuietEvenInFlight_AndSurvivesReplicaHandoff()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(cache, clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 906, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 60, default);
        await store.MoveAsync(pilot, 20000, 5000, null, default, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromSeconds(9));
        await store.RecoverHullAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(940));
        Assert.That(store.RepairBotActive(pilot.UserId), Is.False);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.RecoverHullAsync(pilot.MapId, default);
        Assert.That(store.RepairBotActive(pilot.UserId), Is.True);
        Assert.That(store.CurrentPosition(pilot, clock.GetUtcNow()).X, Is.LessThan(20000));
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(965));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.RecoverHullAsync(pilot.MapId, default);
        await store.RecoverHullAsync(pilot.MapId, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.EqualTo(990));

        var replica = new WorldSessionStore(cache, clock: clock);
        await replica.EnterAsync(pilot, default);
        Assert.That(replica.RepairBotActive(pilot.UserId), Is.True);
        await replica.RecoverHullAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(990));
        clock.Advance(TimeSpan.FromSeconds(1));
        await replica.RecoverHullAsync(pilot.MapId, default);
        Assert.That(replica.CurrentHitPoints(pilot), Is.EqualTo(1000));
        await replica.RecoverHullAsync(pilot.MapId, default);
        Assert.That(replica.RepairBotActive(pilot.UserId), Is.False);
    }

    [Test]
    public async Task FieldRepairBot_DeactivatesWhenCombatResumes()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(cache, clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 908, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 100, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.RecoverHullAsync(pilot.MapId, default);
        Assert.That(store.RepairBotActive(pilot.UserId), Is.True);

        await store.MarkCombatAsync(pilot, default);
        Assert.That(store.RepairBotActive(pilot.UserId), Is.False);
        var replica = new WorldSessionStore(cache, clock: clock);
        await replica.EnterAsync(pilot, default);
        Assert.That(replica.RepairBotActive(pilot.UserId), Is.False);
        await replica.RecoverHullAsync(pilot.MapId, default);
        Assert.That(replica.RepairBotActive(pilot.UserId), Is.False);
    }

    [Test]
    public async Task PilotDestruction_BlocksActions_AndRespawnsOnceAtFactionBaseAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 907, MapId = 2, FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(store.CurrentHitPoints(pilot), Is.Zero);
        Assert.ThrowsAsync<WorldPilotDestroyedException>(async () =>
            await store.MoveAsync(pilot, 6000, 5000, null, default));
        Assert.ThrowsAsync<WorldPilotDestroyedException>(async () =>
            await store.JumpAsync(pilot, 201, null, default));
        Assert.ThrowsAsync<WorldPilotDestroyedException>(async () =>
            await store.SwitchAsync(pilot, 2, default));
        await store.RepairAtBaseAsync(pilot with { X = 19000, Y = 1300 }, DateTimeOffset.UtcNow, default);
        Assert.That(store.CurrentHitPoints(pilot), Is.Zero);

        var replica = new WorldSessionStore(cache);
        var respawned = await replica.RespawnAtBaseAsync(pilot, default);
        Assert.That(respawned, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(respawned!.MapId, Is.EqualTo(1));
            Assert.That((respawned.X, respawned.Y), Is.EqualTo((2000, 1300)));
            Assert.That(replica.CurrentHitPoints(respawned), Is.EqualTo(10));
            Assert.That(replica.CurrentShield(respawned), Is.Zero);
        });
        Assert.That(await store.RespawnAtBaseAsync(respawned!, default), Is.Null);
        var restored = await store.RestoreAsync(pilot, default);
        Assert.That(restored.MapId, Is.EqualTo(1));
        Assert.That(store.CurrentHitPoints(restored), Is.EqualTo(10));
        Assert.That((await store.MoveAsync(restored, 3000, 1300, null, default)).X, Is.EqualTo(3000));
    }

    [Test]
    public async Task PilotDestruction_FreezesTravel_AndCanRespawnAtDeathLocationOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 908, MapId = 2, FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.MoveAsync(pilot, 6000, 5000, null, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var dead = await new WorldSessionStore(cache).RestoreAsync(pilot, default);
        var deathPosition = (dead.X, dead.Y);
        Assert.That(store.MoveId(dead), Is.EqualTo(Guid.Empty));
        Assert.That(deathPosition.Item1, Is.InRange(5000, 6000));
        Assert.That((await store.RestoreAsync(pilot, default)).X, Is.EqualTo(deathPosition.Item1));

        var replica = new WorldSessionStore(cache);
        var respawned = await replica.RespawnAsync(dead, WorldRespawnLocation.DeathLocation, default);
        Assert.That(respawned, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(respawned!.MapId, Is.EqualTo(2));
            Assert.That((respawned.X, respawned.Y), Is.EqualTo(deathPosition));
            Assert.That(replica.CurrentHitPoints(respawned), Is.EqualTo(100));
            Assert.That(replica.CurrentShield(respawned), Is.Zero);
        });
        Assert.That(await store.RespawnAtBaseAsync(respawned!, default), Is.Null);
    }

    [Test]
    public async Task PilotDestruction_GateMapRejectsDeathLocationRepair()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 909, MapId = 101, FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await store.RespawnAsync(pilot, WorldRespawnLocation.DeathLocation, default), Is.Null);
        Assert.That(await store.RespawnAsync(pilot, WorldRespawnLocation.NearestPortal, default), Is.Null);
        Assert.That((await store.RespawnAtBaseAsync(pilot, default))?.MapId, Is.EqualTo(1));
    }

    [Test]
    public async Task PilotDestruction_IneligiblePortalRepairDoesNotCharge()
    {
        var emptyMap = new WorldMapInstance(2, 1, 21000, 13100,
            new WorldStation(200, 19000, 1300, 1000), []);
        Assert.That(emptyMap.AllowsPortalRepair, Is.False);
        Assert.That(WorldMapInstance.Starter(1).AllowsPortalRepair, Is.True);
        Assert.That(WorldMapInstance.Starter(101).AllowsPortalRepair, Is.False);

        var disabledMap = emptyMap with
        {
            Portals = [new WorldPortal(201, 100, 100, 1, 100, 100, Working: false)]
        };
        Assert.That(disabledMap.AllowsPortalRepair, Is.False);
        Assert.That(disabledMap.NearestWorkingPortal(100, 100), Is.Null);

        var mixedMap = disabledMap with
        {
            Portals = [.. disabledMap.Portals, new WorldPortal(202, 500, 500, 1, 100, 100)]
        };
        Assert.That(mixedMap.AllowsPortalRepair, Is.True);
        Assert.That(mixedMap.NearestWorkingPortal(100, 100)?.Id, Is.EqualTo(202));

        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var wallet = new TestRepairWallet { CanPay = true };
        var store = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(),
            repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 964, AccountId = Guid.NewGuid(), MapId = 101, FactionId = 1,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);

        Assert.That(await store.RespawnAsync(pilot, WorldRespawnLocation.NearestPortal, default), Is.Null);
        Assert.That(wallet.Keys, Is.Empty);
        Assert.That(store.CurrentHitPoints(pilot), Is.Zero);
    }

    [Test]
    public async Task PortalOperation_SharedStateControlsJumpAndPaidRepair()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var operations = new WorldPortalOperations(cache);
        var wallet = new TestRepairWallet { CanPay = true };
        var first = new WorldSessionStore(cache, repairWallet: wallet, portalOperations: operations);
        var replica = new WorldSessionStore(cache, repairWallet: wallet,
            portalOperations: new WorldPortalOperations(cache));
        var pilot = await first.EnterAsync(Pilot() with
        {
            UserId = 965, AccountId = Guid.NewGuid(), MapId = 2, X = 1500, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 100, [])
        }, default);

        await operations.SetWorkingAsync(2, 201, false, default);
        Assert.That((await replica.MapAsync(2, default)).AllowsPortalRepair, Is.False);
        Assert.That(await replica.JumpAsync(pilot, 201, null, default), Is.Null);
        Assert.That(await replica.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));

        await first.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await replica.RespawnAsync(pilot, WorldRespawnLocation.NearestPortal, default), Is.Null);
        Assert.That(wallet.Keys, Is.Empty);

        await operations.SetWorkingAsync(2, 201, true, default);
        Assert.That((await replica.MapAsync(2, default)).AllowsPortalRepair, Is.True);
        var repaired = await replica.RespawnAsync(pilot, WorldRespawnLocation.NearestPortal, default);
        Assert.That((repaired?.X, repaired?.Y), Is.EqualTo((1500, 1300)));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PortalOperation_AnnouncesInitialAndChangedStateOnlyToMapPilots()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var operations = new WorldPortalOperations(cache);
        var sessions = new WorldSessionStore(cache);
        using var mapStream = new MemoryStream();
        using var otherStream = new MemoryStream();
        sessions.Attach(new WorldConnection(Pilot() with { UserId = 966, MapId = 2 },
            new WorldPacketWriter(mapStream)));
        sessions.Attach(new WorldConnection(Pilot() with { UserId = 967, MapId = 1 },
            new WorldPacketWriter(otherStream)));
        var announcer = new WorldPortalOperationAnnouncer();

        await announcer.RefreshAsync(2, operations, sessions, default);
        await announcer.RefreshAsync(2, operations, sessions, default);
        Assert.That(otherStream.Length, Is.Zero);
        var initialLength = mapStream.Length;
        await operations.SetWorkingAsync(2, 201, false, default);
        await announcer.RefreshAsync(2, operations, sessions, default);
        await announcer.RefreshAsync(2, operations, sessions, default);
        var changedLength = mapStream.Length;
        Assert.That(changedLength, Is.GreaterThan(initialLength));
        announcer.Forget(2);
        await announcer.RefreshAsync(2, operations, sessions, default);

        using var frames = new MemoryStream(mapStream.ToArray());
        var reader = new WorldPacketReader(frames);
        var packets = new List<WorldPacket>();
        while (await reader.ReadAsync() is { } packet) packets.Add(packet);
        Assert.That(packets.Select(p => p.Command),
            Is.All.EqualTo(WorldCommandId.PortalOperationCommand));
        Assert.That(packets.Select(p => WorldPayload.Decode(p.Payload, r =>
            (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), r.ReadBoolean()))),
            Is.EqualTo(new[] { (2, 201, true), (2, 201, false), (2, 201, false) }));
        Assert.That(otherStream.Length, Is.Zero);
    }

    [Test]
    public async Task PortalOperation_LateEntryCarriesCurrentWorkingState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var operations = new WorldPortalOperations(services.GetRequiredService<IDistributedCache>());
        await operations.SetWorkingAsync(2, 201, false, default);
        var map = await operations.MapAsync(2, default);
        using var stream = new MemoryStream();
        await WorldEntryPackets.SendAsync(new WorldPacketWriter(stream),
            Pilot() with { MapId = 2 }, default, runtimeMap: map);
        using var frames = new MemoryStream(stream.ToArray());
        var reader = new WorldPacketReader(frames);
        WorldPacket? portal = null;
        while (await reader.ReadAsync() is { } packet)
            if (packet.Command == WorldCommandId.JumpgateCreateCommand) portal = packet;
        Assert.That(portal, Is.Not.Null);
        Assert.That(WorldPayload.Decode(portal!.Value.Payload, r =>
        {
            var id = WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt32(r);
            return (id, r.ReadBoolean());
        }), Is.EqualTo((201, false)));
    }

    [Test]
    public async Task PilotDestruction_RepairProtectsFromNpcAndBoundaryDamageForFiveSecondsAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var owner = new WorldSessionStore(cache, clock: clock);
        var pilot = await owner.EnterAsync(Pilot() with
        {
            UserId = 913, X = 0, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await owner.ApplyNpcDamageAsync(pilot, 1, 1200, default);
        Assert.That(owner.CurrentHitPoints(pilot), Is.Zero);

        var replica = new WorldSessionStore(cache, clock: clock);
        var repaired = await replica.RespawnAsync(pilot, WorldRespawnLocation.DeathLocation, default);
        Assert.That(repaired, Is.Not.Null);
        await owner.ApplyNpcDamageAsync(repaired!, 1, 20, default);
        await owner.TickRadiationAsync(repaired.MapId, default);
        Assert.That(owner.CurrentHitPoints(repaired), Is.EqualTo(100));

        clock.Advance(TimeSpan.FromSeconds(5));
        await owner.TickRadiationAsync(repaired.MapId, default);
        Assert.That(owner.CurrentHitPoints(repaired), Is.EqualTo(90));
        await replica.ApplyNpcDamageAsync(repaired, 1, 20, default);
        Assert.That(replica.CurrentHitPoints(repaired), Is.EqualTo(70));
    }

    [TestCase(2, 10)]
    [TestCase(5, 5)]
    public async Task PilotDestruction_StagedRepairRetainsRemainingProtectionAfterCacheLoss(
        int elapsedSeconds, int expectedHitPoints)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var wallet = new TestRepairWallet { ThrowAfterStageOnce = true };
        var first = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var pilot = await first.EnterAsync(Pilot() with
        {
            UserId = 914 + elapsedSeconds, AccountId = Guid.NewGuid(), MapId = 2,
            X = 5000, Y = 5000, Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await first.ApplyNpcDamageAsync(pilot, 1, 1200, default);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.RespawnAsync(pilot, WorldRespawnLocation.Base, default));
        await cache.RemoveAsync($"world:repair-protection:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        var replica = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var repaired = await replica.EnterAsync(pilot, default);
        await replica.ApplyNpcDamageAsync(repaired, 1, 5, default);
        Assert.That(replica.CurrentHitPoints(repaired), Is.EqualTo(expectedHitPoints));
    }

    [Test]
    public async Task PilotDestruction_RepairsAtNearestPortalOnceAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 910, MapId = 1, FactionId = 1, X = 19400, Y = 11000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var replica = new WorldSessionStore(cache);
        var respawned = await replica.RespawnAsync(pilot, WorldRespawnLocation.NearestPortal, default);
        Assert.That(respawned, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(respawned!.MapId, Is.EqualTo(1));
            Assert.That((respawned.X, respawned.Y), Is.EqualTo((19500, 11800)));
            Assert.That(replica.CurrentHitPoints(respawned), Is.EqualTo(100));
            Assert.That(replica.CurrentShield(respawned), Is.Zero);
        });
        Assert.That(await store.RespawnAsync(respawned!, WorldRespawnLocation.NearestPortal, default), Is.Null);
        var restored = await store.RestoreAsync(pilot, default);
        Assert.That((restored.X, restored.Y), Is.EqualTo((19500, 11800)));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 30)]
    [TestCase(WorldRespawnLocation.NearestPortal, 15)]
    public async Task PilotDestruction_PremiumRepairCooldownSurvivesReplicaHandoff(
        WorldRespawnLocation location, int cooldownSeconds)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var store = new WorldSessionStore(cache, clock: clock);
        var replica = new WorldSessionStore(cache, clock: clock);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 911 + cooldownSeconds, MapId = 2, FactionId = 1, X = 5000, Y = 5000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var first = await replica.RespawnAsync(pilot, location, default);
        Assert.That(first, Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(5));
        await store.ApplyNpcDamageAsync(first!, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await replica.RespawnAsync(first!, location, default), Is.Null);
        Assert.That(store.CurrentHitPoints(first!), Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(cooldownSeconds - 6));
        Assert.That(await store.RespawnAsync(first!, location, default), Is.Null);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await replica.RespawnAsync(first!, location, default);
        Assert.That(second, Is.Not.Null);
        Assert.That(replica.CurrentHitPoints(second!), Is.EqualTo(100));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 30)]
    [TestCase(WorldRespawnLocation.NearestPortal, 15)]
    public async Task PilotDestruction_PaidRepairCooldownSurvivesCacheLoss(
        WorldRespawnLocation location, int cooldownSeconds)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var wallet = new TestRepairWallet { CanPay = true };
        var firstStore = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var pilot = await firstStore.EnterAsync(Pilot() with
        {
            UserId = 1200 + cooldownSeconds, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await firstStore.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var repaired = await firstStore.RespawnAsync(pilot, location, default);
        Assert.That(repaired, Is.Not.Null);
        await cache.RemoveAsync($"world:respawn-cooldown:{pilot.UserId}:{location}");
        var replica = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        clock.Advance(TimeSpan.FromSeconds(5));
        await replica.ApplyNpcDamageAsync(repaired!, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await replica.RespawnAsync(repaired!, location, default), Is.Null);
        clock.Advance(TimeSpan.FromSeconds(cooldownSeconds - 5));
        var second = await replica.RespawnAsync(repaired!, location, default);
        Assert.That(second, Is.Not.Null);
        Assert.That(wallet.Keys, Has.Count.EqualTo(2));
        await cache.RemoveAsync($"world:respawn-cooldown:{pilot.UserId}:{location}");
        clock.Advance(TimeSpan.FromSeconds(5));
        await replica.ApplyNpcDamageAsync(second!, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await replica.RespawnAsync(second!, location, default), Is.Null);
    }

    [TestCase(WorldRespawnLocation.DeathLocation)]
    [TestCase(WorldRespawnLocation.NearestPortal)]
    public async Task PilotDestruction_StagedPaidRepairRetryBypassesItsOwnCooldown(
        WorldRespawnLocation location)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterStageOnce = true };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1260 + (int)location, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(pilot, location, default));
        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        Assert.That(await replica.RespawnAsync(pilot, location, default), Is.Not.Null);
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 30, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 15, 200)]
    public async Task PilotDestruction_PaidRepairRequiresFundsAndChargesOncePerLife(
        WorldRespawnLocation location, int cooldownSeconds, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new AdvancingClock();
        var wallet = new TestRepairWallet { CanPay = false };
        var store = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var replica = new WorldSessionStore(cache, clock: clock, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1000 + cooldownSeconds, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.That(await replica.RespawnAsync(pilot, location, default), Is.Null);
        Assert.That(wallet.Keys, Is.Empty);

        wallet.CanPay = true;
        var first = await replica.RespawnAsync(pilot, location, default);
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Uridium, Is.EqualTo(1000 - price));
        var replay = await store.RespawnAsync(first, location, default);
        Assert.That(replay, Is.Not.Null);
        Assert.That(replay!.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        var otherLocation = location is WorldRespawnLocation.DeathLocation
            ? WorldRespawnLocation.NearestPortal : WorldRespawnLocation.DeathLocation;
        Assert.That(await store.RespawnAsync(first, otherLocation, default), Is.Null);

        clock.Advance(TimeSpan.FromSeconds(5));
        await store.ApplyNpcDamageAsync(first, 1, 1200, default, shieldAbsorption: 0);
        clock.Advance(TimeSpan.FromSeconds(cooldownSeconds - 5));
        var second = await replica.RespawnAsync(first, location, default);
        Assert.That(second, Is.Not.Null);
        Assert.That(wallet.Keys, Has.Count.EqualTo(2));
        Assert.That(wallet.Keys[1], Is.Not.EqualTo(wallet.Keys[0]));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_BaseRetryResumesInterruptedPaidRepair(
        WorldRespawnLocation paidLocation, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1200 + price, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var stillDead = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(stillDead), Is.Zero);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(pilot, paidLocation, default));
        var restored = await replica.RespawnAsync(pilot, WorldRespawnLocation.Base, default);

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.MapId, Is.EqualTo(2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        if (paidLocation is WorldRespawnLocation.DeathLocation)
            Assert.That((restored.X, restored.Y), Is.EqualTo((5000, 5000)));
        else
            Assert.That((restored.X, restored.Y), Is.Not.EqualTo((5000, 5000)));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_ReentryCompletesInterruptedPaidRepair(
        WorldRespawnLocation paidLocation, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1500 + price, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(pilot, paidLocation, default));
        var restored = await replica.EnterAsync(pilot, default);

        Assert.That(replica.CurrentHitPoints(restored), Is.GreaterThan(0));
        Assert.That(restored.MapId, Is.EqualTo(2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        if (paidLocation is WorldRespawnLocation.DeathLocation)
            Assert.That((restored.X, restored.Y), Is.EqualTo((5000, 5000)));
        else
            Assert.That((restored.X, restored.Y), Is.Not.EqualTo((5000, 5000)));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_ReentryRecoversMissingDeathKeyAfterCharge(
        WorldRespawnLocation paidLocation, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1800 + price, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(pilot, paidLocation, default));
        await cache.RemoveAsync($"world:death:{pilot.UserId}");
        store.SetCurrentShield(pilot, 0); // A later runtime write must retain the death identity.

        var restored = await replica.EnterAsync(pilot, default);

        Assert.That(replica.CurrentHitPoints(restored), Is.GreaterThan(0));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        Assert.That(await cache.GetStringAsync($"world:death:{pilot.UserId}"), Is.EqualTo(wallet.Keys[0]));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_ReentryRecoversAfterBothDeathCachesAreLost(
        WorldRespawnLocation paidLocation, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var pilot = await store.EnterAsync(Pilot() with
        {
            UserId = 1900 + price, AccountId = Guid.NewGuid(), MapId = 2,
            FactionId = 1, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, [])
        }, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(pilot, paidLocation, default));
        await cache.RemoveAsync($"world:death:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");

        var restored = await replica.EnterAsync(pilot, default);

        Assert.That(replica.CurrentHitPoints(restored), Is.GreaterThan(0));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        Assert.That(await cache.GetStringAsync($"world:death:{pilot.UserId}"), Is.EqualTo(wallet.Keys[0]));
    }

    [Test]
    public async Task PilotDestruction_UnpaidDeathSurvivesLossOfBothCacheEntries()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet();
        var pilot = Pilot() with { UserId = 2410, AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        pilot = await store.EnterAsync(pilot, default);
        await store.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        await cache.RemoveAsync($"world:death:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var dead = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(dead), Is.Zero);
        Assert.That(await wallet.FindPendingDeathAsync(pilot.AccountId, default), Is.Not.Null);
        var restored = await replica.RespawnAtBaseAsync(dead, default);
        Assert.That(restored, Is.Not.Null);
        Assert.That(await wallet.FindPendingDeathAsync(pilot.AccountId, default), Is.Null);
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        var next = new WorldSessionStore(cache, repairWallet: wallet);
        var entered = await next.EnterAsync(restored!, default);
        Assert.That(next.CurrentHitPoints(entered), Is.GreaterThan(0));
    }

    [Test]
    public async Task PilotDestruction_DurableDeathOverridesLiveCacheAfterInterruptedDamage()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { ThrowAfterRecordOnce = true };
        var pilot = Pilot() with { UserId = 2411, AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        pilot = await first.EnterAsync(pilot, default);
        first.SetCurrentShield(pilot, 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0));
        Assert.That(await cache.GetStringAsync($"world:runtime:{pilot.UserId}"), Is.Not.Null);

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var dead = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(dead), Is.Zero);
        Assert.That(replica.CurrentShield(dead), Is.Zero);
        var next = new WorldSessionStore(cache, repairWallet: wallet);
        var reentered = await next.EnterAsync(pilot, default);
        Assert.That(next.CurrentHitPoints(reentered), Is.Zero);
        Assert.That(await next.RespawnAtBaseAsync(reentered, default), Is.Not.Null);
    }

    [Test]
    public async Task PilotDestruction_GameplayRestoreReconcilesInterruptedDeath()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { ThrowAfterRecordOnce = true };
        var pilot = Pilot() with { UserId = 2412, AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        pilot = await first.EnterAsync(pilot, default);
        first.SetCurrentShield(pilot, 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0));

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.RestoreAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(restored), Is.Zero);
        Assert.That(replica.CurrentShield(restored), Is.Zero);
        var another = new WorldSessionStore(cache, repairWallet: wallet);
        var again = await another.RestoreAsync(pilot, default);
        Assert.That(another.CurrentHitPoints(again), Is.Zero);
    }

    [Test]
    public async Task PilotDestruction_GameplayRestoreRecoversDeathPositionAfterCacheLoss()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { ThrowAfterRecordOnce = true };
        var original = Pilot() with { UserId = 2413, AccountId = Guid.NewGuid(),
            MapId = 1, X = 1000, Y = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        await first.EnterAsync(original, default);
        var moved = original with { X = 5000, Y = 5000 };
        await first.PersistAsync(moved, default);
        first.SetCurrentShield(moved, 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.ApplyNpcDamageAsync(moved, 1, 1200, default, shieldAbsorption: 0));
        await cache.RemoveAsync($"world:position:{original.UserId}");

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.RestoreAsync(original, default);
        Assert.That((restored.X, restored.Y), Is.EqualTo((5000, 5000)));
        Assert.That(replica.CurrentHitPoints(restored), Is.Zero);
        var another = new WorldSessionStore(cache, repairWallet: wallet);
        var again = await another.RestoreAsync(original, default);
        Assert.That((again.X, again.Y), Is.EqualTo((5000, 5000)));
        Assert.That(another.CurrentHitPoints(again), Is.Zero);
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_PaidRepairUsesDurableDeathPositionAfterCacheLoss(
        WorldRespawnLocation location, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var original = Pilot() with { UserId = 2600 + price, AccountId = Guid.NewGuid(),
            MapId = 1, X = 1000, Y = 1000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var store = new WorldSessionStore(cache, repairWallet: wallet);
        await store.EnterAsync(original, default);
        await store.PersistAsync(original with { MapId = 2, X = 5000, Y = 5000 }, default);
        var moved = original with { MapId = 2, X = 5000, Y = 5000 };
        await store.ApplyNpcDamageAsync(moved, 1, 1200, default, shieldAbsorption: 0);
        var dead = await store.RestoreAsync(moved, default);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RespawnAsync(dead, location, default));
        await cache.RemoveAsync($"world:position:{original.UserId}");
        await cache.RemoveAsync($"world:runtime:{original.UserId}");
        await cache.RemoveAsync($"world:death:{original.UserId}");

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.EnterAsync(original, default);
        Assert.That(replica.CurrentHitPoints(restored), Is.GreaterThan(0));
        Assert.That(restored.MapId, Is.EqualTo(2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        if (location is WorldRespawnLocation.DeathLocation)
            Assert.That((restored.X, restored.Y), Is.EqualTo((5000, 5000)));
        else
            Assert.That((restored.X, restored.Y), Is.Not.EqualTo((1000, 1000)));
    }

    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_GameplayRestoreResumesChargedRepairAfterCacheLoss(
        WorldRespawnLocation location, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterChargeOnce = true };
        var original = Pilot() with { UserId = 2800 + price, AccountId = Guid.NewGuid(),
            MapId = 2, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        await first.EnterAsync(original, default);
        await first.ApplyNpcDamageAsync(original, 1, 1200, default, shieldAbsorption: 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.RespawnAsync(original, location, default));
        await cache.RemoveAsync($"world:position:{original.UserId}");
        await cache.RemoveAsync($"world:runtime:{original.UserId}");
        await cache.RemoveAsync($"world:death:{original.UserId}");

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.RestoreAsync(original, default);
        Assert.That(replica.CurrentHitPoints(restored), Is.GreaterThan(0));
        Assert.That(restored.MapId, Is.EqualTo(2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(wallet.Keys, Has.Count.EqualTo(1));
        Assert.That(await wallet.FindPendingDeathAsync(original.AccountId, default), Is.Null);
        if (location is WorldRespawnLocation.DeathLocation)
            Assert.That((restored.X, restored.Y), Is.EqualTo((5000, 5000)));
        else
            Assert.That((restored.X, restored.Y), Is.Not.EqualTo((5000, 5000)));
        var next = new WorldSessionStore(cache, repairWallet: wallet);
        Assert.That(next.CurrentHitPoints(await next.RestoreAsync(original, default)), Is.GreaterThan(0));
    }

    [TestCase(WorldRespawnLocation.Base, 0, false)]
    [TestCase(WorldRespawnLocation.DeathLocation, 300, false)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200, false)]
    [TestCase(WorldRespawnLocation.Base, 0, true)]
    [TestCase(WorldRespawnLocation.DeathLocation, 300, true)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200, true)]
    public async Task PilotDestruction_ReentryFinishesStagedRepairAfterCacheLoss(
        WorldRespawnLocation location, int price, bool restoreStaleRuntime)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterCompleteOnce = true };
        var pilot = Pilot() with { UserId = 2500 + price, AccountId = Guid.NewGuid(),
            MapId = 2, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        pilot = await first.EnterAsync(pilot, default);
        first.SetRadiationWarning(pilot.UserId, true);
        await first.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        var staleRuntime = await cache.GetStringAsync($"world:runtime:{pilot.UserId}");
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.RespawnAsync(pilot, location, default));
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        await cache.RemoveAsync($"world:death:{pilot.UserId}");
        await cache.RemoveAsync($"world:position:{pilot.UserId}");
        if (restoreStaleRuntime)
            await cache.SetStringAsync($"world:runtime:{pilot.UserId}", staleRuntime!);

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.EnterAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(restored),
            Is.EqualTo(location == WorldRespawnLocation.Base ? 10 : 100));
        Assert.That(restored.MapId, Is.EqualTo(location == WorldRespawnLocation.Base ? 1 : 2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(replica.RadiationWarning(pilot.UserId), Is.False);
        Assert.That(await wallet.FindPendingDeathAsync(pilot.AccountId, default), Is.Null);
        Assert.That(wallet.Keys, Has.Count.EqualTo(price == 0 ? 0 : 1));
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        var next = new WorldSessionStore(cache, repairWallet: wallet);
        Assert.That(next.CurrentHitPoints(await next.EnterAsync(restored, default)), Is.GreaterThan(0));
    }

    [TestCase(WorldRespawnLocation.Base, 0)]
    [TestCase(WorldRespawnLocation.DeathLocation, 300)]
    [TestCase(WorldRespawnLocation.NearestPortal, 200)]
    public async Task PilotDestruction_GameplayRestoreFinishesStagedRepairAfterCacheLoss(
        WorldRespawnLocation location, int price)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var wallet = new TestRepairWallet { CanPay = true, ThrowAfterCompleteOnce = true };
        var pilot = Pilot() with { UserId = 2700 + price, AccountId = Guid.NewGuid(),
            MapId = 2, X = 5000, Y = 5000, Uridium = 1000,
            Stats = new WorldShipStats(300, 1000, 100, 0, 100, []) };
        var first = new WorldSessionStore(cache, repairWallet: wallet);
        pilot = await first.EnterAsync(pilot, default);
        first.SetRadiationWarning(pilot.UserId, true);
        await first.ApplyNpcDamageAsync(pilot, 1, 1200, default, shieldAbsorption: 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.RespawnAsync(pilot, location, default));
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        await cache.RemoveAsync($"world:death:{pilot.UserId}");
        await cache.RemoveAsync($"world:position:{pilot.UserId}");

        var replica = new WorldSessionStore(cache, repairWallet: wallet);
        var restored = await replica.RestoreAsync(pilot, default);
        Assert.That(replica.CurrentHitPoints(restored),
            Is.EqualTo(location == WorldRespawnLocation.Base ? 10 : 100));
        Assert.That(restored.MapId, Is.EqualTo(location == WorldRespawnLocation.Base ? 1 : 2));
        Assert.That(restored.Uridium, Is.EqualTo(1000 - price));
        Assert.That(replica.RadiationWarning(pilot.UserId), Is.False);
        Assert.That(await wallet.FindPendingDeathAsync(pilot.AccountId, default), Is.Null);
        Assert.That(wallet.Keys, Has.Count.EqualTo(price == 0 ? 0 : 1));
        var next = new WorldSessionStore(cache, repairWallet: wallet);
        var again = await next.RestoreAsync(pilot, default);
        Assert.That(next.CurrentHitPoints(again), Is.GreaterThan(0));
        Assert.That((again.MapId, again.X, again.Y), Is.EqualTo((restored.MapId, restored.X, restored.Y)));
    }

    [TestCase("0", 0)]
    [TestCase("1000000", 1000000)]
    [TestCase("-1", 150)]
    [TestCase("1000001", 150)]
    [TestCase("1.5", 150)]
    [TestCase("\"500\"", 150)]
    public void ShipStats_CatalogOverrideMustBeIntegerWithinRange(string damageJson, int expected)
    {
        var equipment = new LocalEquipmentState([new("laser_lf3", "", "", 1)], [new(1, 1, "laser_lf3", 1)]);
        using var stats = System.Text.Json.JsonDocument.Parse($"{{\"damage\":{damageJson}}}");
        using var prices = System.Text.Json.JsonDocument.Parse("{}");
        var listing = new EconomyListing("laser_lf3", "laser_lf3", "laser", "laser", "common",
            stats.RootElement, null, prices.RootElement, null, null, true);
        var result = WorldShipStatCalculator.Calculate(1, equipment, new LocalPilotSkillState([]), [listing]);
        Assert.That(result.LaserDamage, Is.EqualTo(expected));
    }

    [Test]
    public void ShipStats_CatalogOverridesArePerFieldAndZeroIsAuthoritative()
    {
        var equipment = new LocalEquipmentState(
            [new("laser_lf3", "", "", 1), new("shield_sg3n_a03", "", "", 1),
             new("generator_g3n_3310", "", "", 1)],
            [new(1, 1, "laser_lf3", 1), new(1, 1, "shield_sg3n_a03", 1),
             new(1, 1, "generator_g3n_3310", 1)]);
        using var laser = System.Text.Json.JsonDocument.Parse("{\"damage\":0,\"cargo\":9000}");
        using var shield = System.Text.Json.JsonDocument.Parse("{\"shield\":1000000,\"cargo\":9000}");
        using var generator = System.Text.Json.JsonDocument.Parse("{\"speed\":0,\"cargo\":9000}");
        using var prices = System.Text.Json.JsonDocument.Parse("{}");
        EconomyListing Listing(string id, System.Text.Json.JsonElement stats) =>
            new(id, id, "test", "test", "common", stats, null, prices.RootElement, null, null, true);

        var result = WorldShipStatCalculator.Calculate(1, equipment, new LocalPilotSkillState([]),
            [Listing("laser_lf3", laser.RootElement), Listing("shield_sg3n_a03", shield.RootElement),
             Listing("generator_g3n_3310", generator.RootElement)], baseSpeed: 42);

        Assert.Multiple(() =>
        {
            Assert.That(result.Speed, Is.EqualTo(42));
            Assert.That(result.MaxShield, Is.EqualTo(1_000_000));
            Assert.That(result.LaserDamage, Is.Zero);
            Assert.That(result.Equipment.Select(a => a.ItemId),
                Is.EqualTo(new[] { "laser_lf3", "shield_sg3n_a03", "generator_g3n_3310" }));
        });
    }

    [Test]
    public void ShipStats_FirstMatchingCatalogListingControlsFallbackPerRequestedField()
    {
        var equipment = new LocalEquipmentState([new("laser_lf3", "", "", 1)],
            [new(1, 1, "laser_lf3", 1)]);
        using var first = System.Text.Json.JsonDocument.Parse("{\"shield\":999}");
        using var second = System.Text.Json.JsonDocument.Parse("{\"damage\":500}");
        using var prices = System.Text.Json.JsonDocument.Parse("{}");
        EconomyListing Listing(System.Text.Json.JsonElement stats) =>
            new("laser_lf3", "laser_lf3", "laser", "laser", "common", stats,
                null, prices.RootElement, null, null, true);

        var result = WorldShipStatCalculator.Calculate(1, equipment, new LocalPilotSkillState([]),
            [Listing(first.RootElement), Listing(second.RootElement)]);

        Assert.That(result.LaserDamage, Is.EqualTo(150));
    }

    [Test]
    public void ShipStats_SkillBonusesClampRank_TruncateAndIgnoreOverQuantityAssignments()
    {
        var equipment = new LocalEquipmentState(
            [new("laser_lf2", "", "", 1), new("shield_sg3n_a03", "", "", 1), new("generator_g3n_1010", "", "", 1),
             new("laser_lf3", "", "", 2)],
            [new(1, 1, "laser_lf2", 1), new(1, 1, "shield_sg3n_a03", 1), new(1, 1, "generator_g3n_1010", 1),
             new(1, 1, "laser_lf3", 3), new(1, 1, "laser_lf2", 0), new(2, 1, "laser_lf2", 1)]);
        var skills = new LocalPilotSkillState(new() { ["hull"] = 9, ["shields"] = 1, ["lasers"] = 2 });
        var pusat = WorldShipStatCalculator.Calculate(1, equipment, skills);
        var phoenix = WorldShipStatCalculator.Calculate(2, equipment, skills, baseSpeed: 100);
        var invalid = new[] { 0, 3, -1 };
        Assert.Multiple(() =>
        {
            Assert.That((pusat.Speed, pusat.MaxHitPoints, pusat.MaxShield, pusat.LaserDamage),
                Is.EqualTo((380, 140800L, 3060L, 104)));
            Assert.That(pusat.Equipment.Select(a => a.ItemId),
                Is.EqualTo(new[] { "laser_lf2", "shield_sg3n_a03", "generator_g3n_1010" }));
            Assert.That((phoenix.Speed, phoenix.MaxHitPoints, phoenix.LaserDamage), Is.EqualTo((100, 4400L, 104)));
            foreach (var configuration in invalid)
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    WorldShipStatCalculator.Calculate(1, equipment, skills, configuration: configuration));
        });
    }

    [Test]
    public void Companions_RequireOwnership_ActiveConfiguration_AndFuel()
    {
        var equipment = new LocalEquipmentState(
            [new("drone_flax", "", "", 2), new("pet_10", "", "", 1)],
            [new(1, 1, "drone_flax", 2), new(1, 1, "pet_10", 1), new(1, 2, "drone_iris", 1)]);
        var active = WorldCompanions.FromEquipment(equipment, 1, new LocalPet(true, 5));
        Assert.That(active.Select(c => c.Kind), Is.EqualTo(new[] { "drone", "drone", "pet" }));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet(true, 0)).Count, Is.EqualTo(2));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet(true, 5), 2), Is.Empty);
        Assert.That(WorldCompanions.At(active, 0, 0).All(p => p.X >= 0 && p.Y >= 0), Is.True);
    }

    [Test]
    public void CollectibleCycles_AreDeterministic_MapScoped_AndBounded()
    {
        var first = WorldCollectibles.Spawn(1, 100);
        Assert.That(first, Is.EqualTo(WorldCollectibles.Spawn(1, 100)));
        Assert.That(first.Select(b => (b.Kind, b.AssetId, b.Amount)), Is.EqualTo(new[]
        {
            ("bonus", "credits", 200), ("bonus", "uridium", 5), ("bonus", "galaxy_energy", 1),
            ("ore", "prometium", 10), ("ore", "endurium", 8), ("ore", "terbium", 6)
        }));
        Assert.That(first[0].Id, Is.EqualTo((1 << 26) | (100 << 10) | 1));
        Assert.That(WorldCollectibles.Spawn(2, 100)[0].Id, Is.Not.EqualTo(first[0].Id));
        Assert.That(WorldCollectibles.Spawn(1, 101)[0].Id, Is.Not.EqualTo(first[0].Id));
        Assert.That(WorldCollectibles.Spawn(1, 101)[0].X, Is.Not.EqualTo(first[0].X));
        Assert.That(WorldCollectibles.Spawn(1, 100, -1), Is.Empty);
        Assert.That(WorldCollectibles.Spawn(1, 100, 1001).Count, Is.EqualTo(1000));
    }

    [Test]
    public async Task CollectibleCreditFailure_KeepsClaimReservedAndPreventsRetry()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var wallet = new FailingCollectibleWallet();
        var clock = new TestClock();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, clock);
        var sessions = new WorldSessionStore(cache, mutex);
        var box = (await collectibles.VisibleAsync(1, default)).First();
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = box.X, Y = box.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await collectibles.CollectAsync(pilot, box.Id, sessions, default));
        Assert.That((await collectibles.VisibleAsync(1, default)).Any(item => item.Id == box.Id), Is.False);
        Assert.That((await collectibles.CollectAsync(pilot, box.Id, sessions, default)).Status,
            Is.EqualTo("claimed"));
        Assert.That(wallet.CreditCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task CollectiblePickup_FromPreviousMap_DoesNotClaimDestinationBox()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var wallet = new FailingCollectibleWallet { ThrowOnCredit = false };
        var collectibles = new WorldCollectibles(cache, mutex, wallet, new TestClock());
        var sessions = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var box = (await collectibles.VisibleAsync(2, default)).First();
        var stale = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var current = await destination.TeleportAsync(stale.UserId, 2, box.X, box.Y, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(async () =>
            await collectibles.CollectAsync(stale, box.Id, sessions, default));
        Assert.That(wallet.CreditCalls, Is.Zero);
        Assert.That((await collectibles.VisibleAsync(2, default)).Any(item => item.Id == box.Id), Is.True);
        Assert.That((await collectibles.CollectAsync(current!, box.Id, destination, default)).Status,
            Is.EqualTo("collected"));
        Assert.That(wallet.CreditCalls, Is.EqualTo(1));
    }

    [TestCase(0, "collected")]
    [TestCase(1, "cargo_full")]
    public async Task OrePickup_UsesStrictCargoCapacityBoundary(int excess, string expectedStatus)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var wallet = new FailingCollectibleWallet { ThrowOnCredit = false };
        var collectibles = new WorldCollectibles(cache, mutex, wallet, new TestClock());
        var sessions = new WorldSessionStore(cache, mutex);
        var ore = (await collectibles.VisibleAsync(1, default)).First(box => box.Kind == "ore");
        wallet.CargoUsed = 100 - ore.Amount + excess;
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = ore.X, Y = ore.Y,
            Stats = new WorldShipStats(370, 1000, 0, 100, 0, [])
        }, default);

        var result = await collectibles.CollectAsync(pilot, ore.Id, sessions, default);
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(wallet.CreditCalls, Is.EqualTo(excess == 0 ? 1 : 0));
        Assert.That((await collectibles.VisibleAsync(1, default)).Any(box => box.Id == ore.Id),
            Is.EqualTo(excess != 0));
    }

    [Test]
    public void NpcTemplates_ProduceStableMapScopedIds_AndBoundedCoordinates()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var npcs = new WorldNpcs(services.GetRequiredService<IDistributedCache>(), new TestMutex(),
            new TestRewards(), Options.Create(new WorldNpcOptions()), new TestClock());
        var spawn = npcs.Spawn(1, DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        Assert.Multiple(() =>
        {
            Assert.That(spawn.Select(n => n.Kind), Is.EqualTo(new[]
                { "Streuner", "Streuner", "Streuner", "Lordakia", "Lordakia", "Saimon" }));
            Assert.That(spawn.Select(n => n.Id), Is.EqualTo(new[] { 10001, 10002, 10003, 10101, 10102, 10201 }));
            Assert.That(spawn.All(n => n.X is >= 0 and <= 21000 && n.Y is >= 0 and <= 13100), Is.True);
            Assert.That(spawn[0].Hp, Is.EqualTo(800));
            Assert.That(spawn[0].Shield, Is.EqualTo(200));
        });
        Assert.That(npcs.Spawn(1, DateTimeOffset.FromUnixTimeSeconds(1_000_000)), Is.EqualTo(spawn));
        Assert.That(npcs.Spawn(2, DateTimeOffset.FromUnixTimeSeconds(1_000_000))[0].Id, Is.EqualTo(20001));
        Assert.That(npcs.Spawn(1, DateTimeOffset.FromUnixTimeSeconds(1_000_000),
            new Dictionary<string, int> { ["Streuner"] = 31 }).Any(n => n.Kind == "Streuner"), Is.False);
    }

    [Test]
    public async Task NpcLaser_AbsorbsShieldBeforeHull_AndSavesLethalReward()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var rewards = new TestRewards();
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, rewards,
            Options.Create(new WorldNpcOptions { Templates =
                [new("Streuner", "npc_streuner", 1, 100, 50, 400, 1, 400, 2, 0)] }), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var first = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo("hit"));
            Assert.That((first.Target!.Shield, first.Target.Hp), Is.EqualTo((0, 50)));
            Assert.That(first.Destroyed, Is.False);
            Assert.That(first.Reward, Is.Null);
        });
        var second = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(second.Destroyed, Is.True);
            Assert.That(second.Target!.Hp, Is.Zero);
            Assert.That(second.Reward, Is.EqualTo(new WorldNpcReward(400, 1, 400, 2)));
            Assert.That(rewards.Calls, Is.EqualTo(1));
        });
        Assert.That(await npcs.VisibleAsync(1, default), Is.Empty);
        Assert.That((await npcs.AttackAsync(pilot, spawn.Id, sessions, default)).Status, Is.EqualTo("destroyed"));
    }

    [Test]
    public async Task PilotLaser_MissKeepsTargetVitalityAndAllowsNextShot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            LaserMissChance = 1,
            Templates = [new("Scout", "npc_scout", 1, 100, 50, 5, 0, 0, 0, 0)]
        };
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var miss = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(miss.Status, Is.EqualTo("hit"));
            Assert.That(miss.Damage, Is.Zero);
            Assert.That((miss.Target!.Hp, miss.Target.Shield), Is.EqualTo((100, 50)));
        });
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.EqualTo(50));

        options.LaserMissChance = 0;
        var hit = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        Assert.That((hit.Damage, hit.Target!.Hp, hit.Target.Shield), Is.EqualTo((100, 50, 0)));
    }

    [Test]
    public async Task PilotMiss_RestartsNpcShieldRecoveryDelayAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = new WorldNpcOptions
        {
            AggroRange = 0, LaserMissChance = 0,
            Templates = [new("Scout", "npc_scout", 1, 1000, 100, 0, 0, 0, 0, 0)]
        };
        var first = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = first.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        await first.AttackAsync(pilot, spawn.Id, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(9));
        options.LaserMissChance = 1;
        var miss = await second.AttackAsync(pilot, spawn.Id, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickAggroAsync(1, sessions, default);
        var beforeDelay = (await second.VisibleAsync(1, default)).Single(n => n.Id == spawn.Id);
        clock.Advance(TimeSpan.FromSeconds(9));
        await second.TickAggroAsync(1, sessions, default);
        var afterDelay = (await first.VisibleAsync(1, default)).Single(n => n.Id == spawn.Id);

        Assert.Multiple(() =>
        {
            Assert.That(miss.Damage, Is.Zero);
            Assert.That(beforeDelay.Shield, Is.Zero);
            Assert.That(afterDelay.Shield, Is.EqualTo(10));
        });
    }

    [Test]
    public async Task PilotLaser_RollsBoundedDamageForEachSuccessfulShot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            LaserDamageSpread = 0.2,
            Templates = [new("Scout", "npc_scout", 1, 100_000, 0, 0, 0, 0, 0, 0)]
        };
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var shots = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var hit = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
            Assert.That(hit.Status, Is.EqualTo("hit"));
            shots.Add(hit.Damage);
        }

        var remainingHp = (await npcs.VisibleAsync(1, default)).Single().Hp;
        Assert.Multiple(() =>
        {
            Assert.That(shots, Is.All.InRange(80, 120));
            Assert.That(shots.Distinct().Count(), Is.GreaterThan(1));
            Assert.That(remainingHp, Is.EqualTo(100_000 - shots.Sum()));
        });
    }

    [Test]
    public async Task PilotRocket_DefaultRangeRejectsDistantTargetAtBoundary()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new TestClock();
        var options = new WorldNpcOptions
        {
            RocketDamage = 10,
            Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 0)]
        };
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var distant = await sessions.EnterAsync(Pilot() with
        {
            UserId = 71, AccountId = Guid.NewGuid(), X = spawn.X + 801, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var boundary = await sessions.EnterAsync(Pilot() with
        {
            UserId = 72, AccountId = Guid.NewGuid(), X = spawn.X + 800, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        Assert.That((await npcs.AttackAsync(distant, spawn.Id, sessions, default, rocket: true)).Status,
            Is.EqualTo("distant"));
        var hit = await npcs.AttackAsync(boundary, spawn.Id, sessions, default, rocket: true);
        Assert.That((hit.Status, hit.Damage, hit.Target!.Hp), Is.EqualTo(("hit", 10, 90)));
    }

    [Test]
    public async Task PilotRocket_MissConsumesCooldownWithoutChangingTargetVitality()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            RocketDamage = 50, RocketMissChance = 1, RocketCooldownSeconds = 3,
            Templates = [new("Scout", "npc_scout", 1, 100, 50, 5, 0, 0, 0, 0)]
        };
        var clock = new AdvancingClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var miss = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true);
        var cooldown = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true);
        Assert.Multiple(() =>
        {
            Assert.That(miss.Status, Is.EqualTo("hit"));
            Assert.That(miss.Damage, Is.Zero);
            Assert.That((miss.Target!.Hp, miss.Target.Shield), Is.EqualTo((100, 50)));
            Assert.That(cooldown.Status, Is.EqualTo("cooldown"));
        });

        clock.Advance(TimeSpan.FromSeconds(3));
        options.RocketMissChance = 0;
        var hit = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true);
        Assert.That((hit.Damage, hit.Target!.Hp, hit.Target.Shield), Is.EqualTo((50, 100, 0)));
    }

    [Test]
    public async Task PilotRocket_RollsBoundedDamageAcrossCooldowns()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            RocketDamage = 100, RocketDamageSpread = 0.2, RocketCooldownSeconds = 1,
            Templates = [new("Scout", "npc_scout", 1, 100_000, 0, 0, 0, 0, 0, 0)]
        };
        var clock = new AdvancingClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var shots = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var hit = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true);
            Assert.That(hit.Status, Is.EqualTo("hit"));
            shots.Add(hit.Damage);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var remainingHp = (await npcs.VisibleAsync(1, default)).Single().Hp;
        Assert.Multiple(() =>
        {
            Assert.That(shots, Is.All.InRange(80, 120));
            Assert.That(shots.Distinct().Count(), Is.GreaterThan(1));
            Assert.That(remainingHp, Is.EqualTo(100_000 - shots.Sum()));
        });
    }

    [Test]
    public async Task Launcher_FirstFireStartsSharedReloadWithoutSpendingAHit()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            LauncherCapacity = 2, LauncherReloadSeconds = 2, LauncherRocketDamage = 10,
            Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 0)]
        });
        var first = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var spawn = first.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var empty = await first.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        clock.Advance(TimeSpan.FromSeconds(2));
        var loaded = await second.LauncherStatusAsync(pilot.UserId, default);
        var shot = await second.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);

        Assert.Multiple(() =>
        {
            Assert.That(empty.Status, Is.EqualTo("reloading"));
            Assert.That(loaded, Is.EqualTo(new WorldLauncherStatus(1, 2, 2000)));
            Assert.That(shot.Damage, Is.EqualTo(10));
            Assert.That(shot.Target!.Hp, Is.EqualTo(90));
        });
    }

    [Test]
    public async Task AutoRocket_UsesSharedCooldownDuringSustainedLaserAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = Options.Create(new WorldNpcOptions
        {
            AutoRocketOnLaser = true, RocketDamage = 10, RocketCooldownSeconds = 3,
            LaserCooldownMilliseconds = 1000,
            Templates = [new("Scout", "npc_scout", 1, 1000, 0, 0, 0, 0, 0, 0)]
        });
        var clock = new AdvancingClock();
        var first = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var spawn = first.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 1, [])
        }, default);
        await first.SelectAsync(pilot, spawn.Id, default);
        await first.StartLaserAsync(pilot, spawn.Id, default);

        await first.SetAutoRocketAsync(pilot.UserId, false, default);

        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        var firstHit = (await first.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        await first.TickLasersAsync(pilot.MapId, sessions, default);
        var duplicate = (await second.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        await second.SetAutoRocketAsync(pilot.UserId, true, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickLasersAsync(pilot.MapId, sessions, default);
        var cooldown = (await second.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        clock.Advance(TimeSpan.FromSeconds(2));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        var ready = (await first.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);

        Assert.Multiple(() =>
        {
            Assert.That(firstHit.Hp, Is.EqualTo(999));
            Assert.That(duplicate.Hp, Is.EqualTo(999));
            Assert.That(cooldown.Hp, Is.EqualTo(988));
            Assert.That(ready.Hp, Is.EqualTo(987));
        });
    }

    [Test]
    public async Task LaserWithoutEquippedDamage_DoesNotHitOrTriggerAutomaticWeapons()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            AutoRocketOnLaser = true, AutoLauncherOnLaser = true,
            LauncherCapacity = 1, LauncherReloadSeconds = 1, RocketDamage = 50,
            LaserCooldownMilliseconds = 1000,
            Templates = [new("Scout", "npc_scout", 1, 1000, 0, 0, 0, 0, 0, 0)]
        });
        var first = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var spawn = first.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 0, [])
        }, default);
        await first.SelectAsync(pilot, spawn.Id, default);
        var direct = await first.AttackAsync(pilot, spawn.Id, sessions, default);
        await first.StartLaserAsync(pilot, spawn.Id, default);
        clock.Advance(TimeSpan.FromSeconds(2));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        var afterTick = (await second.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        clock.Advance(TimeSpan.FromSeconds(2));
        await first.TickLasersAsync(pilot.MapId, sessions, default);
        var afterStop = (await first.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        var rocket = await second.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true);

        Assert.Multiple(() =>
        {
            Assert.That(direct.Status, Is.EqualTo("no_lasers"));
            Assert.That(afterTick.Hp, Is.EqualTo(1000));
            Assert.That(afterStop.Hp, Is.EqualTo(1000));
            Assert.That(rocket.Status, Is.EqualTo("hit"));
            Assert.That(rocket.Damage, Is.EqualTo(50));
        });
    }

    [Test]
    public async Task AutoRocket_PilotToggleIsSharedAndOverridesServerDefault()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = Options.Create(new WorldNpcOptions { AutoRocketOnLaser = true });
        var first = new WorldNpcs(cache, mutex, new TestRewards(), options, new AdvancingClock());
        var second = new WorldNpcs(cache, mutex, new TestRewards(), options, new AdvancingClock());

        Assert.That(await first.AutoRocketEnabledAsync(71, default), Is.True);
        await first.SetAutoRocketAsync(71, false, default);
        Assert.That(await second.AutoRocketEnabledAsync(71, default), Is.False);
        Assert.That(await second.AutoRocketEnabledAsync(72, default), Is.True);
        await second.SetAutoRocketAsync(71, true, default);
        Assert.That(await first.AutoRocketEnabledAsync(71, default), Is.True);
    }

    [Test]
    public async Task AutoLauncher_FiresOnlyAtFullLoadDuringSustainedLaserAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            AutoLauncherOnLaser = true, LauncherCapacity = 2, LauncherReloadSeconds = 2,
            LauncherRocketDamage = 10, LaserCooldownMilliseconds = 1000,
            Templates = [new("Scout", "npc_scout", 1, 1000, 0, 0, 0, 0, 0, 0)]
        };
        var clock = new AdvancingClock();
        var first = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = first.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 1, [])
        }, default);
        await first.SelectAsync(pilot, spawn.Id, default);
        await first.StartLaserAsync(pilot, spawn.Id, default);

        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickLasersAsync(pilot.MapId, sessions, default);
        var partial = await first.LauncherStatusAsync(pilot.UserId, default);
        await first.SetAutoLauncherAsync(pilot.UserId, false, default);
        clock.Advance(TimeSpan.FromSeconds(2));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        var disabled = await second.LauncherStatusAsync(pilot.UserId, default);
        var beforeVolley = (await first.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        await first.SetAutoLauncherAsync(pilot.UserId, true, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickLasersAsync(pilot.MapId, sessions, default);
        var spent = await first.LauncherStatusAsync(pilot.UserId, default);
        var afterVolley = (await first.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);
        await first.TickLasersAsync(pilot.MapId, sessions, default);
        var afterDuplicateTick = (await second.VisibleAsync(pilot.MapId, default)).Single(n => n.Id == spawn.Id);

        Assert.Multiple(() =>
        {
            Assert.That(partial.Loaded, Is.EqualTo(1));
            Assert.That(disabled.Loaded, Is.EqualTo(2));
            Assert.That(beforeVolley.Hp, Is.EqualTo(997));
            Assert.That(spent.Loaded, Is.Zero);
            Assert.That(afterVolley.Hp, Is.EqualTo(976));
            Assert.That(afterDuplicateTick.Hp, Is.EqualTo(afterVolley.Hp));
        });
    }

    [Test]
    public async Task AutoLauncher_PilotToggleIsSharedAndOverridesServerDefault()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = Options.Create(new WorldNpcOptions { AutoLauncherOnLaser = true });
        var clock = new AdvancingClock();
        var first = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);
        var second = new WorldNpcs(cache, mutex, new TestRewards(), options, clock);

        var initial = await first.LauncherStatusAsync(71, default);
        await first.SetAutoLauncherAsync(71, false, default);
        var disabled = await second.LauncherStatusAsync(71, default);
        var otherPilot = await second.LauncherStatusAsync(72, default);
        await second.SetAutoLauncherAsync(71, true, default);
        var enabled = await first.LauncherStatusAsync(71, default);

        Assert.Multiple(() =>
        {
            Assert.That(initial.AutoFire, Is.True);
            Assert.That(disabled.AutoFire, Is.False);
            Assert.That(otherPilot.AutoFire, Is.True);
            Assert.That(enabled.AutoFire, Is.True);
            Assert.That(enabled.Loaded, Is.EqualTo(initial.Loaded));
        });
    }

    [Test]
    public async Task LauncherVolley_ConsumesSharedLoadAndReloadsOneRocketAtATime()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var options = new WorldNpcOptions
        {
            LauncherCapacity = 3, LauncherReloadSeconds = 2, LauncherRocketDamage = 10,
            Templates = [new("Scout", "npc_scout", 1, 1000, 0, 0, 0, 0, 0, 0)]
        };
        var clock = new AdvancingClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var replica = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(options), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        var initialLoad = await replica.LauncherStatusAsync(pilot.UserId, default);
        var initialAttempt = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        clock.Advance(TimeSpan.FromSeconds(6));
        var full = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        var spentLoad = await replica.LauncherStatusAsync(pilot.UserId, default);
        var empty = await replica.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        clock.Advance(TimeSpan.FromSeconds(2));
        var oneLoaded = await npcs.LauncherStatusAsync(pilot.UserId, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        var beforeVolley = await replica.LauncherStatusAsync(pilot.UserId, default);
        var single = await replica.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        var halfway = await npcs.LauncherStatusAsync(pilot.UserId, default);
        clock.Advance(TimeSpan.FromSeconds(5));
        var fullyLoaded = await replica.LauncherStatusAsync(pilot.UserId, default);
        var refilled = await npcs.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);
        options.RocketMissChance = 1;
        clock.Advance(TimeSpan.FromSeconds(2));
        var miss = await replica.AttackAsync(pilot, spawn.Id, sessions, default, launcher: true);

        Assert.Multiple(() =>
        {
            Assert.That(initialLoad, Is.EqualTo(new WorldLauncherStatus(0, 3, 2000)));
            Assert.That(initialAttempt.Status, Is.EqualTo("reloading"));
            Assert.That(spentLoad, Is.EqualTo(new WorldLauncherStatus(0, 3, 2000)));
            Assert.That(oneLoaded, Is.EqualTo(new WorldLauncherStatus(1, 3, 2000)));
            Assert.That(beforeVolley, Is.EqualTo(new WorldLauncherStatus(1, 3, 1000)));
            Assert.That(halfway, Is.EqualTo(new WorldLauncherStatus(0, 3, 1000)));
            Assert.That(fullyLoaded, Is.EqualTo(new WorldLauncherStatus(3, 3, 0)));
            Assert.That(full.Damage, Is.EqualTo(30));
            Assert.That(empty.Status, Is.EqualTo("reloading"));
            Assert.That(single.Damage, Is.EqualTo(10));
            Assert.That(refilled.Damage, Is.EqualTo(30));
            Assert.That(miss.Damage, Is.Zero);
            Assert.That(miss.Target!.Hp, Is.EqualTo(930));
        });
    }

    [Test]
    public async Task NpcPulse_DamagesNearbyTargets_AndSharesCooldownAcrossReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            PulseRange = 1000, PulseCooldownSeconds = 30,
            Templates = [new("Scout", "npc_scout", 2, 100, 0, 5, 0, 0, 0, 0)]
        });
        var inventory = new TestPulseInventory(2);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), options, clock,
            pulseInventory: inventory);
        var replica = new WorldNpcs(cache, mutex, new TestRewards(), options, clock,
            pulseInventory: inventory);
        var npc = npcs.Spawn(1, clock.GetUtcNow()).First();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var actionId = Guid.NewGuid();
        var first = await npcs.PulseAsync(pilot, sessions, default, actionId: actionId);
        Assert.That(first.Status, Is.EqualTo("hit"));
        Assert.That(first.Hits.Select(hit => hit.Damage), Is.EqualTo(new[] { 20 }));
        Assert.That(first.Hits.Single().Target!.Hp, Is.EqualTo(80));
        Assert.That(inventory.Started, Is.EqualTo(1));
        Assert.That(inventory.Completed, Is.EqualTo(1));
        Assert.That((await npcs.VisibleAsync(1, default)).Single(other => other.Id != npc.Id).Hp, Is.EqualTo(100));
        Assert.That((await replica.PulseAsync(pilot, sessions, default)).Status, Is.EqualTo("cooldown"));
        Assert.That(inventory.Remaining, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await replica.PulseAsync(pilot, sessions, default, actionId: actionId)).Status,
            Is.EqualTo("duplicate"));
        Assert.That(inventory.Remaining, Is.EqualTo(1));
        var second = await replica.PulseAsync(pilot, sessions, default);
        Assert.That(second.Hits.Single().Target!.Hp, Is.EqualTo(64));
        Assert.That(inventory.Remaining, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await npcs.PulseAsync(pilot, sessions, default)).Status, Is.EqualTo("out_of_ammo"));
        Assert.That((await npcs.VisibleAsync(1, default)).Single(other => other.Id == npc.Id).Hp,
            Is.EqualTo(64));
        inventory.Refill(1);
        Assert.That((await replica.PulseAsync(pilot, sessions, default)).Hits.Single().Target!.Hp,
            Is.EqualTo(52));
        var station = WorldMapInstance.Starter(1).HomeStation;
        var stationPilot = await sessions.EnterAsync(pilot with
        { UserId = 8, AccountId = Guid.NewGuid(), X = station.X, Y = station.Y }, default);
        Assert.That((await npcs.PulseAsync(stationPilot, sessions, default)).Status, Is.EqualTo("restricted"));
        var portal = WorldMapInstance.Starter(1).Portals.First();
        var portalPilot = await sessions.EnterAsync(pilot with
        { UserId = 9, AccountId = Guid.NewGuid(), X = portal.X, Y = portal.Y }, default);
        Assert.That((await npcs.PulseAsync(portalPilot, sessions, default)).Status, Is.EqualTo("restricted"));
    }

    [Test]
    public async Task NpcPulse_RetryResumesStartedTargets_WithoutAnotherCharge()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            PulseRange = 10000,
            Templates = [new("Scout", "npc_scout", 2, 1, 0, 5, 0, 0, 0, 0)]
        });
        var rewards = new TestRewards { FailFirstCredit = true };
        var inventory = new TestPulseInventory(1);
        var first = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var second = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var spawn = first.Spawn(1, clock.GetUtcNow());
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn[0].X, Y = spawn[0].Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var actionId = Guid.NewGuid();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.PulseAsync(pilot, sessions, default, actionId: actionId));
        Assert.That(inventory.Remaining, Is.Zero);
        var vitalKey = $"world:npc:v2:1:{spawn[0].Id}";
        Assert.That(await cache.GetStringAsync(vitalKey), Does.Contain(actionId.ToString()));
        await second.ReconcileStartedPulsesAsync(pilot.MapId, sessions, default);
        Assert.That(inventory.Started, Is.EqualTo(1));
        Assert.That(inventory.Completed, Is.EqualTo(1));
        Assert.That(rewards.Calls, Is.EqualTo(3));
        Assert.That(await cache.GetStringAsync(vitalKey), Does.Not.Contain(actionId.ToString()));
        Assert.That((await second.PulseAsync(pilot, sessions, default, actionId: actionId)).Status,
            Is.EqualTo("duplicate"));
    }

    [Test]
    public async Task NpcPulse_ReconcilesCompletedActionWithUnclearedEffectId()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 100, 0, 5, 0, 0, 0, 0)]
        });
        var inventory = new TestPulseInventory(0);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), options, clock,
            pulseInventory: inventory);
        var npc = npcs.Spawn(1, clock.GetUtcNow())[0];
        var vitalKey = $"world:npc:v2:1:{npc.Id}";
        var actionId = Guid.NewGuid();
        await cache.SetStringAsync(vitalKey, JsonSerializer.Serialize(new
        {
            Hp = npc.Hp, Shield = npc.Shield, Destroyed = false, Killer = Guid.Empty,
            Generation = 0L, PulseActions = new[] { actionId }
        }));
        inventory.SeedCompleted(Guid.NewGuid(), actionId, 1, [new PulseTarget(npc.Id, 0)]);

        await npcs.ReconcileCompletedPulsesAsync(1, default);

        Assert.That(await cache.GetStringAsync(vitalKey), Does.Not.Contain(actionId.ToString()));
        Assert.That(await inventory.CompletedOnMapAsync(1, default), Is.Empty);
    }

    [Test]
    public async Task NpcPulse_StartedRewardRetryCompletesAfterPilotIsDestroyed()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            PulseRange = 10000,
            Templates = [new("Scout", "npc_scout", 2, 1, 0, 5, 0, 0, 0, 0)]
        });
        var rewards = new TestRewards { FailFirstCredit = true };
        var inventory = new TestPulseInventory(1);
        var first = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var second = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var spawn = first.Spawn(1, clock.GetUtcNow())[0];
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var actionId = Guid.NewGuid();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.PulseAsync(pilot, sessions, default, actionId: actionId));

        await sessions.ApplyNpcDamageAsync(pilot, spawn.Id, 1000, default, shieldAbsorption: 0);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.Zero);
        Assert.That((await second.PulseAsync(pilot, sessions, default)).Status,
            Is.EqualTo("destroyed_pilot"));

        await second.ReconcileStartedPulsesAsync(pilot.MapId, sessions, default);
        Assert.That(inventory.Completed, Is.EqualTo(1));
        Assert.That(inventory.Remaining, Is.Zero);
        Assert.That(rewards.Calls, Is.EqualTo(3));
        Assert.That(await cache.GetStringAsync($"world:npc:v2:1:{spawn.Id}"),
            Does.Not.Contain(actionId.ToString()));
    }

    [Test]
    public async Task NpcPulse_StartedRewardRetryCompletesAfterPilotLeavesMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            PulseRange = 10000,
            Templates = [new("Scout", "npc_scout", 2, 1, 0, 5, 0, 0, 0, 0)]
        });
        var rewards = new TestRewards { FailFirstCredit = true };
        var inventory = new TestPulseInventory(1);
        var first = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var second = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var spawn = first.Spawn(1, clock.GetUtcNow())[0];
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var actionId = Guid.NewGuid();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.PulseAsync(pilot, sessions, default, actionId: actionId));
        await sessions.LeaveAsync(pilot, null, default);
        Assert.That(await sessions.VisiblePilotsAsync(pilot.MapId, default), Is.Empty);

        await second.ReconcileStartedPulsesAsync(pilot.MapId, sessions, default);
        Assert.That(inventory.Completed, Is.EqualTo(1));
        Assert.That(inventory.Remaining, Is.Zero);
        Assert.That(rewards.Calls, Is.EqualTo(3));
        Assert.That(await cache.GetStringAsync($"world:npc:v2:1:{spawn.Id}"),
            Does.Not.Contain(actionId.ToString()));
    }

    [Test]
    public async Task NpcPulse_RetrySkipsTargetThatRespawnedAfterPlanWasStored()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var options = Options.Create(new WorldNpcOptions
        {
            PulseRange = 1, RespawnSeconds = 1,
            Templates = [new("Scout", "npc_scout", 2, 1, 0, 5, 0, 0, 0, 0)]
        });
        var rewards = new TestRewards { FailFirstCredit = true };
        var inventory = new TestPulseInventory(1);
        var first = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var second = new WorldNpcs(cache, mutex, rewards, options, clock, pulseInventory: inventory);
        var spawn = first.Spawn(1, clock.GetUtcNow())[0];
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var actionId = Guid.NewGuid();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.PulseAsync(pilot, sessions, default, actionId: actionId));
        var vitalKey = $"world:npc:v2:1:{spawn.Id}";
        Assert.That(await cache.GetStringAsync(vitalKey), Does.Contain(actionId.ToString()));
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickAggroAsync(1, sessions, default);
        Assert.That(await cache.GetStringAsync(vitalKey), Does.Not.Contain(actionId.ToString()));
        var recovered = (await second.VisibleAsync(1, default)).Single(n => n.Id == spawn.Id);
        Assert.That(recovered.Hp, Is.EqualTo(spawn.Hp));

        var replay = await second.PulseAsync(pilot, sessions, default, actionId: actionId);
        Assert.That(replay.Hits, Is.Empty);
        Assert.That((await second.VisibleAsync(1, default)).Single(n => n.Id == spawn.Id).Hp,
            Is.EqualTo(spawn.Hp));
        Assert.That(inventory.Remaining, Is.Zero);
        Assert.That(inventory.Completed, Is.EqualTo(1));
    }

    [Test]
    public async Task NpcRewardFailure_PreservesKillForKillerRetry_AndRejectsOtherAccount()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var rewards = new TestRewards { FailFirstCredit = true };
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, rewards,
            Options.Create(new WorldNpcOptions { Templates =
                [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] }), clock);
        var spawn = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var killer = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var other = await sessions.EnterAsync((Pilot() with
        {
            UserId = 8, AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }), default);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await npcs.AttackAsync(killer, spawn.Id, sessions, default));
        Assert.Multiple(() =>
        {
            Assert.That(rewards.Calls, Is.EqualTo(1));
            Assert.That(rewards.LastCredited, Is.Not.Null);
        });
        Assert.That(await npcs.VisibleAsync(1, default), Is.Empty);
        Assert.That(await npcs.CanRetryRewardAsync(killer, spawn.Id, default), Is.True);
        Assert.That(await npcs.CanRetryRewardAsync(other, spawn.Id, default), Is.False);
        Assert.That((await npcs.AttackAsync(other, spawn.Id, sessions, default)).Status,
            Is.EqualTo("destroyed"));
        Assert.That(rewards.Calls, Is.EqualTo(1));

        var retry = await npcs.AttackAsync(killer, spawn.Id, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(retry.Status, Is.EqualTo("destroyed"));
            Assert.That(retry.Destroyed, Is.False);
            Assert.That(rewards.Calls, Is.EqualTo(2));
            Assert.That(rewards.LastCredited, Is.EqualTo(spawn));
        });
    }

    [Test]
    public async Task CloakActivation_UsesConfiguredBlockedMaps()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["World:CloakBlockedMaps:0"] = "1" }).Build();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(),
            new TestMutex(), configuration: configuration);
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            X = 10000, Y = 6000,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        Assert.ThrowsAsync<WorldCloakBlockedException>(
            async () => await sessions.SetCloakAsync(pilot, true, default));
        Assert.That(await services.GetRequiredService<IDistributedCache>()
            .GetStringAsync($"world:cloak-cooldown:{pilot.UserId}"), Is.Null);
        var allowed = await sessions.EnterAsync(pilot with { UserId = pilot.UserId + 1, MapId = 2 }, default);
        Assert.That((await sessions.SetCloakAsync(allowed, true, default)).IsInvisible, Is.True);
    }

    [Test]
    public async Task CloakActivation_BlockedMapDoesNotStartCooldown()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(),
            new TestMutex(), cloakBlockedMaps: new HashSet<int> { 1 });
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            X = 10000, Y = 6000,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        Assert.ThrowsAsync<WorldCloakBlockedException>(
            async () => await sessions.SetCloakAsync(pilot, true, default));
        Assert.That((await sessions.ConnectedAsync(1, default)).Single().IsInvisible, Is.False);
        Assert.That(await services.GetRequiredService<IDistributedCache>()
            .GetStringAsync($"world:cloak-cooldown:{pilot.UserId}"), Is.Null);

        var other = await sessions.EnterAsync(pilot with { UserId = pilot.UserId + 1, MapId = 2 }, default);
        other = await sessions.SetCloakAsync(other, true, default);
        Assert.That(other.IsInvisible, Is.True);
    }

    [Test]
    public async Task CloakActivation_FromPreviousMap_DoesNotChangeTransferredPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var oldMap = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var stale = await oldMap.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        await destination.TeleportAsync(stale.UserId, 2, 3000, 1300, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await oldMap.SetCloakAsync(stale, true, default));
        Assert.That(await oldMap.CloakCooldownRemainingMsAsync(stale.UserId, default), Is.Zero);
        Assert.That(await destination.ConnectedAsync(1, default), Is.Empty);
        var current = (await destination.ConnectedAsync(2, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(current.IsInvisible, Is.False);
            Assert.That((current.X, current.Y), Is.EqualTo((3000, 1300)));
        });
    }

    [Test]
    public async Task NpcSelection_FromPreviousMap_DoesNotSelectOnDestination()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var sessions = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(),
            Options.Create(new WorldNpcOptions
            {
                Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 0)]
            }), new TestClock());
        var stale = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var destinationTarget = npcs.Spawn(2, DateTimeOffset.UtcNow).Single();
        var current = await destination.TeleportAsync(stale.UserId, 2,
            destinationTarget.X, destinationTarget.Y, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await npcs.SelectAsync(stale, destinationTarget.Id, default,
                sessions: sessions));
        Assert.That(await cache.GetStringAsync($"world:npc:target:{stale.UserId}"), Is.Null);
        Assert.That(await npcs.SelectAsync(current!, destinationTarget.Id, default,
            sessions: destination), Is.Not.Null);
    }

    [Test]
    public async Task NpcAttack_FromPreviousMap_DoesNotDamageDestinationTarget()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var sessions = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(),
            Options.Create(new WorldNpcOptions
            {
                Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 0)]
            }), new TestClock());
        var stale = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var target = npcs.Spawn(2, DateTimeOffset.UtcNow).Single();
        var current = await destination.TeleportAsync(stale.UserId, 2,
            target.X, target.Y, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await npcs.AttackAsync(stale, target.Id, sessions, default));
        Assert.That((await npcs.VisibleAsync(2, default)).Single().Hp, Is.EqualTo(100));
        Assert.That((await npcs.AttackAsync(current!, target.Id, destination, default)).Status,
            Is.EqualTo("hit"));
    }

    [Test]
    public async Task NpcPulse_FromPreviousMap_DoesNotChargeOrDamageDestinationTargets()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var sessions = new WorldSessionStore(cache, mutex);
        var destination = new WorldSessionStore(cache, mutex);
        var inventory = new TestPulseInventory(1);
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(),
            Options.Create(new WorldNpcOptions
            {
                PulseRange = 1000,
                Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 0)]
            }), new TestClock(), pulseInventory: inventory);
        var stale = await sessions.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        var target = npcs.Spawn(2, DateTimeOffset.UtcNow).Single();
        var current = await destination.TeleportAsync(stale.UserId, 2,
            target.X, target.Y, false, default);

        Assert.ThrowsAsync<WorldMapChangedException>(
            async () => await npcs.PulseAsync(stale, sessions, default));
        Assert.That(inventory.Remaining, Is.EqualTo(1));
        Assert.That(await cache.GetStringAsync($"world:npc:pulse:{stale.UserId}"), Is.Null);
        Assert.That((await npcs.VisibleAsync(2, default)).Single().Hp, Is.EqualTo(100));
        Assert.That((await npcs.PulseAsync(current!, destination, default)).Status,
            Is.EqualTo("hit"));
    }

    [Test]
    public async Task CloakState_IsSharedAcrossWorldInstances_AndSurvivesReentry()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var first = new WorldSessionStore(cache, mutex, clock: clock);
        var second = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await first.EnterAsync(Pilot() with
        {
            X = 10000, Y = 6000,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        pilot = await first.SetCloakAsync(pilot, true, default);
        Assert.That(await second.CloakCooldownRemainingMsAsync(pilot.UserId, default), Is.EqualTo(20_000));
        Assert.That((await second.ConnectedAsync(1, default)).Single().IsInvisible, Is.True);
        Assert.That(second.CurrentHitPoints(pilot), Is.EqualTo(1000));
        Assert.That((await second.EnterAsync(pilot with { IsInvisible = false }, default)).IsInvisible,
            Is.True);

        await second.MarkCombatAsync(pilot, default);
        Assert.That((await first.ConnectedAsync(1, default)).Single().IsInvisible, Is.False);
        var cooldown = Assert.ThrowsAsync<WorldCloakCooldownException>(
            async () => await first.SetCloakAsync(pilot, true, default));
        Assert.That(cooldown!.RemainingMs, Is.EqualTo(20_000));
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.That(await second.CloakCooldownRemainingMsAsync(pilot.UserId, default), Is.EqualTo(13_000));
        clock.Advance(TimeSpan.FromSeconds(13));
        Assert.That(await second.CloakCooldownRemainingMsAsync(pilot.UserId, default), Is.Zero);
        pilot = await first.SetCloakAsync(pilot, true, default);

        pilot = await first.SetCloakAsync(pilot, false, default);
        Assert.That((await second.ConnectedAsync(1, default)).Single().IsInvisible, Is.False);
        Assert.That((await second.EnterAsync(pilot with { IsInvisible = true }, default)).IsInvisible,
            Is.False);
    }

    [Test]
    public async Task CloakActivatedWhileEntryWaitsForMeshLock_IsPublishedOnEntry()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new EntryGateMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        mutex.PauseNextAcquire();
        var entry = second.EnterAsync(pilot, default);
        await mutex.WaitForPausedAcquireAsync();
        try { pilot = await first.SetCloakAsync(pilot, true, default); }
        finally { mutex.ResumeAcquire(); }

        var entered = await entry;
        Assert.That(entered.IsInvisible, Is.True);
        Assert.That((await second.ConnectedAsync(pilot.MapId, default)).Single().IsInvisible, Is.True);
    }

    [Test]
    public async Task CombatAfterConcurrentCloakActivation_LeavesPilotVisible()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new EntryGateMutex();
        var cloakNode = new WorldSessionStore(cache, mutex);
        var combatNode = new WorldSessionStore(cache, mutex);
        var pilot = await cloakNode.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        mutex.PauseNextAcquire();
        var combat = combatNode.MarkCombatAsync(pilot, default);
        await mutex.WaitForPausedAcquireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try { pilot = await cloakNode.SetCloakAsync(pilot, true, default); }
        finally { mutex.ResumeAcquire(); }
        await combat;

        Assert.That((await cloakNode.ConnectedAsync(pilot.MapId, default)).Single().IsInvisible, Is.False);
        Assert.That((await combatNode.RestoreAsync(pilot, default)).IsInvisible, Is.False);
    }

    [Test]
    public async Task CombatDecloak_WithStalePilot_DoesNotRestorePresenceOnPreviousMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var combatNode = new WorldSessionStore(cache, mutex);
        var transferNode = new WorldSessionStore(cache, mutex);
        var stale = await combatNode.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        stale = await combatNode.SetCloakAsync(stale, true, default);

        var transferred = await transferNode.TeleportAsync(stale.UserId, 2, 3000, 1300, false, default);
        Assert.That(transferred?.IsInvisible, Is.True);
        await combatNode.MarkCombatAsync(stale, default);

        Assert.That(await transferNode.ConnectedAsync(1, default), Is.Empty);
        var current = (await transferNode.ConnectedAsync(2, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(current.IsInvisible, Is.False);
            Assert.That((current.X, current.Y), Is.EqualTo((3000, 1300)));
        });
        Assert.That((await combatNode.RestoreAsync(stale, default)).MapId, Is.EqualTo(2));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_CannotKeepPilotOnPreviousMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 1)]
        }), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var pilot = await first.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y
        }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        var transferred = await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        Assert.That(transferred, Is.Not.Null);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);

        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await npcs.TickAggroAsync(1, first, default);
        Assert.That(await cache.GetStringAsync($"world:npc:v2:1:{spawn.Id}"), Does.Contain("\"TargetUserId\":0"));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_WithMissingPosition_CannotAttractNpc()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var clock = new TestClock();
        var npcs = new WorldNpcs(cache, mutex, new TestRewards(), Options.Create(new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 100, 0, 0, 0, 0, 0, 1)]
        }), clock);
        var spawn = npcs.Spawn(1, clock.GetUtcNow()).Single();
        var pilot = await first.EnterAsync(Pilot() with
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y
        }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:position:{pilot.UserId}");

        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await npcs.TickAggroAsync(1, first, default);
        Assert.That(await cache.GetStringAsync($"world:npc:v2:1:{spawn.Id}"), Does.Contain("\"TargetUserId\":0"));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_WithMissingPosition_IsHiddenFromRemoteViews()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:position:{pilot.UserId}");

        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await first.VisiblePilotsAsync(2, default)).Single().UserId,
            Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task StaleLocalSocketAfterTransfer_WithMissingRuntime_IsHiddenFromPreviousMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        using var wire = new MemoryStream();
        first.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");

        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await first.VisiblePilotsAsync(2, default)).Single().UserId,
            Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_WithMissingPositionAndRuntime_UsesMapOwnership()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:position:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");

        Assert.That(await cache.GetStringAsync($"world:map:{pilot.UserId}"), Is.EqualTo("2"));
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await first.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));

        await first.RefreshLocalPresenceAsync(default);
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
    }

    [Test]
    public async Task StaleLocalSocketAfterTransfer_WithMissingPosition_IsRemovedByHeartbeat()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        using var wire = new MemoryStream();
        first.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:position:{pilot.UserId}");

        Assert.That(first.ActivePilotCount, Is.EqualTo(1));
        Assert.That(await cache.GetStringAsync($"world:runtime:{pilot.UserId}"), Does.Contain("\"MapId\":2"));
        await first.RefreshLocalPresenceAsync(default);

        Assert.That(first.ActivePilotCount, Is.Zero);
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
        Assert.That((await second.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task StaleLocalSocketAfterTransfer_WithInvalidPosition_UsesRuntimeMapForHeartbeat()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        using var wire = new MemoryStream();
        first.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", "{invalid");
        await cache.RemoveAsync($"world:map:{pilot.UserId}");

        Assert.That(await cache.GetStringAsync($"world:runtime:{pilot.UserId}"), Does.Contain("\"MapId\":2"));
        await first.RefreshLocalPresenceAsync(default);

        Assert.That(first.ActivePilotCount, Is.Zero);
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
        Assert.That((await second.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_WithInvalidPosition_UsesRuntimeMapForObservers()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", "{invalid");
        await cache.RemoveAsync($"world:map:{pilot.UserId}");

        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await second.VisiblePilotsAsync(2, default)).Single().UserId,
            Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task StalePresenceAfterTransfer_WithRestoredOldPositionAndMissingMapOwner_UsesRuntimeMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        using var wire = new MemoryStream();
        first.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.RemoveAsync($"world:map:{pilot.UserId}");

        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        await first.RefreshLocalPresenceAsync(default);
        Assert.That(first.ActivePilotCount, Is.Zero);
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
    }

    [Test]
    public async Task SavedMapAfterTransfer_UsesMapOwnerAndRuntimeAheadOfRestoredOldPosition()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);

        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        var currentRuntime = await cache.GetStringAsync($"world:runtime:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        await cache.SetStringAsync($"world:runtime:{pilot.UserId}", currentRuntime!);
        await cache.RemoveAsync($"world:map:{pilot.UserId}");
        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");
        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(1));
    }

    [Test]
    public async Task StaleLocalSocketAfterTransfer_WithRestoredOldPosition_IsRemovedByHeartbeat()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        using var wire = new MemoryStream();
        first.Attach(new WorldConnection(pilot, new WorldPacketWriter(wire)));
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);

        Assert.That(await cache.GetStringAsync($"world:map:{pilot.UserId}"), Is.EqualTo("2"));
        Assert.That(first.ActivePilotCount, Is.EqualTo(1));
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await second.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await second.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await first.RefreshLocalPresenceAsync(default);

        Assert.That(first.ActivePilotCount, Is.Zero);
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CurrentMapSocket_WithRestoredOldPosition_RemainsPresentOnHeartbeat(bool removeMapOwner)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        var destination = await second.EnterAsync(pilot with { MapId = 2 }, default);
        Assert.That(destination.MapId, Is.EqualTo(2));
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);
        if (removeMapOwner) await cache.RemoveAsync($"world:map:{pilot.UserId}");

        await second.RefreshLocalPresenceAsync(default);

        Assert.That(second.ActivePilotCount, Is.EqualTo(1));
        Assert.That((await second.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await second.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task InvalidMapOwner_FallsBackToRuntimeForHeartbeatAndMapReaders()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await second.EnterAsync(pilot with { MapId = 2 }, default);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);
        await cache.SetStringAsync($"world:map:{pilot.UserId}", "999");

        Assert.That(await second.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await second.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await second.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));

        await first.RefreshLocalPresenceAsync(default);
        await second.RefreshLocalPresenceAsync(default);
        Assert.That(first.ActivePilotCount, Is.Zero);
        Assert.That(second.ActivePilotCount, Is.EqualTo(1));
        Assert.That(await cache.GetStringAsync("world:presence:map:1"), Is.EqualTo("{}"));
    }

    [Test]
    public async Task InvalidRuntimeMap_FallsBackToSavedPositionForHeartbeatAndMapReaders()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await second.EnterAsync(pilot with { MapId = 2 }, default);
        var runtimeKey = $"world:runtime:{pilot.UserId}";
        var runtime = await cache.GetStringAsync(runtimeKey);
        Assert.That(runtime, Is.Not.Null);
        await cache.SetStringAsync(runtimeKey, runtime!.Replace("\"MapId\":2", "\"MapId\":999"));
        await cache.RemoveAsync($"world:map:{pilot.UserId}");

        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await first.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await second.RefreshLocalPresenceAsync(default);
        Assert.That(second.ActivePilotCount, Is.EqualTo(1));
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task InvalidSavedPositionMap_DoesNotRemoveCurrentMapPresence()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var positionKey = $"world:position:{pilot.UserId}";
        var position = await cache.GetStringAsync(positionKey);
        Assert.That(position, Is.Not.Null);
        await cache.SetStringAsync(positionKey, position!.Replace("\"MapId\":1", "\"MapId\":999"));
        await cache.RemoveAsync($"world:map:{pilot.UserId}");
        await cache.RemoveAsync($"world:runtime:{pilot.UserId}");

        Assert.That(await second.SavedMapAsync(pilot.UserId, default), Is.Null);
        Assert.That((await second.ConnectedAsync(1, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await second.VisiblePilotsAsync(1, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await first.RefreshLocalPresenceAsync(default);
        Assert.That(first.ActivePilotCount, Is.EqualTo(1));
        Assert.That((await second.ConnectedAsync(1, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task CurrentMapOwner_OutranksStaleRuntimeWhenSavedPositionIsMissing()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldRoster = await cache.GetStringAsync("world:presence:map:1");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await second.EnterAsync(pilot with { MapId = 2 }, default);
        var runtimeKey = $"world:runtime:{pilot.UserId}";
        var runtime = await cache.GetStringAsync(runtimeKey);
        Assert.That(runtime, Is.Not.Null);
        await cache.SetStringAsync(runtimeKey, runtime!.Replace("\"MapId\":2", "\"MapId\":1"));
        await cache.RemoveAsync($"world:position:{pilot.UserId}");
        await cache.SetStringAsync("world:presence:map:1", oldRoster!);

        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await first.VisiblePilotsAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        Assert.That((await first.VisiblePilotsAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
        await second.RefreshLocalPresenceAsync(default);
        Assert.That(second.ActivePilotCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EntryAfterTransfer_DoesNotRestoreOldMapFromStalePosition(bool removeMapOwner)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var pilot = await first.EnterAsync(Pilot() with { AccountId = Guid.NewGuid() }, default);
        var oldPosition = await cache.GetStringAsync($"world:position:{pilot.UserId}");

        await second.TeleportAsync(pilot.UserId, 2, 3000, 1300, false, default);
        await cache.SetStringAsync($"world:position:{pilot.UserId}", oldPosition!);
        if (removeMapOwner) await cache.RemoveAsync($"world:map:{pilot.UserId}");

        var restored = await second.RestoreAsync(pilot with { MapId = 2, X = 3000, Y = 1300 }, default);
        Assert.That(restored.MapId, Is.EqualTo(2));
        Assert.That((restored.X, restored.Y), Is.EqualTo((3000, 1300)));

        var entered = await second.EnterAsync(restored, default);
        Assert.That(entered.MapId, Is.EqualTo(2));
        Assert.That(await first.SavedMapAsync(pilot.UserId, default), Is.EqualTo(2));
        Assert.That(await first.ConnectedAsync(1, default), Is.Empty);
        Assert.That((await first.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(pilot.UserId));
    }

    [Test]
    public async Task CloakCooldown_SurvivesCooldownKeyEvictionAcrossWorldInstances()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new TestMutex();
        var clock = new AdvancingClock();
        var first = new WorldSessionStore(cache, mutex, clock: clock);
        var second = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await first.EnterAsync(Pilot() with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);

        pilot = await first.SetCloakAsync(pilot, true, default);
        await cache.RemoveAsync($"world:cloak-cooldown:{pilot.UserId}");
        await second.MarkCombatAsync(pilot, default);
        await first.MoveAsync(pilot, 3000, 1300, null, default);
        Assert.That(await second.CloakCooldownRemainingMsAsync(pilot.UserId, default), Is.EqualTo(20_000));
        Assert.ThrowsAsync<WorldCloakCooldownException>(
            async () => await second.SetCloakAsync(pilot, true, default));

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.That(await second.CloakCooldownRemainingMsAsync(pilot.UserId, default), Is.Zero);
        Assert.That((await second.SetCloakAsync(pilot, true, default)).IsInvisible, Is.True);
    }

    [Test]
    public async Task CloakTransitions_NotifyConnectedPilotsOncePerChange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new WorldSessionStore(services.GetRequiredService<IDistributedCache>(), new TestMutex());
        var pilot = await sessions.EnterAsync(Pilot() with
        {
            X = 10000, Y = 6000,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        }, default);
        using var ownWire = new MemoryStream();
        using var observerWire = new MemoryStream();
        sessions.Attach(new WorldConnection(pilot, new WorldPacketWriter(ownWire)));
        sessions.Attach(new WorldConnection(pilot with { UserId = pilot.UserId + 1 },
            new WorldPacketWriter(observerWire)));

        pilot = await sessions.SetCloakAsync(pilot, true, default);
        pilot = await sessions.SetCloakAsync(pilot, true, default);
        await sessions.MarkCombatAsync(pilot, default);
        await sessions.MarkCombatAsync(pilot, default);

        foreach (var wire in new[] { ownWire, observerWire })
        {
            wire.Position = 0;
            var reader = new WorldPacketReader(wire);
            var first = await reader.ReadAsync();
            var second = await reader.ReadAsync();
            Assert.Multiple(() =>
            {
                Assert.That(first?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
                Assert.That(second?.Command, Is.EqualTo(WorldCommandId.CloakStateCommand));
            });
            Assert.That(WorldPayload.Decode(first!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, true)));
            Assert.That(WorldPayload.Decode(second!.Value.Payload, r =>
                (WorldPayload.ReadInt64(r), r.ReadBoolean())), Is.EqualTo((pilot.UserId, false)));
            Assert.That(await reader.ReadAsync(), Is.Null);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_000_000);
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    private sealed class BlockingReadStream(byte[] bytes, int bytesBeforeBlock) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position >= bytesBeforeBlock)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await base.ReadAsync(buffer[..1], cancellationToken);
        }
    }

    private sealed class BlockingFirstWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }

        public void ReleaseFirstWrite() => release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls == 1)
            {
                FirstWriteStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailFirstWriteStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls == 1) throw new IOException("Simulated socket write failure.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class TestPulseInventory(int remaining) : IWorldPulseInventory
    {
        public int Remaining { get; private set; } = remaining;
        public int Started { get; private set; }
        public int Completed { get; private set; }
        private readonly HashSet<Guid> actions = [];
        private readonly Dictionary<Guid, PulseActionPlan> plans = [];
        private readonly Dictionary<Guid, Guid> owners = [];

        public void Refill(int amount) => Remaining += amount;

        public void SeedCompleted(Guid accountId, Guid actionId, int mapId, PulseTarget[] targets)
        {
            owners[actionId] = accountId;
            plans[actionId] = new("completed", mapId, targets);
        }

        public Task<PulseChargeResult> ConsumeAsync(Guid accountId, Guid actionId, CancellationToken ct)
        {
            if (actions.Contains(actionId)) return Task.FromResult(PulseChargeResult.Duplicate);
            if (Remaining == 0) return Task.FromResult(PulseChargeResult.Unavailable);
            Remaining--;
            actions.Add(actionId);
            owners[actionId] = accountId;
            return Task.FromResult(PulseChargeResult.Consumed);
        }

        public Task<bool> StartAsync(Guid accountId, Guid actionId, int mapId, PulseTarget[] targets,
            CancellationToken ct)
        {
            Started++;
            plans[actionId] = new("started", mapId, targets);
            return Task.FromResult(actions.Contains(actionId));
        }

        public Task<PulseActionPlan?> PlanAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
            Task.FromResult(plans.GetValueOrDefault(actionId));

        public Task<IReadOnlyList<StartedPulseAction>> StartedOnMapAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StartedPulseAction>>(plans.Where(entry =>
                entry.Value.Status == "started" && entry.Value.MapId == mapId)
                .Select(entry => new StartedPulseAction(owners[entry.Key], entry.Key)).ToArray());

        public Task<IReadOnlyList<StartedPulseAction>> CompletedOnMapAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StartedPulseAction>>(plans.Where(entry =>
                entry.Value.Status == "completed" && entry.Value.MapId == mapId)
                .Select(entry => new StartedPulseAction(owners[entry.Key], entry.Key)).ToArray());

        public Task CompleteAsync(Guid accountId, Guid actionId, CancellationToken ct)
        {
            Completed++;
            plans[actionId] = plans[actionId] with { Status = "completed" };
            return Task.CompletedTask;
        }

        public Task SettleAsync(Guid accountId, Guid actionId, CancellationToken ct)
        {
            plans[actionId] = plans[actionId] with { Status = "settled" };
            return Task.CompletedTask;
        }

        public Task<bool> RefundUnstartedAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class TestRewards : IWorldNpcRewards
    {
        public int Calls { get; private set; }
        public bool FailFirstCredit { get; init; }
        public WorldNpc? LastCredited { get; private set; }
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        {
            Calls++;
            LastCredited = npc;
            if (FailFirstCredit && Calls == 1)
                throw new InvalidOperationException("Simulated uncertain reward outcome.");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCollectibleWallet : IWorldRewardWallet
    {
        public bool ThrowOnCredit { get; init; } = true;
        public long CargoUsed { get; set; }
        public int CreditCalls { get; private set; }

        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        {
            CreditCalls++;
            if (ThrowOnCredit) throw new InvalidOperationException("Simulated uncertain ledger outcome.");
            return Task.FromResult(true);
        }

        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(CargoUsed);
    }

    private sealed class TestRepairWallet : IWorldRepairWallet
    {
        public bool CanPay { get; set; }
        public bool ThrowAfterChargeOnce { get; set; }
        public bool ThrowAfterStageOnce { get; set; }
        public bool ThrowAfterCompleteOnce { get; set; }
        public bool ThrowAfterRecordOnce { get; set; }
        public List<string> Keys { get; } = [];
        private readonly Dictionary<string, int> prices = [];
        private readonly Dictionary<Guid, string> deaths = [];
        private readonly Dictionary<Guid, WorldPosition> deathPositions = [];
        private readonly HashSet<Guid> pendingDeaths = [];
        private readonly Dictionary<Guid, WorldRepairOutcome> outcomes = [];
        private readonly Dictionary<(Guid, WorldRespawnLocation), DateTimeOffset> lastRepairs = [];
        public Task RecordDeathAsync(Guid accountId, string deathId, WorldPosition position, CancellationToken ct)
        {
            deaths[accountId] = deathId;
            deathPositions[accountId] = position;
            pendingDeaths.Add(accountId);
            outcomes.Remove(accountId);
            if (ThrowAfterRecordOnce)
            {
                ThrowAfterRecordOnce = false;
                throw new InvalidOperationException("Interrupted after durable death recording.");
            }
            return Task.CompletedTask;
        }
        public Task<string?> FindDeathAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(deaths.GetValueOrDefault(accountId));
        public Task<string?> FindPendingDeathAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(pendingDeaths.Contains(accountId) ? deaths.GetValueOrDefault(accountId) : null);
        public Task<WorldPosition?> FindPendingDeathPositionAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(pendingDeaths.Contains(accountId) ? deathPositions.GetValueOrDefault(accountId) : null);
        public Task<WorldRepairOutcome?> FindPendingRepairAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(pendingDeaths.Contains(accountId) ? outcomes.GetValueOrDefault(accountId) : null);
        public Task<DateTimeOffset?> FindLastRepairAsync(Guid accountId, WorldRespawnLocation location, CancellationToken ct) =>
            Task.FromResult(lastRepairs.TryGetValue((accountId, location), out var at)
                ? (DateTimeOffset?)at : null);
        public Task StageRepairAsync(Guid accountId, string deathId, WorldRepairOutcome outcome,
            WorldRespawnLocation location, DateTimeOffset at, CancellationToken ct)
        {
            if (!pendingDeaths.Contains(accountId) || deaths.GetValueOrDefault(accountId) != deathId)
                throw new InvalidOperationException("The destroyed life changed.");
            if (!outcomes.ContainsKey(accountId) && location is not WorldRespawnLocation.Base)
                lastRepairs[(accountId, location)] = at;
            outcomes[accountId] = outcome;
            if (ThrowAfterStageOnce)
            {
                ThrowAfterStageOnce = false;
                throw new InvalidOperationException("Interrupted after durable repair staging.");
            }
            return Task.CompletedTask;
        }
        public Task CompleteDeathAsync(Guid accountId, string deathId, CancellationToken ct)
        {
            if (ThrowAfterCompleteOnce)
            {
                ThrowAfterCompleteOnce = false;
                throw new InvalidOperationException("Interrupted before durable repair completion.");
            }
            if (deaths.GetValueOrDefault(accountId) == deathId) pendingDeaths.Remove(accountId);
            return Task.CompletedTask;
        }
        public Task<WorldRepairPayment> ChargeAsync(Guid accountId, WorldRespawnLocation location,
            string deathId, CancellationToken ct)
        {
            if (!CanPay) return Task.FromResult(new WorldRepairPayment(WorldRepairCharge.InsufficientFunds, 0));
            if (Keys.Contains(deathId)) return Task.FromResult(new WorldRepairPayment(
                prices[deathId] == WorldRepairWallet.Price(location)
                    ? WorldRepairCharge.AlreadyCharged : WorldRepairCharge.InsufficientFunds,
                1000 - prices[deathId]));
            Keys.Add(deathId);
            prices[deathId] = WorldRepairWallet.Price(location);
            if (ThrowAfterChargeOnce)
            {
                ThrowAfterChargeOnce = false;
                throw new InvalidOperationException("Interrupted after durable repair charge.");
            }
            return Task.FromResult(new WorldRepairPayment(WorldRepairCharge.Applied, 1000 - WorldRepairWallet.Price(location)));
        }

        public Task<WorldRepairPayment?> FindChargeAsync(Guid accountId, WorldRespawnLocation location,
            string deathId, CancellationToken ct) => Task.FromResult<WorldRepairPayment?>(
                prices.TryGetValue(deathId, out var price)
                    && price == WorldRepairWallet.Price(location)
                    ? new(WorldRepairCharge.AlreadyCharged, 1000 - price) : null);
    }

    private sealed class TestMutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Releaser(gate); }
        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }

    private sealed class EntryGateMutex : IServerMeshMutex
    {
        private readonly TestMutex inner = new();
        private int armed;
        private TaskCompletionSource? paused;
        private TaskCompletionSource? resumed;

        public void PauseNextAcquire()
        {
            paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            armed = 1;
        }

        public Task WaitForPausedAcquireAsync() => paused!.Task;
        public void ResumeAcquire() => resumed!.SetResult();

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                paused!.SetResult();
                await resumed!.Task.WaitAsync(ct);
            }
            return await inner.AcquireAsync(ct);
        }
    }

    private static WorldPilot Pilot() => new(7, "mmo", 1, 1, "ship_pusat-plus", 0, 0);
}
