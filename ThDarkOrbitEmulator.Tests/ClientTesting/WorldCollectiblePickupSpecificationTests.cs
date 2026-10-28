using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldCollectiblePickupSpecificationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Test]
    public void Visibility_AnnouncesRespawnedBoxes_AndRemovesPriorCycle()
    {
        var announced = new HashSet<int>();
        var first = WorldCollectibles.Spawn(1, 42, 2);
        var next = WorldCollectibles.Spawn(1, 43, 2);

        var initial = WorldCollectibleVisibility.Diff(announced, first);
        Assert.Multiple(() =>
        {
            Assert.That(initial.Removed, Is.Empty);
            Assert.That(initial.Created.Select(box => box.Id), Is.EqualTo(first.Select(box => box.Id)));
        });

        var unchanged = WorldCollectibleVisibility.Diff(announced, first);
        Assert.That(unchanged.Removed, Is.Empty);
        Assert.That(unchanged.Created, Is.Empty);

        var respawn = WorldCollectibleVisibility.Diff(announced, next);
        Assert.Multiple(() =>
        {
            Assert.That(respawn.Removed, Is.EqualTo(first.Select(box => box.Id)));
            Assert.That(respawn.Created.Select(box => box.Id), Is.EqualTo(next.Select(box => box.Id)));
        });

        var claimed = WorldCollectibleVisibility.Diff(announced, next.Skip(1).ToArray());
        Assert.That(claimed.Removed, Is.EqualTo(new[] { next[0].Id }));
        Assert.That(claimed.Created, Is.Empty);
    }

    [Test]
    public void Spawn_UsesFixedRewardRotation_AndSeededCoordinateFormula()
    {
        var boxes = WorldCollectibles.Spawn(3, 42, 8);
        Assert.Multiple(() =>
        {
            Assert.That(boxes.Select(b => b.Id), Is.EqualTo(Enumerable.Range((3 << 26) | (42 << 10) | 1, 8)));
            Assert.That(boxes.Select(b => (b.Kind, b.AssetId, b.Amount)), Is.EqualTo(new[]
            {
                ("bonus", "credits", 200), ("bonus", "uridium", 5), ("bonus", "galaxy_energy", 1),
                ("ore", "prometium", 10), ("ore", "endurium", 8), ("ore", "terbium", 6),
                ("bonus", "credits", 200), ("bonus", "uridium", 5)
            }), "slot rewards rotate every six slots");
            Assert.That((boxes[0].X, boxes[0].Y), Is.EqualTo((9029, 5173)),
                "seed = 3*1709 + 0*7919 + 42*31 = 6429; X = 2600 + seed%15800; Y = 2200 + (seed*37)%8700");
        });
    }

    [Test]
    public void Spawn_KeepsEveryCoordinateInsideTheStarterMapInterior()
    {
        var boxes = Enumerable.Range(1, 16).SelectMany(map => WorldCollectibles.Spawn(map, 1_000_000, 1000)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(boxes.Min(b => b.X), Is.GreaterThanOrEqualTo(2600));
            Assert.That(boxes.Max(b => b.X), Is.LessThan(2600 + 15800));
            Assert.That(boxes.Min(b => b.Y), Is.GreaterThanOrEqualTo(2200));
            Assert.That(boxes.Max(b => b.Y), Is.LessThan(2200 + 8700));
            Assert.That(boxes.Select(b => b.Id).Distinct().Count(), Is.EqualTo(boxes.Length));
        });
    }

    [TestCase(0)]
    [TestCase(17)]
    [TestCase(-1)]
    public void Spawn_RejectsMapsOutsideTheStarterRange(int mapId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldCollectibles.Spawn(mapId, 1));

    [Test]
    public async Task Collect_WithoutAccount_ReturnsInvalid_BeforeTouchingTheWallet()
    {
        var world = World.Create();
        var box = WorldCollectibles.Spawn(1, Start.ToUnixTimeSeconds() / 300)[0];
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.Empty, box.X, box.Y), default);
        Assert.That((await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("invalid"));
        Assert.That(world.Wallet.Credits, Is.Empty);
    }

    [TestCase(1007)]
    [TestCase(2001)]
    [TestCase(0)]
    public async Task Collect_UnknownOrOtherMapBoxId_ReturnsMissing(int boxId)
    {
        var world = World.Create();
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), 5000, 5000), default);
        var result = await world.Collectibles.CollectAsync(pilot, boxId, world.Sessions, default);
        Assert.That((result.Status, result.Box), Is.EqualTo(("missing", (WorldCollectible?)null)));
        Assert.That(world.Wallet.Credits, Is.Empty);
    }

    [TestCase(180, 0, "collected")]
    [TestCase(181, 0, "distant")]
    [TestCase(108, 144, "collected")]
    [TestCase(109, 144, "distant")]
    public async Task Collect_UsesInclusiveSquaredDistanceRadius(int dx, int dy, string expected)
    {
        var world = World.Create();
        var box = (await world.Collectibles.VisibleAsync(1, default))[0];
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X + dx, box.Y + dy), default);
        Assert.That((await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default)).Status,
            Is.EqualTo(expected));
        Assert.That((await world.Collectibles.VisibleAsync(1, default)).Any(b => b.Id == box.Id),
            Is.EqualTo(expected == "distant"), "a distant attempt leaves the box visible");
    }

    [Test]
    public async Task Collect_RuntimePickupRadius_ReplacesTheDefault()
    {
        var world = World.Create(new() { ["world.collectibles.pickup_radius"] = 50 });
        var box = (await world.Collectibles.VisibleAsync(1, default))[0];
        var far = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X + 51, box.Y), default);
        Assert.That((await world.Collectibles.CollectAsync(far, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("distant"));
        var near = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X + 50, box.Y) with { UserId = 8 }, default);
        Assert.That((await world.Collectibles.CollectAsync(near, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("collected"));
    }

    [TestCase("credits", 1.5, 300)]
    [TestCase("credits", -2.0, 0)]
    [TestCase("uridium", 0.3, 2)]
    [TestCase("uridium", 0.5, 2)]
    [TestCase("galaxy_energy", 10.0, 1)]
    public async Task Collect_RewardMultipliers_RoundToEven_ClampAtZero_AndOnlyApplyToCreditsAndUridium(
        string asset, double multiplier, int expectedAmount)
    {
        var world = World.Create(new()
        {
            ["economy.credits_reward_multiplier"] = multiplier,
            ["economy.uridium_reward_multiplier"] = multiplier
        });
        var box = (await world.Collectibles.VisibleAsync(1, default)).Single(b => b.AssetId == asset);
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X, box.Y), default);
        var result = await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("collected"));
            Assert.That(result.Box!.Amount, Is.EqualTo(expectedAmount), "the result reports the adjusted amount");
            Assert.That(world.Wallet.Credits.Single().Box.Amount, Is.EqualTo(expectedAmount));
            Assert.That(result.Box with { Amount = box.Amount }, Is.EqualTo(box), "only the amount changes");
        });
    }

    [Test]
    public async Task Collect_ClaimKey_IsMapBoxAndCycleScoped_UsingRuntimeRespawnSeconds()
    {
        var world = World.Create(new() { ["world.collectibles.respawn_seconds"] = 60 });
        var box = (await world.Collectibles.VisibleAsync(1, default))[0];
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X, box.Y), default);
        Assert.That((await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("collected"));
        var cycle = Start.ToUnixTimeSeconds() / 60;
        Assert.That(world.Wallet.Credits.Single().Key, Is.EqualTo($"world:collect:1:{box.Id}:{cycle}"));
        Assert.That((await world.Collectibles.VisibleAsync(1, default)).Any(b => b.Id == box.Id), Is.False);

        world.Clock.Now = Start.AddSeconds(60);
        var respawned = (await world.Collectibles.VisibleAsync(1, default)).Single(b => b.AssetId == box.AssetId);
        Assert.That(respawned, Is.EqualTo(WorldCollectibles.Spawn(1, cycle + 1)[0]),
            "the next runtime-length cycle respawns the slot at its new coordinates");
        Assert.That(respawned.Id, Is.Not.EqualTo(box.Id));
        Assert.That((await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("missing"), "a delayed pickup cannot claim the next spawn");
    }

    [Test]
    public async Task Collect_WalletRejectsDuplicateLedgerKey_ReturnsClaimed_AndHidesTheBox()
    {
        var world = World.Create();
        world.Wallet.Accept = false;
        var box = (await world.Collectibles.VisibleAsync(1, default))[0];
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), box.X, box.Y), default);
        Assert.That((await world.Collectibles.CollectAsync(pilot, box.Id, world.Sessions, default)).Status,
            Is.EqualTo("claimed"));
        Assert.That((await world.Collectibles.VisibleAsync(1, default)).Any(b => b.Id == box.Id), Is.False);
    }

    [Test]
    public async Task Visible_RuntimeBonusBoxCount_ControlsGeneratedSlots()
    {
        var world = World.Create(new() { ["world.spawn.bonus_box_count"] = 2 });
        Assert.That((await world.Collectibles.VisibleAsync(4, default)).Select(b => b.Id),
            Is.EqualTo(WorldCollectibles.Spawn(4, Start.ToUnixTimeSeconds() / 300, 2).Select(b => b.Id)));
        var pilot = await world.Sessions.EnterAsync(Pilot(Guid.NewGuid(), 5000, 5000) with { MapId = 4 }, default);
        Assert.That((await world.Collectibles.CollectAsync(pilot, 4003, world.Sessions, default)).Status,
            Is.EqualTo("missing"), "slots beyond the runtime count cannot be collected");
    }

    private static WorldPilot Pilot(Guid accountId, int x, int y) =>
        new WorldPilot(7, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = accountId, X = x, Y = y,
            Stats = new WorldShipStats(300, 1000, 0, 1000, 0, [])
        };

    private sealed record World(WorldCollectibles Collectibles, WorldSessionStore Sessions, Wallet Wallet, Clock Clock)
    {
        public static World Create(Dictionary<string, double>? overrides = null)
        {
            var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
                .GetRequiredService<IDistributedCache>();
            var mutex = new Mutex();
            var wallet = new Wallet();
            var clock = new Clock { Now = Start };
            var variables = overrides is null ? null : new Variables(overrides);
            return new(new WorldCollectibles(cache, mutex, wallet, clock, variables),
                new WorldSessionStore(cache, mutex), wallet, clock);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Wallet : IWorldRewardWallet
    {
        public bool Accept { get; set; } = true;
        public List<(WorldCollectible Box, string Key)> Credits { get; } = [];
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        {
            if (Accept) Credits.Add((box, key));
            return Task.FromResult(Accept);
        }
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Releaser(gate); }
        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }

    private sealed class Variables(Dictionary<string, double> overrides) : IRuntimeVariableRegistry
    {
        private static readonly Dictionary<string, double> Defaults = new()
        {
            ["world.spawn.bonus_box_count"] = 6,
            ["world.collectibles.respawn_seconds"] = 300,
            ["world.collectibles.pickup_radius"] = 180,
            ["economy.credits_reward_multiplier"] = 1,
            ["economy.uridium_reward_multiplier"] = 1
        };
        public IReadOnlyList<RuntimeVariable> Catalog => [];
        public Task<double> NumberAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(overrides.TryGetValue(key, out var value) ? value : Defaults.GetValueOrDefault(key));
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
}
