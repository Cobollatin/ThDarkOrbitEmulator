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
    public async Task LaserRepeatsOnServerInterval_AndStopsExplicitly()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            LaserCooldownMilliseconds = 1000, AggroRange = 0
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X, npc.Y), default);
        await npcs.SelectAsync(pilot, npc.Id, default);
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, default)).Target!.Hp, Is.EqualTo(299));
        await npcs.StartLaserAsync(pilot, npc.Id, default);
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(299));
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(298));
        await npcs.StopLaserAsync(pilot.UserId, default);
        clock.Advance(TimeSpan.FromSeconds(2));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(298));
    }

    [Test]
    public async Task LaserMiss_DoesNotStopRepeatedFire()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            LaserCooldownMilliseconds = 1000, AggroRange = 0, LaserMissChance = 1
        };
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X, npc.Y), default);
        await npcs.SelectAsync(pilot, npc.Id, default);
        await npcs.StartLaserAsync(pilot, npc.Id, default);

        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(300));

        options.LaserMissChance = 0;
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(299));
    }

    [Test]
    public async Task LaserStopsWhenTargetLockChanges()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 2, 300, 0, 0, 0, 0, 0, 0)]
        });
        var target = (await npcs.VisibleAsync(1, default)).First();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(target.X, target.Y), default);
        await npcs.SelectAsync(pilot, target.Id, default);
        await npcs.StartLaserAsync(pilot, target.Id, default);
        await npcs.SelectAsync(pilot, target.Id + 1, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).First().Hp, Is.EqualTo(300));
    }

    [Test]
    public async Task LaserStopsWhenPilotLeavesRange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)]
        });
        var target = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(target.X, target.Y), default);
        await npcs.SelectAsync(pilot, target.Id, default);
        await npcs.StartLaserAsync(pilot, target.Id, default);
        await sessions.TeleportAsync(pilot.UserId, 1, target.X + 2000, target.Y, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        await sessions.TeleportAsync(pilot.UserId, 1, target.X, target.Y, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickLasersAsync(1, sessions, default);
        Assert.That((await npcs.VisibleAsync(1, default)).Single().Hp, Is.EqualTo(300));
    }

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
    public async Task NpcRespawn_UsesItsOwnKillTime_AndPreservesOtherNpcVitality()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var rewards = new Rewards();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 2, 250, 0, 17, 0, 0, 0, 0)],
            RespawnSeconds = 60, RocketDamage = 250, AggroRange = 0
        };
        var npcs = CreateNpcs(cache, mutex, rewards, clock, options);
        var spawns = await npcs.VisibleAsync(1, default);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(spawns[0].X, spawns[0].Y) with
        {
            Stats = new WorldShipStats(300, 1000, 0, 100, 1, [])
        }, default);

        Assert.That((await npcs.AttackAsync(pilot, spawns[0].Id, sessions, default, rocket: true)).Destroyed,
            Is.True);
        var firstRewardKey = rewards.LastKey;
        await sessions.TeleportAsync(pilot.UserId, 1, spawns[1].X, spawns[1].Y, false, default);
        Assert.That((await npcs.AttackAsync(pilot, spawns[1].Id, sessions, default)).Target!.Hp,
            Is.EqualTo(249));

        clock.Advance(TimeSpan.FromSeconds(25)); // Crosses the former global 60-second cycle.
        await npcs.TickAggroAsync(1, sessions, default);
        var beforeRespawn = await npcs.VisibleAsync(1, default);
        Assert.That(beforeRespawn.Select(n => (n.Id, n.Hp)), Is.EqualTo(new[] { (spawns[1].Id, 249) }));

        clock.Advance(TimeSpan.FromSeconds(35));
        await npcs.TickAggroAsync(1, sessions, default);
        var afterRespawn = await npcs.VisibleAsync(1, default);
        var revived = afterRespawn.Single(n => n.Id == spawns[0].Id);
        Assert.That(revived.Hp, Is.EqualTo(250));
        var map = WorldMapInstance.Starter(1);
        Assert.That(revived.X, Is.EqualTo((spawns[0].X + map.Width / 3 + 1) % (map.Width + 1)));
        Assert.That(revived.Y, Is.EqualTo((spawns[0].Y + map.Height / 3 + 1) % (map.Height + 1)));
        var replica = CreateNpcs(cache, mutex, rewards, clock, options);
        Assert.That((await replica.VisibleAsync(1, default)).Single(n => n.Id == revived.Id),
            Is.EqualTo(revived));
        Assert.That(afterRespawn.Single(n => n.Id == spawns[1].Id).Hp, Is.EqualTo(249));

        await sessions.TeleportAsync(pilot.UserId, 1, revived.X, revived.Y, false, default);
        Assert.That((await npcs.AttackAsync(pilot, spawns[0].Id, sessions, default, rocket: true)).Destroyed,
            Is.True);
        Assert.That(rewards.LastKey, Is.Not.EqualTo(firstRewardKey));
    }

    [Test]
    public async Task IdleNpc_ContinuesBoundedPatrolAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 250, 0, 0, 0, 0, 0, 1000)],
            AggroRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var spawn = (await first.VisibleAsync(1, default)).Single();

        await first.TickAggroAsync(1, sessions, default);
        var outbound = (await second.VisibleAsync(1, default)).Single();
        clock.Advance(TimeSpan.FromSeconds(5));
        await second.TickAggroAsync(1, sessions, default);
        var turn = (await first.VisibleAsync(1, default)).Single();
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickAggroAsync(1, sessions, default);
        var returning = (await second.VisibleAsync(1, default)).Single();
        var map = WorldMapInstance.Starter(1);

        Assert.Multiple(() =>
        {
            Assert.That((outbound.X, outbound.Y), Is.Not.EqualTo((spawn.X, spawn.Y)));
            Assert.That((turn.X, turn.Y), Is.Not.EqualTo((outbound.X, outbound.Y)));
            Assert.That(Math.Abs(returning.X - spawn.X), Is.LessThan(Math.Abs(turn.X - spawn.X)));
            Assert.That(returning.X, Is.InRange(0, map.Width));
            Assert.That(returning.Y, Is.InRange(0, map.Height));
        });
    }

    [Test]
    public async Task IdleNpc_ChoosesAnotherBoundedLegAfterReturningAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 250, 0, 0, 0, 0, 0, 1000)],
            AggroRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var origin = (await first.VisibleAsync(1, default)).Single();

        await first.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(5));
        await second.TickAggroAsync(1, sessions, default);
        var firstWaypoint = (await second.VisibleAsync(1, default)).Single();
        clock.Advance(TimeSpan.FromSeconds(5));
        await second.TickAggroAsync(1, sessions, default);
        var returned = (await first.VisibleAsync(1, default)).Single();
        clock.Advance(TimeSpan.FromSeconds(5));
        await first.TickAggroAsync(1, sessions, default);
        var nextWaypoint = (await second.VisibleAsync(1, default)).Single();
        var map = WorldMapInstance.Starter(1);

        Assert.Multiple(() =>
        {
            Assert.That((returned.X, returned.Y), Is.EqualTo((origin.X, origin.Y)));
            Assert.That((nextWaypoint.X, nextWaypoint.Y), Is.Not.EqualTo((firstWaypoint.X, firstWaypoint.Y)));
            Assert.That(nextWaypoint.X, Is.InRange(0, map.Width));
            Assert.That(nextWaypoint.Y, Is.InRange(0, map.Height));
        });
    }

    [Test]
    public async Task IdleNpc_AccumulatesSubpixelPatrolAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);

        await first.TickAggroAsync(1, sessions, default);
        var initial = (await first.VisibleAsync(1, default)).Single();
        for (var tick = 0; tick < 20; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await (tick % 2 == 0 ? second : first).TickAggroAsync(1, sessions, default);
        }

        var advanced = (await second.VisibleAsync(1, default)).Single();
        Assert.That((advanced.X, advanced.Y), Is.Not.EqualTo((initial.X, initial.Y)));
    }

    [Test]
    public async Task IdleNpc_DiagonalPatrolStaysWithinSpeedAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var previous = (await first.VisibleAsync(1, default)).Single();

        for (var tick = 0; tick < 3; tick++)
        {
            await (tick % 2 == 0 ? first : second).TickAggroAsync(1, sessions, default);
            var current = (await first.VisibleAsync(1, default)).Single();
            Assert.That(Math.Sqrt(Math.Pow(current.X - previous.X, 2) +
                Math.Pow(current.Y - previous.Y, 2)), Is.LessThanOrEqualTo(1));
            previous = current;
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    public async Task NpcRespawn_PreservesFireCooldownAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 250, 0, 0, 0, 0, 0, 0)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 10,
            NpcMissChance = 0, NpcFireCooldownSeconds = 5,
            RespawnSeconds = 1, RocketDamage = 250
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(spawn.X, spawn.Y), default);

        await first.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(990));
        Assert.That((await first.AttackAsync(pilot, spawn.Id, sessions, default, rocket: true)).Destroyed,
            Is.True);

        var map = WorldMapInstance.Starter(1);
        var respawnX = (spawn.X + map.Width / 3 + 1) % (map.Width + 1);
        var respawnY = (spawn.Y + map.Height / 3 + 1) % (map.Height + 1);
        await sessions.TeleportAsync(pilot.UserId, 1, respawnX, respawnY, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(990));

        clock.Advance(TimeSpan.FromSeconds(4));
        await first.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(980));
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
            NpcDamage = 40, NpcFireCooldownSeconds = 2, NpcMissChance = 0
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
            Assert.That(sessions.CurrentShield(pilot), Is.EqualTo(18));
            Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(92));
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

    [Test]
    public async Task AutonomousNpc_NewTargetDoesNotInheritIdleMovementTime()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await first.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        var idle = (await first.VisibleAsync(1, default)).Single();
        await sessions.EnterAsync(Pilot(idle.X + 500, idle.Y), default);

        await first.TickAggroAsync(1, sessions, default);
        var acquired = (await first.VisibleAsync(1, default)).Single();
        Assert.That(acquired.X, Is.EqualTo(idle.X + 1));

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        Assert.That((await second.VisibleAsync(1, default)).Single().X,
            Is.EqualTo(acquired.X + 1));
    }

    [Test]
    public async Task AutonomousNpc_SubpixelAcquisitionResetsIdleMovementTimeAcrossOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 1000, NpcStandoffRange = 141, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await first.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(2));
        await first.TickAggroAsync(1, sessions, default);
        var idle = (await first.VisibleAsync(1, default)).Single();
        clock.Advance(TimeSpan.FromSeconds(10));
        var pilot = Pilot(idle.X + 100, idle.Y + 100);
        await sessions.EnterAsync(pilot, default);

        await first.TickAggroAsync(1, sessions, default);
        var acquired = (await first.VisibleAsync(1, default)).Single();
        Assert.That((acquired.X, acquired.Y), Is.EqualTo((idle.X, idle.Y)));

        await sessions.MoveAsync(pilot, idle.X + 200, idle.Y + 200, null, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickAggroAsync(1, sessions, default);
        var continued = (await second.VisibleAsync(1, default)).Single();
        Assert.That(Math.Sqrt(Math.Pow(continued.X - idle.X, 2) +
            Math.Pow(continued.Y - idle.Y, 2)), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task AutonomousNpc_AccumulatesSubpixelPursuitAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(spawn.X + 100, spawn.Y), default);

        await first.TickAggroAsync(1, sessions, default);
        Assert.That((await first.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X + 1));

        for (var tick = 0; tick < 10; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await (tick % 2 == 0 ? first : second).TickAggroAsync(1, sessions, default);
        }

        Assert.That((await second.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X + 2));
    }

    [Test]
    public async Task AutonomousNpc_DiagonalPursuitStaysWithinSpeedAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var previous = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(previous.X + 100, previous.Y + 100), default);

        for (var tick = 0; tick < 3; tick++)
        {
            await (tick % 2 == 0 ? first : second).TickAggroAsync(1, sessions, default);
            var current = (await first.VisibleAsync(1, default)).Single();
            Assert.That(Math.Sqrt(Math.Pow(current.X - previous.X, 2) +
                Math.Pow(current.Y - previous.Y, 2)), Is.LessThanOrEqualTo(1));
            previous = current;
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    public async Task AutonomousNpc_RetreatsWhenPilotEntersStandoffRange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(spawn.X + 100, spawn.Y), default);

        await first.TickAggroAsync(1, sessions, default);
        Assert.That((await first.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X - 100));

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        Assert.That((await second.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X - 200));

        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickAggroAsync(1, sessions, default);
        Assert.That((await first.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X - 200));
    }

    [Test]
    public async Task AutonomousNpc_RetreatsFromOverlappingPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        });
        var spawn = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(spawn.X, spawn.Y), default);

        await npcs.TickAggroAsync(1, sessions, default);

        Assert.That((await npcs.VisibleAsync(1, default)).Single().X, Is.EqualTo(spawn.X - 100));
    }

    [TestCase(0, 100)]
    [TestCase(21000, 20900)]
    public async Task AutonomousNpc_OverlappingAtMapEdgeRetreatsInwardAcrossOwners(
        int edgeX, int firstX)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        await cache.SetStringAsync($"world:npc:v2:1:{spawn.Id}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                spawn.Hp, spawn.Shield, Destroyed = false, Killer = Guid.Empty,
                X = edgeX, Y = 6500
            }));
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(edgeX, 6500), default);

        await first.TickAggroAsync(1, sessions, default);
        Assert.That((await first.VisibleAsync(1, default)).Single().X, Is.EqualTo(firstX));

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        Assert.That((await second.VisibleAsync(1, default)).Single().X,
            Is.EqualTo(edgeX == 0 ? 200 : 20800));
    }

    [TestCase(0, 6500, 100, 6500, 0, 6400)]
    [TestCase(21000, 6500, 20900, 6500, 21000, 6400)]
    [TestCase(10500, 0, 10500, 100, 10400, 0)]
    [TestCase(10500, 13100, 10500, 13000, 10400, 13100)]
    public async Task AutonomousNpc_RetreatsAlongEdgeWhenDirectPathIsBlocked(
        int npcX, int npcY, int pilotX, int pilotY, int expectedX, int expectedY)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        await cache.SetStringAsync($"world:npc:v2:1:{spawn.Id}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                spawn.Hp, spawn.Shield, Destroyed = false, Killer = Guid.Empty,
                X = npcX, Y = npcY
            }));
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(pilotX, pilotY), default);

        await first.TickAggroAsync(1, sessions, default);
        var after = (await first.VisibleAsync(1, default)).Single();
        Assert.That((after.X, after.Y), Is.EqualTo((expectedX, expectedY)));

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        var continued = (await second.VisibleAsync(1, default)).Single();
        Assert.That((continued.X, continued.Y), Is.Not.EqualTo((after.X, after.Y)));
    }

    [TestCase(0, 6500, 100, 6550, 0, 6400)]
    [TestCase(21000, 6500, 20900, 6550, 21000, 6400)]
    [TestCase(10500, 0, 10550, 100, 10400, 0)]
    [TestCase(10500, 13100, 10550, 13000, 10400, 13100)]
    public async Task AutonomousNpc_UsesFullEdgeStepWhenDiagonalRetreatIsClipped(
        int npcX, int npcY, int pilotX, int pilotY, int expectedX, int expectedY)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        await cache.SetStringAsync($"world:npc:v2:1:{spawn.Id}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                spawn.Hp, spawn.Shield, Destroyed = false, Killer = Guid.Empty,
                X = npcX, Y = npcY
            }));
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(pilotX, pilotY), default);

        await first.TickAggroAsync(1, sessions, default);
        var after = (await first.VisibleAsync(1, default)).Single();
        Assert.That((after.X, after.Y), Is.EqualTo((expectedX, expectedY)));

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        var continued = (await second.VisibleAsync(1, default)).Single();
        Assert.That((continued.X, continued.Y), Is.Not.EqualTo((after.X, after.Y)));
    }

    [TestCase(0, 0, 100, 100)]
    [TestCase(21000, 13100, 20900, 13000)]
    public async Task AutonomousNpc_RetreatStaysWithinMap(int npcX, int npcY,
        int pilotX, int pilotY)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 300, NpcFireRange = 0
        });
        var spawn = (await npcs.VisibleAsync(1, default)).Single();
        await cache.SetStringAsync($"world:npc:v2:1:{spawn.Id}",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                spawn.Hp, spawn.Shield, Destroyed = false, Killer = Guid.Empty,
                X = npcX, Y = npcY
            }));
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        await sessions.EnterAsync(Pilot(pilotX, pilotY), default);

        await npcs.TickAggroAsync(1, sessions, default);

        var after = (await npcs.VisibleAsync(1, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(after.X, Is.EqualTo(npcX));
            Assert.That(after.Y, Is.EqualTo(npcY));
        });
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)]
        });
        Assert.That((await second.VisibleAsync(1, default)).Single(), Is.EqualTo(after));
    }

    [Test]
    public async Task AutonomousNpc_StartsNewPatrolFromLastPursuitPositionAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 1000)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 0
        };
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(spawn.X + 600, spawn.Y), default);

        await npcs.TickAggroAsync(1, sessions, default);
        var pursued = (await npcs.VisibleAsync(1, default)).Single();
        Assert.That(pursued.X, Is.EqualTo(spawn.X + 600));

        await sessions.TeleportAsync(pilot.UserId, 2, 5000, 5000, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickAggroAsync(1, sessions, default);
        var otherNode = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var roaming = (await otherNode.VisibleAsync(1, default)).Single();
        Assert.That((roaming.X, roaming.Y), Is.Not.EqualTo((pursued.X, pursued.Y)));

        clock.Advance(TimeSpan.FromSeconds(5));
        await otherNode.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(5));
        await npcs.TickAggroAsync(1, sessions, default);
        var returned = (await otherNode.VisibleAsync(1, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That((returned.X, returned.Y), Is.EqualTo((pursued.X, pursued.Y)));
            Assert.That((returned.X, returned.Y), Is.Not.EqualTo((spawn.X, spawn.Y)));
        });
    }

    [Test]
    public async Task AutonomousNpc_DoesNotSpendOldPursuitTimeOnFirstPatrolStep()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var spawn = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(spawn.X + 100, spawn.Y), default);
        await first.TickAggroAsync(1, sessions, default);
        var pursued = (await first.VisibleAsync(1, default)).Single();

        await sessions.TeleportAsync(pilot.UserId, 2, 5000, 5000, false, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await second.TickAggroAsync(1, sessions, default);
        var firstPatrol = (await second.VisibleAsync(1, default)).Single();
        var firstDistance = Math.Sqrt(Math.Pow(firstPatrol.X - pursued.X, 2) +
            Math.Pow(firstPatrol.Y - pursued.Y, 2));
        Assert.That(firstDistance, Is.InRange(1, 100));

        clock.Advance(TimeSpan.FromSeconds(1));
        await first.TickAggroAsync(1, sessions, default);
        var continued = (await first.VisibleAsync(1, default)).Single();
        var nextDistance = Math.Sqrt(Math.Pow(continued.X - firstPatrol.X, 2) +
            Math.Pow(continued.Y - firstPatrol.Y, 2));
        Assert.That(nextDistance, Is.InRange(1, 100));
    }

    [Test]
    public async Task AutonomousNpc_SkipsInvisiblePilotAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 10,
            NpcFireCooldownSeconds = 1, NpcMissChance = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var invisible = await sessions.EnterAsync(Pilot(npc.X + 50, npc.Y), default);
        invisible = await sessions.SetCloakAsync(invisible, true, default);
        var visible = await sessions.EnterAsync(Pilot(npc.X + 150, npc.Y) with
            { UserId = 8 }, default);

        await first.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.TickAggroAsync(1, sessions, default);

        Assert.Multiple(() =>
        {
            Assert.That(sessions.CurrentHitPoints(invisible), Is.EqualTo(1000));
            Assert.That(sessions.CurrentHitPoints(visible), Is.EqualTo(980));
        });
    }

    [Test]
    public async Task AutonomousNpc_KeepsTargetUntilPeaceZoneThenRetargets()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 10,
            NpcFireCooldownSeconds = 1, NpcMissChance = 0
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var first = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);
        var second = await sessions.EnterAsync(Pilot(npc.X + 600, npc.Y) with { UserId = 8 }, default);

        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(first), Is.EqualTo(990));
        await sessions.TeleportAsync(second.UserId, 1, npc.X + 100, npc.Y, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.CurrentHitPoints(first), Is.EqualTo(980));
            Assert.That(sessions.CurrentHitPoints(second), Is.EqualTo(1000));
        });

        var station = WorldMapInstance.Starter(1).HomeStation;
        await sessions.TeleportAsync(first.UserId, 1, station.X, station.Y, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(second), Is.EqualTo(990));
    }

    [Test]
    public async Task PassiveNpc_PursuesWithoutFiring()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 100,
                Aggressive: false)],
            AggroRange = 1000, NpcStandoffRange = 0, NpcFireRange = 1000,
            NpcDamage = 10, NpcFireCooldownSeconds = 1
        });
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);

        await npcs.TickAggroAsync(1, sessions, default);
        var pursued = (await npcs.VisibleAsync(1, default)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(pursued.X, Is.GreaterThan(npc.X));
            Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(1000));
        });
        clock.Advance(TimeSpan.FromSeconds(2));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(1000));
    }

    [Test]
    public async Task AutonomousNpc_MissConsumesShotCooldownWithoutDamagingPilot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 10,
            NpcFireCooldownSeconds = 2, NpcMissChance = 1
        };
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);

        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(1000));

        options.NpcMissChance = 0;
        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(1000));

        clock.Advance(TimeSpan.FromSeconds(1));
        await npcs.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(990));
    }

    [Test]
    public async Task AutonomousNpc_RollsBoundedDamageForEachShot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 40,
            NpcDamageSpread = 0.5, NpcFireCooldownSeconds = 1, NpcMissChance = 0
        };
        var npcs = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);
        var damages = new List<long>();

        for (var shot = 0; shot < 10; shot++)
        {
            var before = sessions.CurrentHitPoints(pilot);
            await npcs.TickAggroAsync(1, sessions, default);
            damages.Add(before - sessions.CurrentHitPoints(pilot));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.That(damages, Is.All.InRange(20, 60));
        Assert.That(damages.Distinct().Count(), Is.GreaterThan(1));
    }

    [Test]
    public async Task PassiveNpc_RetaliatesAgainstAttackerThenStopsAfterTargetLeaves()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0,
                Aggressive: false)],
            AggroRange = 1000, NpcFireRange = 1000, NpcDamage = 10,
            NpcFireCooldownSeconds = 1, NpcMissChance = 0
        };
        var firstNode = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await firstNode.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var attacker = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);
        var bystander = await sessions.EnterAsync(Pilot(npc.X + 600, npc.Y) with { UserId = 8 }, default);

        await firstNode.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(attacker), Is.EqualTo(1000));
        await sessions.TeleportAsync(bystander.UserId, 1, npc.X + 100, npc.Y, false, default);
        Assert.That((await firstNode.AttackAsync(attacker, npc.Id, sessions, default)).Status, Is.EqualTo("hit"));

        var secondNode = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        await secondNode.TickAggroAsync(1, sessions, default);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.CurrentHitPoints(attacker), Is.EqualTo(990));
            Assert.That(sessions.CurrentHitPoints(bystander), Is.EqualTo(1000));
        });

        await sessions.TeleportAsync(attacker.UserId, 2, 5000, 5000, false, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await firstNode.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(bystander), Is.EqualTo(1000));
    }

    [Test]
    public async Task AutonomousNpc_DefaultFireCooldown_IsSharedAcrossMapOwners()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var clock = new Clock();
        var options = new WorldNpcOptions
        {
            Templates = [new("Scout", "npc_scout", 1, 300, 0, 0, 0, 0, 0, 0)],
            NpcDamage = 10, NpcMissChance = 0
        };
        var first = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var second = CreateNpcs(cache, mutex, new Rewards(), clock, options);
        var npc = (await first.VisibleAsync(1, default)).Single();
        var sessions = new WorldSessionStore(cache, mutex, clock: clock);
        var pilot = await sessions.EnterAsync(Pilot(npc.X + 400, npc.Y), default);

        await first.TickAggroAsync(1, sessions, default);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await second.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(990));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await second.TickAggroAsync(1, sessions, default);
        Assert.That(sessions.CurrentHitPoints(pilot), Is.EqualTo(980));
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
