using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

[Category("Integration")]
public sealed class RuntimeVariablePostgresTests
{
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Lease(gate);
        }
        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Wallet : IWorldRewardWallet
    {
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct) => Task.FromResult(true);
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }
    private sealed class Rewards : IWorldNpcRewards
    {
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task ChangesAreVisibleAcrossReplicasAndRollbackAppendsImmutableHistory()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var nodeA = new RuntimeVariableStore(cache, config);
        var nodeB = new RuntimeVariableStore(cache, config);
        var actor = new RuntimeVariableActor("ServerManagement", "operator", "node-a", "test change");
        Assert.That(await nodeA.NumberAsync("world.movement.speed_multiplier", ct), Is.EqualTo(1));
        var first = await nodeA.SetAsync("world.movement.speed_multiplier", "2", actor, ct);
        Assert.That(await nodeB.NumberAsync(first.VariableKey, ct), Is.EqualTo(2));
        var second = await nodeB.SetAsync(first.VariableKey, "3",
            actor with { Surface = "AdminCms", NodeId = "node-b" }, ct);
        Assert.That(second.PreviousValue, Is.EqualTo("2"));
        var rollback = await nodeA.RollbackAsync(second.ChangeId, actor, ct);
        Assert.Multiple(() =>
        {
            Assert.That(rollback.ChangeId, Is.GreaterThan(second.ChangeId));
            Assert.That(rollback.PreviousValue, Is.EqualTo("3"));
            Assert.That(rollback.NewValue, Is.EqualTo("2"));
        });
        Assert.That(await nodeB.NumberAsync(first.VariableKey, ct), Is.EqualTo(2));
        var reset = await nodeB.ResetToDefaultAsync(first.VariableKey, actor, ct);
        Assert.That(reset.NewValue, Is.EqualTo("1"));
        Assert.That((await nodeA.HistoryAsync(first.VariableKey, ct: ct)).Select(x => x.ChangeId),
            Is.EqualTo(new[] { reset.ChangeId, rollback.ChangeId, second.ChangeId, first.ChangeId }));
        Assert.That((await nodeA.HistoryAsync(first.VariableKey, 2, rollback.ChangeId, ct))
            .Select(x => x.ChangeId), Is.EqualTo(new[] { second.ChangeId, first.ChangeId }));
        Assert.That((await nodeA.HistoryAsync(ct: ct)).Count, Is.GreaterThanOrEqualTo(4));
        var dynamic = await nodeA.SetAsync("world.custom.gravity", "9.81",
            actor with { Surface = "InGameAdmin" }, ct);
        Assert.Multiple(() =>
        {
            Assert.That(dynamic.Category, Is.EqualTo("Dynamic"));
            Assert.That(dynamic.DefaultValue, Is.EqualTo("9.81"));
            Assert.That(dynamic.Surface, Is.EqualTo("InGameAdmin"));
        });
        await nodeB.SetAsync(dynamic.VariableKey, "12", actor, ct);
        Assert.That((await nodeA.ResetToDefaultAsync(dynamic.VariableKey, actor, ct)).NewValue,
            Is.EqualTo("9.81"));
        Assert.That((await nodeB.ListAsync(ct)).Single(item => item.Key == dynamic.VariableKey).CurrentValue,
            Is.EqualTo("9.81"));
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using var mutate = new NpgsqlCommand(
            $"DELETE FROM identity.runtime_variable_history WHERE change_id={first.ChangeId}", db);
        Assert.ThrowsAsync<PostgresException>(async () => await mutate.ExecuteNonQueryAsync(ct));

        var sessions = new WorldSessionStore(cache, variables: nodeA);
        var pilot = new WorldPilot(771, "mmo", 1, 1, "pusat_plus", 0, 0)
        {
            AccountId = Guid.NewGuid(), X = 2000, Y = 1300,
            Stats = new WorldShipStats(100, 1000, 0, 100, 100, []),
            ConfigurationTwoStats = new WorldShipStats(100, 1000, 0, 100, 100, [])
        };
        pilot = await sessions.EnterAsync(pilot, ct);
        await nodeA.SetAsync("world.movement.speed_multiplier", "2", actor, ct);
        var start = DateTimeOffset.UtcNow;
        var fast = await sessions.MoveAsync(pilot, 3000, 1300, null, ct, start);
        Assert.That(fast.TimeToTargetMs, Is.EqualTo(5000));
        await nodeB.SetAsync("world.movement.speed_multiplier", "1", actor, ct);
        var slow = await sessions.MoveAsync(pilot, 3000, 1300, null, ct, start);
        Assert.That(slow.TimeToTargetMs, Is.EqualTo(10000));
        await nodeA.SetAsync("world.ship.pusat_plus_base_speed", "470", actor, ct);
        var upgraded = await sessions.MoveAsync(pilot, 3000, 1300, null, ct, start);
        Assert.That(upgraded.TimeToTargetMs, Is.EqualTo(5000));
        await nodeA.ResetToDefaultAsync("world.ship.pusat_plus_base_speed", actor, ct);
        await sessions.SwitchAsync(pilot, 2, ct, start);
        var blocked = await sessions.SwitchAsync(pilot, 1, ct, start.AddMilliseconds(1000));
        Assert.That(blocked.CooldownRemainingMs, Is.EqualTo(4000));
        await nodeB.SetAsync("world.config.switch_cooldown_ms", "500", actor, ct);
        Assert.That((await sessions.SwitchAsync(pilot, 1, ct, start.AddMilliseconds(1000))).Changed, Is.True);

        var mutex = new Mutex();
        var collectibles = new WorldCollectibles(cache, mutex, new Wallet(), TimeProvider.System, nodeA);
        var box = (await collectibles.VisibleAsync(1, ct)).First();
        var collector = pilot with { UserId = 772, X = box.X + 5, Y = box.Y };
        collector = await sessions.EnterAsync(collector, ct);
        await nodeB.SetAsync("world.collectibles.pickup_radius", "1", actor, ct);
        Assert.That((await collectibles.CollectAsync(collector, box.Id, sessions, ct)).Status, Is.EqualTo("distant"));
        await nodeA.SetAsync("world.collectibles.pickup_radius", "10", actor, ct);
        Assert.That((await collectibles.CollectAsync(collector, box.Id, sessions, ct)).Status, Is.EqualTo("collected"));

        var npcs = new WorldNpcs(cache, mutex, new Rewards(), Options.Create(new WorldNpcOptions()),
            TimeProvider.System, nodeB);
        var npc = npcs.Spawn(1, DateTimeOffset.UtcNow).First();
        var fighter = pilot with { UserId = 773, X = npc.X, Y = npc.Y };
        fighter = await sessions.EnterAsync(fighter, ct);
        await nodeA.SetAsync("world.combat.laser_damage_multiplier", "2", actor, ct);
        var attack = await npcs.AttackAsync(fighter, npc.Id, sessions, ct);
        Assert.That(attack.Damage, Is.EqualTo(200));
    }
}
