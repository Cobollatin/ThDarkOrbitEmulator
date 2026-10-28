using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class WorldRocketAndNpcAggroTests
{
    [Test]
    public async Task Rocket_UsesCooldown_ThenCreditsLethalRewardOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var rewards = new Rewards();
        var npcs = CreateNpcs(cache, mutex, rewards, clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 17, 2, 11, 3, 0)],
            RocketDamage = 200, RocketCooldownSeconds = 3
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X, npc.Y), default);

        var first = await npcs.AttackAsync(pilot, npc.Id, sessions, default, rocket: true);
        var cooling = await npcs.AttackAsync(pilot, npc.Id, sessions, default, rocket: true);
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo("hit"));
            Assert.That(first.Target!.Hp, Is.EqualTo(100));
            Assert.That(cooling.Status, Is.EqualTo("cooldown"));
            Assert.That(rewards.Calls, Is.Zero);
        });

        clock.Advance(TimeSpan.FromSeconds(3));
        var lethal = await npcs.AttackAsync(pilot, npc.Id, sessions, default, rocket: true);
        Assert.Multiple(() =>
        {
            Assert.That(lethal.Destroyed, Is.True);
            Assert.That(lethal.Reward, Is.EqualTo(new WorldNpcReward(17, 2, 11, 3)));
            Assert.That(rewards.Calls, Is.EqualTo(1));
        });
        Assert.That(await npcs.VisibleAsync(1, default), Is.Empty);
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, default, rocket: true)).Status,
            Is.EqualTo("destroyed"));
        Assert.That(rewards.LastKey, Is.Not.Null);
    }

    [Test]
    public async Task AutonomousNpc_PursuesNearestPilot_AndPersistsShieldThenHullDamage()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 300,
            NpcDamage = 40, NpcFireCooldownSeconds = 2
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X + 300, npc.Y) with
        {
            Stats = new WorldShipStats(300, 100, 50, 100, 0, [])
        }, default);

        await npcs.TickAggroAsync(1, sessions, default);
        var moved = (await npcs.VisibleAsync(1, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(moved.X, Is.EqualTo(npc.X + 100));
            Assert.That(sessions.CurrentShield(pilot), Is.EqualTo(10));
            Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(100));
        });
        clock.Advance(TimeSpan.FromSeconds(2));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.CurrentShield(pilot), Is.Zero);
            Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(70));
        });
        var secondNode = new WorldSessionStore(cache, mutex, clock: clock);
        await secondNode.EnterAsync(pilot, default);
        Assert.That(secondNode.CurrentHitPoints(pilot), Is.EqualTo(70));
    }

    [Test]
    public async Task AutonomousNpc_StopsAtConfiguredStandoffRange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 500)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 200
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(npc.X + 600, npc.Y), default);

        await npcs.TickAggroAsync(1, sessions, default);
        var approached = (await npcs.VisibleAsync(1, default)).Single();
        Assert.That(approached.X, Is.EqualTo(npc.X + 300));

        clock.Advance(TimeSpan.FromSeconds(2));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().X, Is.EqualTo(approached.X));
    }

    private static WorldNpcs CreateNpcs(IDistributedCache cache, Mutex mutex, Rewards rewards,
        Clock clock, WorldNpcOptions options) => new(cache, mutex, rewards, Options.Create(options), clock);

    private static WorldPilot Pilot(int x, int y) => new(7, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
    {
        AccountId = Guid.NewGuid(), X = x, Y = y,
        Stats = new WorldShipStats(300, 1000, 0, 100, 0, [])
    };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Rewards : IWorldNpcRewards
    {
        public int Calls { get; private set; }
        public string? LastKey { get; private set; }
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        { Calls++; LastKey = key; return Task.CompletedTask; }
    }

    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Release(gate); }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }
}
