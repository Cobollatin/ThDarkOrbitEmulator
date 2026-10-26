using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldEntryPacketSpecificationTests
{
    private static WorldPilot Pilot() => new(71, "mmo", 5, 2, "ship_phoenix", 900, 17)
    {
        UserName = "Pilot", FactionId = 2, X = 700, Y = 800,
        Experience = 1234, Honor = -5, Level = 9,
        Stats = new WorldShipStats(320, 4000, 6000, 100, 150,
            [new LocalEquipmentAssignment(2, 1, "laser_lf3", 2)])
    };

    private static async Task<WorldPacket[]> Entry(WorldPilot pilot, WorldShipStats? stats = null,
        long? shield = null, (int X, int Y)? position = null, long? hp = null, WorldMapInstance? map = null)
    {
        using var stream = new MemoryStream();
        await WorldEntryPackets.SendAsync(new WorldPacketWriter(stream), pilot, default,
            stats, shield, position, hp, map);
        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var packets = new List<WorldPacket>();
        while (await reader.ReadAsync() is { } packet) packets.Add(packet);
        return [.. packets];
    }

    [Test]
    public async Task CustomRuntimeMap_ControlsGeometryAndEveryGateBeforeHero()
    {
        var map = new WorldMapInstance(5, 3, 321, 654,
            new WorldStation(88, 10, 20, 30),
            [new WorldPortal(91, 1, 2, 6, 7, 8), new WorldPortal(92, 3, 4, 7, 9, 10)]);
        var packets = await Entry(Pilot(), map: map);
        Assert.That(packets.Select(p => p.Command), Is.EqualTo(new[]
        {
            WorldCommandId.MapInitializationCommand, WorldCommandId.SpaceStationCommand,
            WorldCommandId.JumpgateCreateCommand, WorldCommandId.JumpgateCreateCommand,
            WorldCommandId.SafeZoneCommand, WorldCommandId.HeroInitCommand
        }));
        static (int, int, int, int) Fields(WorldPacket packet) => WorldPayload.Decode(packet.Payload,
            r => (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r)));
        Assert.Multiple(() =>
        {
            Assert.That(Fields(packets[0]), Is.EqualTo((5, 321, 654, 3)));
            Assert.That(Fields(packets[1]), Is.EqualTo((88, 10, 20, 30)));
            Assert.That(Fields(packets[2]), Is.EqualTo((91, 1, 2, 6)));
            Assert.That(Fields(packets[3]), Is.EqualTo((92, 3, 4, 7)));
        });
    }

    [TestCase(40, 20, 1)]
    [TestCase(41, 20, 0)]
    [TestCase(28, 44, 1)]
    [TestCase(29, 44, 0)]
    public async Task SafeZone_UsesInclusiveSquaredRadiusAtEffectivePosition(int x, int y, byte expected)
    {
        var map = new WorldMapInstance(5, 2, 100, 100,
            new WorldStation(500, 10, 20, 30), []);
        var packets = await Entry(Pilot(), position: (x, y), map: map);
        Assert.That(packets.Select(p => p.Command), Is.EqualTo(new[]
        {
            WorldCommandId.MapInitializationCommand, WorldCommandId.SpaceStationCommand,
            WorldCommandId.SafeZoneCommand, WorldCommandId.HeroInitCommand
        }));
        Assert.That(packets[2].Payload.ToArray(), Is.EqualTo(new[] { expected }));
    }

    [TestCase(int.MinValue, int.MaxValue, 0, 0, 30, (byte)0)]
    [TestCase(int.MaxValue, int.MinValue, 0, 0, 30, (byte)0)]
    [TestCase(int.MaxValue, 0, 0, 0, int.MaxValue, (byte)1)]
    [TestCase(int.MaxValue, 0, int.MaxValue, 0, int.MaxValue, (byte)0)]
    public async Task SafeZone_ExtremeCoordinatesDoNotWrapBeforeDistanceComparison(
        int x, int stationX, int y, int stationY, int radius, byte expected)
    {
        var map = new WorldMapInstance(5, 2, 100, 100,
            new WorldStation(500, stationX, stationY, radius), []);
        var packets = await Entry(Pilot(), position: (x, y), map: map);
        Assert.That(packets[2].Payload.ToArray(), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public async Task Hero_EncodesEveryFieldInWireOrderAndUsesProvidedVitalsAndPosition()
    {
        var stats = new WorldShipStats(501, 8000, 12000, 400, 321,
            [new LocalEquipmentAssignment(2, 2, "shield_sg3n_a03", 3)]);
        var packets = await Entry(Pilot(), stats, shield: 42, position: (11, -12), hp: 43);
        using var stream = new MemoryStream(packets[^1].Payload.ToArray());
        using var r = new BinaryReader(stream);
        Assert.Multiple(() =>
        {
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(71));
            Assert.That(WorldPayload.ReadString(r), Is.EqualTo("Pilot"));
            Assert.That(WorldPayload.ReadString(r), Is.EqualTo("ship_phoenix"));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(2));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(5));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(11));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(-12));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(501));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(43));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(8000));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(42));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(12000));
            Assert.That(WorldPayload.ReadInt32(r), Is.Zero);
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(400));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(321));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(1234));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(-5));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(9));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(900));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(17));
            Assert.That(r.ReadByte(), Is.EqualTo(2));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(1));
            Assert.That(WorldPayload.ReadString(r), Is.EqualTo("shield_sg3n_a03"));
            Assert.That(WorldPayload.ReadInt32(r), Is.EqualTo(3));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        });
    }

    [Test]
    public async Task NullOverrides_DefaultVitalsAndMap_AndDoNotMutatePilot()
    {
        var pilot = Pilot();
        var packets = await Entry(pilot);
        Assert.That(WorldPayload.Decode(packets[0].Payload, r =>
            (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
                WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
            Is.EqualTo((5, 21000, 13100, 2)));
        using var stream = new MemoryStream(packets[^1].Payload.ToArray());
        using var r = new BinaryReader(stream);
        WorldPayload.ReadInt64(r);
        WorldPayload.ReadString(r);
        WorldPayload.ReadString(r);
        for (var i = 0; i < 5; i++) WorldPayload.ReadInt32(r);
        Assert.Multiple(() =>
        {
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(4000));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(4000));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(6000));
            Assert.That(WorldPayload.ReadInt64(r), Is.EqualTo(6000));
            Assert.That((pilot.X, pilot.Y, pilot.Stats.MaxHitPoints), Is.EqualTo((700, 800, 4000L)));
        });
    }
}
