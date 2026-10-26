using System.Buffers.Binary;
using System.Text;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldNpcPacketSpecificationTests
{
    private static WorldNpc Npc(int id = 7, string kind = "Streuner", string shipId = "npc_streuner",
        int x = 100, int y = -200, int hp = 800, int maxHp = 900, int shield = 200, int maxShield = 250) =>
        new(id, kind, shipId, x, y, hp, maxHp, shield, maxShield, 400, 1, 400, 2);

    [Test]
    public void Create_EmitsNineFieldsInWireOrder_AndConsumesThePayloadExactly()
    {
        var payload = WorldNpcPackets.Create(Npc());
        var decoded = WorldPayload.Decode(payload, r => (
            WorldPayload.ReadInt32(r), WorldPayload.ReadString(r), WorldPayload.ReadString(r),
            WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
            WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r)));
        Assert.That(decoded, Is.EqualTo((7, "Streuner", "npc_streuner", 100, -200, 800, 900, 200, 250)));
    }

    [Test]
    public void Create_HasExactByteLength_AndOmitsRewardFields()
    {
        var payload = WorldNpcPackets.Create(Npc());
        var expected = 4 + 2 + "Streuner".Length + 2 + "npc_streuner".Length + 6 * 4;
        Assert.Multiple(() =>
        {
            Assert.That(payload, Has.Length.EqualTo(expected));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(payload), Is.EqualTo(7));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4)), Is.EqualTo((ushort)8));
        });
    }

    [Test]
    public void Create_IgnoresRewardValues()
    {
        var npc = Npc();
        var richer = npc with { Credits = int.MaxValue, Uridium = -1, Experience = 0, Honor = int.MinValue };
        Assert.That(WorldNpcPackets.Create(richer), Is.EqualTo(WorldNpcPackets.Create(npc)));
    }

    [Test]
    public void Health_IsExactlyTwentyBytes_InIdHpMaxHpShieldMaxShieldOrder()
    {
        var payload = WorldNpcPackets.Health(Npc());
        var decoded = WorldPayload.Decode(payload, r => (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
            WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r)));
        Assert.Multiple(() =>
        {
            Assert.That(payload, Has.Length.EqualTo(20));
            Assert.That(decoded, Is.EqualTo((7, 800, 900, 200, 250)));
        });
    }

    [Test]
    public void Health_IsTheTrailingTwentyBytesOfCreateAfterIdentityAndPosition()
    {
        var npc = Npc();
        var create = WorldNpcPackets.Create(npc);
        var health = WorldNpcPackets.Health(npc);
        Assert.Multiple(() =>
        {
            Assert.That(health.AsSpan(0, 4).ToArray(), Is.EqualTo(create.AsSpan(0, 4).ToArray()));
            Assert.That(health.AsSpan(4).ToArray(), Is.EqualTo(create.AsSpan(create.Length - 16).ToArray()));
        });
    }

    [Test]
    public void Health_DoesNotEncodeKindOrShipId()
    {
        var npc = Npc(kind: "\uD800", shipId: new string('x', 2000));
        Assert.That(WorldNpcPackets.Health(npc), Is.EqualTo(WorldNpcPackets.Health(Npc())));
    }

    [TestCase(int.MinValue)]
    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(int.MaxValue)]
    public void BothEmitters_PreserveSignedInt32BoundsWithoutClamping(int value)
    {
        var npc = Npc(id: value, x: value, y: value, hp: value, maxHp: value, shield: value, maxShield: value);
        var create = WorldPayload.Decode(WorldNpcPackets.Create(npc), r =>
        {
            var values = new List<int> { WorldPayload.ReadInt32(r) };
            WorldPayload.ReadString(r);
            WorldPayload.ReadString(r);
            for (var i = 0; i < 6; i++) values.Add(WorldPayload.ReadInt32(r));
            return values;
        });
        var health = WorldPayload.Decode(WorldNpcPackets.Health(npc), r =>
            Enumerable.Range(0, 5).Select(_ => WorldPayload.ReadInt32(r)).ToList());
        Assert.Multiple(() =>
        {
            Assert.That(create, Has.Count.EqualTo(7).And.All.EqualTo(value));
            Assert.That(health, Has.All.EqualTo(value));
        });
    }

    [Test]
    public void Health_DoesNotValidateVitals_HpAboveMaxAndNegativeShieldAreEmittedAsGiven()
    {
        var payload = WorldNpcPackets.Health(Npc(hp: 5000, maxHp: 10, shield: -3, maxShield: 0));
        Assert.That(WorldPayload.Decode(payload, r => (WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r),
            WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
            Is.EqualTo((7, 5000, 10, -3, 0)));
    }

    [Test]
    public void Create_CountsKindAndShipIdInUtf8Bytes()
    {
        var payload = WorldNpcPackets.Create(Npc(kind: "Ä", shipId: "\U0001F680"));
        Assert.Multiple(() =>
        {
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4)), Is.EqualTo((ushort)2));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(8)), Is.EqualTo((ushort)4));
            Assert.That(payload, Has.Length.EqualTo(4 + 2 + 2 + 2 + 4 + 6 * 4));
        });
    }

    [Test]
    public void Create_EncodesEmptyStringsAsZeroLengthPrefixes()
    {
        var payload = WorldNpcPackets.Create(Npc(kind: "", shipId: ""));
        Assert.Multiple(() =>
        {
            Assert.That(payload.AsSpan(4, 4).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
            Assert.That(payload, Has.Length.EqualTo(4 + 4 + 6 * 4));
        });
    }

    [TestCase(1024, true)]
    [TestCase(1025, false)]
    public void Create_AppliesOrdinaryStringLimitToShipId(int length, bool accepted)
    {
        var npc = Npc(shipId: new string('s', length));
        if (accepted)
            Assert.That(WorldNpcPackets.Create(npc), Has.Length.EqualTo(4 + 2 + 8 + 2 + length + 6 * 4));
        else
            Assert.Throws<InvalidDataException>(() => WorldNpcPackets.Create(npc));
    }

    [Test]
    public void Create_RejectsKindAboveLimitCountedInBytes()
    {
        var kind = new string('é', 513);
        Assert.That(Encoding.UTF8.GetByteCount(kind), Is.EqualTo(1026));
        Assert.Throws<InvalidDataException>(() => WorldNpcPackets.Create(Npc(kind: kind)));
    }

    // Lone surrogates cannot survive attribute metadata, so the cases select where to inject one.
    [TestCase(true)]
    [TestCase(false)]
    public void Create_RejectsUnpairedSurrogatesWithoutReturningAPartialPayload(bool inKind)
    {
        var npc = inKind ? Npc(kind: "\uD800") : Npc(shipId: "npc_\uDC00");
        Assert.Throws<EncoderFallbackException>(() => WorldNpcPackets.Create(npc));
    }

    [Test]
    public void Emitters_AreDeterministic_AndReturnFreshArrays()
    {
        var npc = Npc();
        var first = WorldNpcPackets.Create(npc);
        var second = WorldNpcPackets.Create(npc);
        var health = WorldNpcPackets.Health(npc);
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(WorldNpcPackets.Health(npc), Is.EqualTo(health).And.Not.SameAs(health));
        });
        first[0] = 0xFF;
        Assert.That(WorldNpcPackets.Create(npc)[0], Is.EqualTo(0));
    }

    [Test]
    public async Task CreateAndHealth_FrameUnderShipCreateAndNpcHealthCommandIds()
    {
        using var stream = new MemoryStream();
        var writer = new WorldPacketWriter(stream);
        await writer.WriteAsync(WorldCommandId.ShipCreateCommand, WorldNpcPackets.Create(Npc()));
        await writer.WriteAsync(WorldCommandId.NpcHealthCommand, WorldNpcPackets.Health(Npc()));
        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        var create = await reader.ReadAsync();
        var health = await reader.ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That((ushort)WorldCommandId.ShipCreateCommand, Is.EqualTo(24));
            Assert.That((ushort)WorldCommandId.NpcHealthCommand, Is.EqualTo(37));
            Assert.That(create?.Command, Is.EqualTo(WorldCommandId.ShipCreateCommand));
            Assert.That(create!.Value.Payload.ToArray(), Is.EqualTo(WorldNpcPackets.Create(Npc())));
            Assert.That(health?.Command, Is.EqualTo(WorldCommandId.NpcHealthCommand));
            Assert.That(health!.Value.Payload.ToArray(), Is.EqualTo(WorldNpcPackets.Health(Npc())));
        });
    }
}
