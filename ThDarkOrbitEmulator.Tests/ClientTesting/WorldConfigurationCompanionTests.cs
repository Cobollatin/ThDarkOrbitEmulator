using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldConfigurationCompanionTests
{
    [Test]
    public async Task ShieldChange_IsVisibleToAnotherConnectedReplica()
    {
        var (first, cache) = CreateStore();
        var second = new WorldSessionStore(cache);
        var original = new WorldPilot(509, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1000, 1000, 0, 0, [])
        };
        var pilot = await first.EnterAsync(original, default);
        await second.EnterAsync(original, default);
        first.SetCurrentShield(pilot, 275);
        var refreshed = await second.GetOrEnterAsync(original, default);
        Assert.That(second.CurrentShield(refreshed), Is.EqualTo(275));
    }

    private static (WorldSessionStore Store, IDistributedCache Cache) CreateStore()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        return (new WorldSessionStore(cache), cache);
    }

    [Test]
    public async Task SwitchMidFlight_RetimesVelocityAndPreservesShieldRatio()
    {
        var (store, _) = CreateStore();
        var pilot = await store.EnterAsync(new WorldPilot(501, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1000, 1000, 0, 100, []),
            ConfigurationTwoStats = new WorldShipStats(200, 1000, 4000, 0, 250, [])
        }, default);
        var start = DateTimeOffset.UtcNow;
        await store.MoveAsync(pilot, 3000, 1300, null, default, start);
        store.SetCurrentShield(pilot, 250);
        var switched = await store.SwitchAsync(pilot, 2, default, start.AddSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(switched.Changed, Is.True);
            Assert.That(switched.Move!.TimeToTargetMs, Is.EqualTo(4000));
            Assert.That(store.CurrentPosition(pilot, start.AddSeconds(2)), Is.EqualTo((2200, 1300)));
            Assert.That(store.CurrentPosition(pilot, start.AddSeconds(3)), Is.EqualTo((2400, 1300)));
            Assert.That(store.CurrentShield(pilot), Is.EqualTo(1000));
            Assert.That(store.ActiveStats(pilot).LaserDamage, Is.EqualTo(250));
        });
        var returned = await store.SwitchAsync(pilot, 1, default, start.AddSeconds(7));
        Assert.That(returned.Changed, Is.True);
        Assert.That(store.CurrentShield(pilot), Is.EqualTo(250));
    }

    [Test]
    public async Task SwitchCooldown_RejectsEarlyRetryWithoutChangingTrajectory()
    {
        var (store, _) = CreateStore();
        var pilot = await store.EnterAsync(new WorldPilot(502, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1000, 1000, 0, 100, []),
            ConfigurationTwoStats = new WorldShipStats(200, 1000, 2000, 0, 200, [])
        }, default);
        var start = DateTimeOffset.UtcNow;
        await store.MoveAsync(pilot, 3000, 1300, null, default, start);
        await store.SwitchAsync(pilot, 2, default, start);
        var rejected = await store.SwitchAsync(pilot, 1, default, start.AddSeconds(4));
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Changed, Is.False);
            Assert.That(rejected.CooldownRemainingMs, Is.EqualTo(1000));
            Assert.That(store.Configuration(pilot.UserId), Is.EqualTo(2));
            Assert.That(store.CurrentPosition(pilot, start.AddSeconds(4)), Is.EqualTo((2800, 1300)));
        });
        Assert.That((await store.SwitchAsync(pilot, 1, default, start.AddSeconds(5))).Changed, Is.True);
    }

    [Test]
    public async Task ReconnectThroughAnotherStore_RestoresConfigurationShieldAndCooldown()
    {
        var (first, cache) = CreateStore();
        var original = new WorldPilot(504, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1000, 1000, 0, 100, []),
            ConfigurationTwoStats = new WorldShipStats(200, 1000, 4000, 0, 250, [])
        };
        var pilot = await first.EnterAsync(original, default);
        var start = DateTimeOffset.UtcNow;
        await first.MoveAsync(pilot, 3000, 1300, null, default, start);
        first.SetCurrentShield(pilot, 250);
        await first.SwitchAsync(pilot, 2, default, start.AddSeconds(1));
        first.Leave(pilot);

        var second = new WorldSessionStore(cache);
        var restored = await second.EnterAsync(original, default);
        Assert.Multiple(() =>
        {
            Assert.That(second.Configuration(restored.UserId), Is.EqualTo(2));
            Assert.That(second.CurrentShield(restored), Is.EqualTo(1000));
            Assert.That(second.ActiveStats(restored).LaserDamage, Is.EqualTo(250));
        });
        var rejected = await second.SwitchAsync(restored, 1, default, start.AddSeconds(4));
        Assert.That(rejected.CooldownRemainingMs, Is.GreaterThan(0));

        using var stream = new MemoryStream();
        await WorldEntryPackets.SendAsync(new WorldPacketWriter(stream), restored, default,
            second.ActiveStats(restored), second.CurrentShield(restored),
            second.CurrentPosition(restored, start.AddSeconds(2)));
        stream.Position = 0;
        var reader = new WorldPacketReader(stream);
        for (var i = 0; i < 10; i++) await reader.ReadAsync();
        var hero = (await reader.ReadAsync())!.Value;
        var attributes = WorldPayload.Decode(hero.Payload, r =>
        {
            WorldPayload.ReadInt64(r); WorldPayload.ReadString(r); WorldPayload.ReadString(r);
            WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
            var x = WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
            var speed = WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r);
            var shield = WorldPayload.ReadInt64(r);
            var maxShield = WorldPayload.ReadInt64(r);
            WorldPayload.ReadInt32(r); WorldPayload.ReadInt32(r);
            var damage = WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); WorldPayload.ReadInt32(r);
            WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); r.ReadByte();
            var count = WorldPayload.ReadInt32(r);
            for (var i = 0; i < count; i++)
            {
                WorldPayload.ReadString(r);
                WorldPayload.ReadInt32(r);
            }
            return (x, speed, shield, maxShield, damage);
        });
        Assert.That(attributes, Is.EqualTo((2300, 200, 1000L, 4000L, 250)));
    }

    [Test]
    public async Task EquippedDronesAndFueledPet_FollowShipAndChangeWithConfiguration()
    {
        var (store, cache) = CreateStore();
        var account = Guid.NewGuid();
        var pets = new LocalPetStore(cache);
        await pets.SetAsync(account, new LocalPet(true, 80, "arrow"), default);
        var pet = await pets.GetAsync(account, default);
        var equipment = new LocalEquipmentState(
            [new("drone_flax", "Flax", "Drones", 2, "drone"),
             new("drone_iris", "Iris", "Drones", 1, "drone"),
             new("pet_10", "P.E.T.", "PET", 1, "pet")],
            [new(1, 1, "drone_flax", 2), new(1, 1, "drone_iris", 1),
             new(1, 1, "pet_10", 1), new(1, 2, "drone_iris", 1)]);
        var first = WorldCompanions.FromEquipment(equipment, 1, pet);
        var second = WorldCompanions.FromEquipment(equipment, 1, pet, 2);
        Assert.That(first.Select(c => c.ItemId), Is.EqualTo(new[]
            { "drone_flax", "drone_flax", "drone_iris", "pet_10" }));
        Assert.That(second.Select(c => c.ItemId), Is.EqualTo(new[] { "drone_iris" }));
        Assert.That(first.Select(c => c.Formation), Is.All.EqualTo("arrow"));
        var arrow = WorldCompanions.At(first, 2000, 1300);
        Assert.That(arrow[0].Y, Is.LessThan(1300));
        Assert.That(arrow[1].Y, Is.GreaterThan(1300));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, pet with { Fuel = 0 }).Count, Is.EqualTo(3));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, pet with { Enabled = false }).Count, Is.EqualTo(3));
        var pilot = await store.EnterAsync(new WorldPilot(503, "mmo", 1, 1, "ship", 0, 0)
        {
            Stats = new WorldShipStats(100, 1, 0, 0, 0, []),
            ConfigurationTwoStats = new WorldShipStats(100, 1, 0, 0, 0, []),
            Companions = first, ConfigurationTwoCompanions = second
        }, default);
        var start = DateTimeOffset.UtcNow;
        var original = store.CompanionPositions(pilot, start);
        await store.MoveAsync(pilot, 3000, 1300, null, default, start);
        var moved = store.CompanionPositions(pilot, start.AddSeconds(5));
        Assert.That(moved.Zip(original, (a, b) => a.X - b.X), Is.All.EqualTo(500));
        await store.SwitchAsync(pilot, 2, default, start.AddSeconds(5));
        Assert.That(store.CompanionPositions(pilot, start.AddSeconds(5)).Select(c => c.ItemId),
            Is.EqualTo(new[] { "drone_iris" }));
    }
}
