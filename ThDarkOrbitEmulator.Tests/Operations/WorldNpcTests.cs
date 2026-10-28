using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Operations;

public sealed class WorldNpcTests
{
    [Test]
    public async Task DelayedFire_RejectsMapChangedUnderMutexBeforeDamage()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var rewards = new Rewards();
        var clock = new Clock();
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions { Templates =
            [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 90)] }), clock);
        var sessions = new WorldSessionStore(cache, mutex);
        var spawn = npcs.Spawn(1, clock.Now).Single();
        var oldMap = await sessions.EnterAsync(new WorldPilot(91, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) }, default);
        await sessions.PersistAsync(oldMap with { MapId = 2 }, default);

        Assert.ThrowsAsync<WorldMapChangedException>(async () =>
            await npcs.AttackAsync(oldMap, spawn.Id, sessions, default, expectedMapId: 1));
        Assert.That(rewards.Calls, Is.Zero);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(50));
    }

    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Handle(gate); }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }
    private sealed class Rewards : IWorldNpcRewards
    {
        public int Calls;
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }
    private sealed class FlakyRewards : IWorldNpcRewards
    {
        public int Calls;
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        {
            if (++Calls == 1) throw new IOException("temporary ledger failure");
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public async Task NpcShield_RepairsAfterQuietPeriod_OncePerTickIntervalAndNeverBeyondMaximum()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = new WorldNpcs(cache, mutex, new Rewards(), Options.Create(new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 200, 100, 0, 0, 0, 0, 0)]
        }), clock);
        var spawn = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = new WorldPilot(71, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        };
        var hit = await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        Assert.That(hit.Target!.Shield, Is.Zero);

        clock.Now = clock.Now.AddSeconds(9);
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.Zero);
        clock.Now = clock.Now.AddSeconds(1);
        await npcs.TickAggroAsync(1, sessions, default);
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.EqualTo(10));
        clock.Now = clock.Now.AddSeconds(1);
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.EqualTo(20));
        await npcs.AttackAsync(pilot, spawn.Id, sessions, default);
        clock.Now = clock.Now.AddSeconds(9);
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.Zero);
        clock.Now = clock.Now.AddSeconds(1);
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.EqualTo(10));
        for (var i = 0; i < 10; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            await npcs.TickAggroAsync(1, sessions, default);
        }
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Shield, Is.EqualTo(100));
    }

    [Test]
    public async Task SharedHealth_OnlyOnePilotGetsKill_AndNpcRespawns()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var rewards = new Rewards();
        var clock = new Clock();
        var options = Options.Create(new WorldNpcOptions { Templates =
            [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 90)] });
        var first = new WorldNpcs(cache, mutex, rewards, options, clock);
        var second = new WorldNpcs(cache, mutex, rewards, options, clock);
        var spawn = first.Spawn(1, clock.Now).Single();
        var sessionsA = new WorldSessionStore(cache, mutex);
        var sessionsB = new WorldSessionStore(cache, mutex);
        WorldPilot Pilot(long id) => new(id, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = spawn.X, Y = spawn.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var a = Pilot(41); var b = Pilot(42);
        var hits = await Task.WhenAll(first.AttackAsync(a, spawn.Id, sessionsA, default),
            second.AttackAsync(b, spawn.Id, sessionsB, default));
        Assert.That(hits.Count(h => h.Destroyed), Is.EqualTo(1));
        Assert.That(rewards.Calls, Is.EqualTo(1));
        Assert.That(await second.VisibleAsync(1, default), Is.Empty);
        clock.Now = clock.Now.AddSeconds(300);
        Assert.That(await first.VisibleAsync(1, default), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Targeting_RequiresRange_AndDamageConsumesShieldBeforeHull()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var options = Options.Create(new WorldNpcOptions { Templates =
            [new("Lordakia", "npc_lordakia", 1, 200, 60, 800, 2, 800, 4, 70)] });
        var clock = new Clock();
        var npcs = new WorldNpcs(cache, mutex, new Rewards(), options, clock);
        var spawn = npcs.Spawn(5, clock.Now).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = new WorldPilot(43, "eic", 5, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = 2000, Y = 1300,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        Assert.That((await npcs.AttackAsync(pilot, spawn.Id, sessions, default)).Status, Is.EqualTo("distant"));
        var near = pilot with { UserId = 44, X = spawn.X, Y = spawn.Y };
        var hit = await npcs.AttackAsync(near, spawn.Id, sessions, default);
        Assert.That((hit.Status, hit.Target?.Shield, hit.Target?.Hp), Is.EqualTo(("hit", 0, 160)));
    }

    [Test]
    public async Task KillerCanRetryRewardAfterLedgerFailure()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var rewards = new FlakyRewards();
        var clock = new Clock();
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Saimon", "npc_saimon", 1, 10, 0, 1, 1, 1, 1, 0)] }), clock);
        var npc = npcs.Spawn(1, clock.Now).Single();
        var sessions = new WorldSessionStore(cache, mutex);
        var pilot = new WorldPilot(45, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        Assert.ThrowsAsync<IOException>(async () => await npcs.AttackAsync(pilot, npc.Id, sessions, default));
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, default)).Status, Is.EqualTo("destroyed"));
        Assert.That(rewards.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task StaleLeaseCannotRetryACompletedKillReward()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        ServerMeshNode Node(string id) => new(id, "api-game", $"http://{id}", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0);
        await registry.HeartbeatAsync(Node("a"));
        var initial = await registry.HeartbeatAsync(Node("b"));
        var lease = initial.Leases.Single(item => item.MapId == 1);
        var fence = new WorldLeaseFence(registry, lease.NodeId, lease.Epoch);
        var clock = new Clock();
        var rewards = new Rewards();
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 10, 0, 1, 0, 0, 0, 0)] }), clock);
        var npc = npcs.Spawn(1, clock.Now).Single();
        var pilot = new WorldPilot(46, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) };
        var sessions = new WorldSessionStore(cache, mutex);
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, default, fence)).Destroyed, Is.True);
        Assert.That(rewards.Calls, Is.EqualTo(1));
        await registry.SetDrainingAsync(lease.NodeId);
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await npcs.AttackAsync(pilot, npc.Id, sessions, default, fence));
        Assert.That(rewards.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task TargetSelection_RejectsStaleLeaseBeforeWritingSharedSelection()
    {
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var registry = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ClusterOptions()));
        var now = DateTimeOffset.UtcNow;
        ServerMeshNode Node(string id) => new(id, "api-game", $"http://{id}", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0);
        var initial = await registry.HeartbeatAsync(Node("a"));
        var old = initial.Leases.Single(item => item.MapId == 1);
        var clock = new Clock();
        var npcs = new WorldNpcs(cache, mutex, new Rewards(), Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 1, 0, 0, 0, 0)] }), clock);
        var npc = npcs.Spawn(1, clock.Now).Single();
        var pilot = new WorldPilot(47, "mmo", 1, 1, "ship", 0, 0) { AccountId = Guid.NewGuid() };
        var sessions = new WorldSessionStore(cache, mutex);
        await registry.HeartbeatAsync(Node("b"));
        var changed = await registry.SetDrainingAsync("a");
        var current = changed.Leases.Single(item => item.MapId == 1);
        Assert.That(current.Epoch, Is.GreaterThan(old.Epoch));
        Assert.ThrowsAsync<WorldLeaseLostException>(async () =>
            await npcs.SelectAsync(pilot, npc.Id, default,
                new WorldLeaseFence(registry, "a", old.Epoch), sessions));
        Assert.That(await npcs.IsSelectedAsync(pilot, npc.Id, default), Is.False);
        Assert.That(await npcs.SelectAsync(pilot, npc.Id, default,
            new WorldLeaseFence(registry, current.NodeId, current.Epoch), sessions), Is.Not.Null);
        Assert.That(await npcs.IsSelectedAsync(pilot, npc.Id, default), Is.True);
    }
}
