using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldCompanionStateSpecificationTests
{
    private static readonly WorldCompanion[] First =
    [
        new("drone", "drone_flax", 0, "line"),
        new("pet", "pet_10", 0, "line")
    ];
    private static readonly WorldCompanion[] Second = [new("drone", "drone_iris", 0, "arrow")];

    private static (WorldSessionStore Store, WorldPilot Pilot) Create()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var pilot = new WorldPilot(6501, "mmo", 1, 1, "ship", 2000, 1300)
        {
            Stats = new WorldShipStats(100, 1000, 0, 0, 0, []),
            ConfigurationTwoStats = new WorldShipStats(200, 1000, 0, 0, 0, []),
            Companions = First,
            ConfigurationTwoCompanions = Second
        };
        return (new WorldSessionStore(cache), pilot);
    }

    [Test]
    public async Task CompanionPositions_FollowInterpolatedShipAtRequestedTime_AndRemainPureAcrossReads()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        var start = DateTimeOffset.UtcNow;
        await store.MoveAsync(pilot, 3000, 1300, null, default, start);
        var before = store.CompanionPositions(pilot, start);
        var halfway = store.CompanionPositions(pilot, start.AddSeconds(5));
        var complete = store.CompanionPositions(pilot, start.AddSeconds(15));
        Assert.Multiple(() =>
        {
            Assert.That(before.Select(c => (c.X, c.Y)), Is.EqualTo(new[] { (1940, 1300), (1925, 1355) }));
            Assert.That(halfway.Select(c => (c.X, c.Y)), Is.EqualTo(new[] { (2440, 1300), (2425, 1355) }));
            Assert.That(complete.Select(c => (c.X, c.Y)), Is.EqualTo(new[] { (2940, 1300), (2925, 1355) }));
            Assert.That(store.CompanionPositions(pilot, start), Is.EqualTo(before),
                "a future-position read must not advance stored travel");
        });
    }

    [Test]
    public async Task CompanionPositions_SwitchConfigurationImmediately_WithoutKeepingOldCompanions()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        var at = DateTimeOffset.UtcNow;
        var switched = await store.SwitchAsync(pilot, 2, default, at);
        Assert.Multiple(() =>
        {
            Assert.That(switched.Changed, Is.True);
            Assert.That(store.CompanionPositions(pilot, at), Is.EqualTo(new[]
            {
                new WorldCompanionPosition("drone", "drone_iris", 0, "arrow", 1955, 1272)
            }));
        });
        var rejected = await store.SwitchAsync(pilot, 1, default, at.AddSeconds(4));
        Assert.That(rejected.CooldownRemainingMs, Is.EqualTo(1000));
        Assert.That(store.CompanionPositions(pilot, at.AddSeconds(4)).Select(c => c.ItemId),
            Is.EqualTo(new[] { "drone_iris" }));
        Assert.That((await store.SwitchAsync(pilot, 1, default, at.AddSeconds(5))).Changed, Is.True);
        Assert.That(store.CompanionPositions(pilot, at.AddSeconds(5)).Select(c => c.ItemId),
            Is.EqualTo(new[] { "drone_flax", "pet_10" }));
    }

    [Test]
    public async Task CompanionStatePacket_Stationary_UsesActiveConfigurationAndZeroDuration()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        await store.SwitchAsync(pilot, 2, default, DateTimeOffset.UtcNow);
        using var stream = new MemoryStream();
        await store.SendCompanionsAsync(new WorldPacketWriter(stream), pilot, default);
        stream.Position = 0;
        var packet = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        var contents = WorldPayload.Decode(packet.Payload, reader =>
        {
            var userId = WorldPayload.ReadInt64(reader);
            var count = WorldPayload.ReadInt32(reader);
            var kind = WorldPayload.ReadString(reader);
            var itemId = WorldPayload.ReadString(reader);
            var index = WorldPayload.ReadInt32(reader);
            var formation = WorldPayload.ReadString(reader);
            var x = WorldPayload.ReadInt32(reader);
            var y = WorldPayload.ReadInt32(reader);
            var targetX = WorldPayload.ReadInt32(reader);
            var targetY = WorldPayload.ReadInt32(reader);
            var duration = WorldPayload.ReadInt32(reader);
            return (userId, count, kind, itemId, index, formation, x, y, targetX, targetY, duration);
        });
        Assert.Multiple(() =>
        {
            Assert.That(packet.Command, Is.EqualTo(WorldCommandId.CompanionStateCommand));
            Assert.That(contents, Is.EqualTo((6501L, 1, "drone", "drone_iris", 0, "arrow",
                1955, 1272, 1955, 1272, 0)));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        });
    }

    [Test]
    public async Task CompanionStatePacket_Moving_ProjectsCurrentTowardShipDestination_WithSharedRemainingDuration()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        await store.MoveAsync(pilot, 3000, 1300, null, default, DateTimeOffset.UtcNow.AddSeconds(-5));
        var (userId, entries) = await ReadCompanionStateAsync(store, pilot);
        Assert.Multiple(() =>
        {
            Assert.That(userId, Is.EqualTo(6501L));
            Assert.That(entries.Select(e => (e.Kind, e.ItemId, e.Index, e.Formation)), Is.EqualTo(new[]
            {
                ("drone", "drone_flax", 0, "line"), ("pet", "pet_10", 0, "line")
            }));
            Assert.That(entries.Select(e => (e.TargetX, e.TargetY)), Is.EqualTo(new[] { (2940, 1300), (2925, 1355) }));
            Assert.That(entries.Select(e => e.Y), Is.EqualTo(new[] { 1300, 1355 }), "straight travel keeps Y offsets");
            Assert.That(entries[0].X, Is.InRange(2440, 2600), "about halfway after five of ten seconds");
            Assert.That(entries[1].X - entries[0].X, Is.EqualTo(-15), "formation offsets survive interpolation");
            Assert.That(entries[0].Duration, Is.InRange(1, 5000));
            Assert.That(entries.Select(e => e.Duration).Distinct().Count(), Is.EqualTo(1),
                "every entry carries the same remaining travel duration");
            Assert.That(Math.Abs((entries[0].TargetX - entries[0].X) * 10 - entries[0].Duration), Is.LessThanOrEqualTo(100),
                "remaining duration matches the remaining distance at the active configuration speed");
        });
    }

    [Test]
    public async Task CompanionStatePacket_ArrivedTravelRecord_ReportsTargetAsCurrent_AndZeroDuration()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        await store.MoveAsync(pilot, 3000, 1300, null, default, DateTimeOffset.UtcNow.AddSeconds(-20));
        var (_, entries) = await ReadCompanionStateAsync(store, pilot);
        Assert.That(entries.Select(e => (e.X, e.Y, e.TargetX, e.TargetY, e.Duration)), Is.EqualTo(new[]
        {
            (2940, 1300, 2940, 1300, 0), (2925, 1355, 2925, 1355, 0)
        }));
    }

    [Test]
    public async Task CompanionStatePacket_OutOfMapDestination_UsesClampedShipTarget()
    {
        var (store, original) = Create();
        var pilot = await store.EnterAsync(original, default);
        await store.MoveAsync(pilot, 30000, 1300, null, default, DateTimeOffset.UtcNow);
        var (_, entries) = await ReadCompanionStateAsync(store, pilot);
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(e => (e.TargetX, e.TargetY)), Is.EqualTo(new[] { (20940, 1300), (20925, 1355) }));
            Assert.That(entries[0].Duration, Is.InRange(185000, 190000),
                "19000 units at speed 100 toward the clamped edge, not the requested 28000");
        });
    }

    [TestCase(0.5, 20000)]
    [TestCase(2.0, 5000)]
    public async Task CompanionStatePacket_RuntimeSpeedMultiplier_UsesBaseSpeedForRemainingDuration(
        double multiplier, int moveDuration)
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache, variables: new MovementVariables(multiplier));
        var pilot = await store.EnterAsync(Create().Pilot with { HangarSlot = 2, BaseSpeedAtLoad = 320 }, default);
        var move = await store.MoveAsync(pilot, 3000, 1300, null, default,
            DateTimeOffset.UtcNow.AddSeconds(-2));
        var (_, entries) = await ReadCompanionStateAsync(store, pilot);
        var remaining = entries[0].TargetX - entries[0].X;
        Assert.Multiple(() =>
        {
            Assert.That(move.TimeToTargetMs, Is.EqualTo(moveDuration),
                "movement uses the runtime-adjusted speed");
            Assert.That(remaining, Is.GreaterThan(0));
            Assert.That(entries[0].Duration, Is.InRange((remaining * 10) - 10, (remaining * 10) + 10),
                "the packet uses base speed 100 for its duration");
            Assert.That(entries.Select(e => e.Duration).Distinct(), Is.EquivalentTo(new[] { entries[0].Duration }));
            Assert.That(entries[0].Duration, multiplier > 1 ? Is.GreaterThan(moveDuration - 2000)
                : Is.LessThan(moveDuration - 2000),
                "packet duration differs from the actual travel time when speed is adjusted");
        });
    }

    private sealed class MovementVariables(double multiplier) : IRuntimeVariableRegistry
    {
        public IReadOnlyList<RuntimeVariable> Catalog => [];
        public Task<double> NumberAsync(string key, CancellationToken ct = default) => Task.FromResult(key switch
        {
            "world.ship.phoenix_base_speed" => 320d,
            "world.movement.speed_multiplier" => multiplier,
            _ => 0d
        });
        public Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100,
            long? beforeChangeId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<(long UserId, List<(string Kind, string ItemId, int Index, string Formation,
        int X, int Y, int TargetX, int TargetY, int Duration)> Entries)> ReadCompanionStateAsync(
        WorldSessionStore store, WorldPilot pilot)
    {
        using var stream = new MemoryStream();
        await store.SendCompanionsAsync(new WorldPacketWriter(stream), pilot, default);
        stream.Position = 0;
        var packet = (await new WorldPacketReader(stream).ReadAsync())!.Value;
        Assert.That(packet.Command, Is.EqualTo(WorldCommandId.CompanionStateCommand));
        return WorldPayload.Decode(packet.Payload, reader =>
        {
            var userId = WorldPayload.ReadInt64(reader);
            var count = WorldPayload.ReadInt32(reader);
            var entries = new List<(string, string, int, string, int, int, int, int, int)>();
            for (var i = 0; i < count; i++)
                entries.Add((WorldPayload.ReadString(reader), WorldPayload.ReadString(reader),
                    WorldPayload.ReadInt32(reader), WorldPayload.ReadString(reader),
                    WorldPayload.ReadInt32(reader), WorldPayload.ReadInt32(reader),
                    WorldPayload.ReadInt32(reader), WorldPayload.ReadInt32(reader),
                    WorldPayload.ReadInt32(reader)));
            return (userId, entries);
        });
    }
}
