using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class UnityWorldProtocolSpecificationTests
{
    private const string Dosid = "567f2237234e4c4b9093f6d080bf3d35";

    // Captured from DarkOrbit Unity 1.1.93 after the version handshake.
    private static readonly byte[] CapturedLoginFrame = Convert.FromHexString(
        "000031" + "00072faf080000000020" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(Dosid)) +
        "00000057600001");

    [Test]
    public async Task FrameReader_ParsesCapturedThreeByteLoginFrame()
    {
        var packet = await new UnityWorldFrameReader(new MemoryStream(CapturedLoginFrame)).ReadAsync();

        Assert.That(packet, Is.Not.Null);
        Assert.That((ushort)packet!.Value.Command, Is.EqualTo(UnityWorldModuleId.LoginRequest));
        var login = UnityLoginRequest.Read(packet.Value.Payload);
        Assert.That(login.UserId, Is.EqualTo(100000000));
        Assert.That(login.FactionId, Is.EqualTo((short)0));
        Assert.That(login.SessionId, Is.EqualTo(Dosid));
        Assert.That(login.Version, Is.EqualTo(string.Empty));
        Assert.That(login.InstanceId, Is.EqualTo(1398));
        Assert.That(login.IsMiniClient, Is.True);
    }

    [Test]
    public async Task FrameWriter_UsesThreeByteLengthAndVersionModuleLayout()
    {
        using var stream = new MemoryStream();
        await new UnityWorldFrameWriter(stream).WriteAsync(UnityWorldModules.Version("ab", true));

        Assert.That(Convert.ToHexString(stream.ToArray()), Is.EqualTo("000007" + "029B" + "0002" + "6162" + "01"));
    }

    [Test]
    public void RotatedInts_RoundTripThroughClientRotation()
    {
        var bytes = new UnityModuleWriter(1).RotatedInt(100000000, 14).RotatedInt(-7, 31).ToArray();
        var reader = new UnityModuleReader(bytes.AsMemory(2));

        Assert.That(reader.RotatedInt(14), Is.EqualTo(100000000));
        Assert.That(reader.RotatedInt(31), Is.EqualTo(-7));
    }

    [Test]
    public void ShipInitialization_RequestsSceneRefreshAndEndsWithEmptyModifierList()
    {
        var pilot = new WorldPilot(71, "mmo", 5, 2, "ship_phoenix", 900, 17) { UserName = "Pilot", FactionId = 2 };
        var stats = new WorldShipStats(320, 4000, 6000, 100, 150, []);

        var body = UnityWorldModules.ShipInitialization(pilot, stats, 6000, 4000, 700, 800);

        Assert.That(body[0], Is.EqualTo((byte)0));
        Assert.That(body[1], Is.EqualTo((byte)UnityWorldModuleId.ShipInitializationCommand));
        // Tail: cloaked, refreshMap, captainEnergy, maxCaptainEnergy, modifier count.
        Assert.That(Convert.ToHexString(body[^11..]), Is.EqualTo("00" + "01" + "00000000" + "00000000" + "00"));
        var reader = new UnityModuleReader(body.AsMemory(2));
        Assert.That(reader.RotatedInt(14), Is.EqualTo(71));
        Assert.That(reader.String(), Is.EqualTo("Pilot"));
        Assert.That(reader.String(), Is.EqualTo("ship_phoenix"));
        Assert.That(reader.RotatedInt(20), Is.EqualTo(320));
    }
}
