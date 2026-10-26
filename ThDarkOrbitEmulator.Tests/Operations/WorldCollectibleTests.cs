using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.Operations;

public sealed class WorldCollectibleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Handle(gate);
        }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Wallet : IWorldRewardWallet
    {
        public readonly Dictionary<string, long> Balances = [];
        private readonly HashSet<string> keys = [];
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        {
            lock (keys)
            {
                if (!keys.Add(key)) return Task.FromResult(false);
                Balances[box.AssetId] = Balances.GetValueOrDefault(box.AssetId) + box.Amount;
                return Task.FromResult(true);
            }
        }
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(Balances.Where(pair => pair.Key is "prometium" or "endurium" or "terbium")
                .Sum(pair => pair.Value));
    }

    [Test]
    public async Task MovingToBox_EnforcesProximityAndPreventsDoubleClaimAcrossNodes()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var wallet = new Wallet();
        var first = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var second = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var sessionsA = new WorldSessionStore(cache, mutex);
        var sessionsB = new WorldSessionStore(cache, mutex);
        var pilot = new WorldPilot(101, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = Guid.NewGuid(), X = 2000, Y = 1300,
            Stats = new WorldShipStats(10000, 1000, 0, 100, 0, [])
        };
        var box = (await first.VisibleAsync(1, default)).First();
        Assert.That((await first.CollectAsync(pilot, box.Id, sessionsA, default)).Status,
            Is.EqualTo("distant"));
        pilot = await sessionsA.EnterAsync(pilot, default);
        await sessionsA.MoveAsync(pilot, box.X, box.Y, null, default,
            DateTimeOffset.UtcNow.AddSeconds(-100));
        Assert.That(sessionsA.CurrentPosition(pilot), Is.EqualTo((box.X, box.Y)));
        var results = await Task.WhenAll(first.CollectAsync(pilot, box.Id, sessionsA, default),
            second.CollectAsync(pilot, box.Id, sessionsB, default));
        Assert.That(results.Select(result => result.Status),
            Is.EquivalentTo(new[] { "collected", "claimed" }));
        Assert.That(wallet.Balances[box.AssetId], Is.EqualTo(box.Amount));
        Assert.That((await second.VisibleAsync(1, default)).Any(item => item.Id == box.Id), Is.False);
    }

    [Test]
    public async Task Ore_RespectsCargoCapacity()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var wallet = new Wallet();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var sessions = new WorldSessionStore(cache, mutex);
        var ore = (await collectibles.VisibleAsync(1, default)).First(box => box.Kind == "ore");
        var pilot = new WorldPilot(102, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = Guid.NewGuid(), X = ore.X, Y = ore.Y,
            Stats = new WorldShipStats(100, 1000, 0, ore.Amount - 1, 0, [])
        };
        Assert.That((await collectibles.CollectAsync(pilot, ore.Id, sessions, default)).Status,
            Is.EqualTo("cargo_full"));
        Assert.That(wallet.Balances, Is.Empty);
    }

    [Test]
    public async Task NextCycle_RespawnsDeterministicSlotsOnAllReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var clock = new Clock();
        var mutex = new Mutex();
        var wallet = new Wallet();
        var first = new WorldCollectibles(cache, mutex, wallet, clock);
        var second = new WorldCollectibles(cache, mutex, wallet, clock);
        var current = await first.VisibleAsync(5, default);
        Assert.That(await second.VisibleAsync(5, default), Is.EqualTo(current));
        clock.Now = clock.Now.AddSeconds(WorldCollectibles.RespawnSeconds);
        var next = await first.VisibleAsync(5, default);
        Assert.That(await second.VisibleAsync(5, default), Is.EqualTo(next));
        Assert.That(next.Select(b => (b.X, b.Y)), Is.Not.EqualTo(current.Select(b => (b.X, b.Y))));
        Assert.That(next, Has.Count.EqualTo(6));
    }
}
