using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class AdminCommandDispatcherTests
{
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Release(gate);
        }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    [Test]
    public async Task EveryVerbUsesSharedStateAndRecordsSurfaceActorAndNode()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app")
            .WithPassword(Guid.NewGuid().ToString("N")).Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var vars = new RuntimeVariableStore(cache, config, mutex);
        var mesh = new ServerMeshCoordinator(cache, mutex, TimeProvider.System,
            Options.Create(new ClusterOptions()), vars);
        var sessions = new WorldSessionStore(cache, mutex, vars);
        var events = new WorldAdminEvents(cache, mutex);
        var dispatcher = new AdminCommandDispatcher(vars, mesh, sessions,
            new EconomyStore(config, vars), config, events);
        var now = DateTimeOffset.UtcNow;
        await mesh.HeartbeatAsync(new ServerMeshNode("node-a", "api-game", "http://localhost",
            "127.0.0.1", 60954, ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0), ct);
        var accountId = Guid.NewGuid();
        long userId;
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username) VALUES(@id,'TestPilot') RETURNING game_user_id", db);
            insert.Parameters.AddWithValue("id", accountId);
            userId = (long)(await insert.ExecuteScalarAsync(ct))!;
        }
        var pilot = new WorldPilot(userId, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = accountId, UserName = "TestPilot", X = 2000, Y = 1300,
            Stats = new WorldShipStats(320, 1000, 500, 100, 100, []) };
        await sessions.EnterAsync(pilot, ct);
        var actor = new AdminCommandActor("ServerManagement", "operator-sub", "node-a");
        Task<AdminCommandResult> Run(string command, AdminCommandActor? by = null) =>
            dispatcher.ExecuteAsync(new AdminCommand(command, null, null), by ?? actor, ct);

        Assert.That((string[])(await Run("help")).Data!, Does.Contain("world announce <message>"));
        Assert.That((IReadOnlyList<RuntimeVariable>)(await Run("var list Movement")).Data!, Is.Not.Empty);
        Assert.That(((RuntimeVariable)(await Run("var get world.tick_rate_ms")).Data!).CurrentValue, Is.EqualTo("1000"));
        var changed = (RuntimeVariableChangeEntry)(await Run("var set world.tick_rate_ms 750 'operator tuning'")).Data!;
        Assert.That(changed.Reason, Is.EqualTo("operator tuning"));
        Assert.That(changed.RawCommand, Is.EqualTo("var set world.tick_rate_ms 750 'operator tuning'"));
        Assert.That((IReadOnlyList<RuntimeVariableChangeEntry>)(await Run("var history world.tick_rate_ms 5")).Data!,
            Has.Count.GreaterThanOrEqualTo(1));
        await Run($"var rollback {changed.ChangeId} review");
        await Run("var reset world.tick_rate_ms reset");
        Assert.That(((RuntimeVariable)(await Run("var get world.tick_rate_ms")).Data!).CurrentValue, Is.EqualTo("1000"));
        await Run("var set world.tick_rate_ms 900 cms", actor with { Surface = "AdminCms" });
        await Run("var set world.tick_rate_ms 1000 game", actor with { Surface = "InGameAdmin" });

        Assert.That((ServerMeshSnapshot)(await Run("mesh status")).Data!, Is.Not.Null);
        Assert.That(((ServerMeshSnapshot)(await Run("mesh scale 2 8")).Data!).DesiredReplicas, Is.EqualTo(2));
        Assert.That((await vars.GetAsync("mesh.desired_replicas", ct)).CurrentValue, Is.EqualTo("2"));
        Assert.That((await vars.GetAsync("mesh.max_maps_per_node", ct)).CurrentValue, Is.EqualTo("8"));
        Assert.That(((ServerMeshSnapshot)(await Run("mesh drain local-sim-1")).Data!).Nodes
            .Single(n => n.NodeId == "local-sim-1").Status, Is.EqualTo(ServerMeshNodeStatus.Draining));
        await Run("mesh activate local-sim-1");
        await Run("mesh rebalance");

        await Run("world status 1");
        Assert.That(((WorldPilot)(await Run("world tp TestPilot 5 3300 2200")).Data!).MapId, Is.EqualTo(5));
        sessions.SetCurrentShield(pilot, 10);
        Assert.That(((WorldPilot)(await Run($"world heal {userId}")).Data!).MapId, Is.EqualTo(5));
        Assert.That(sessions.CurrentShield(pilot), Is.EqualTo(500));
        foreach (var kind in new[] { "box", "ore", "streuner", "lordakia", "saimon" })
            await Run($"world spawn {kind} 5 3500 2300");
        Assert.That(await events.BoxesAsync(5, ct), Has.Count.EqualTo(2));
        Assert.That(await events.NpcsAsync(5, ct), Has.Count.EqualTo(3));
        foreach (var asset in new[] { "credits", "uridium", "xp", "honor" })
            await Run($"world grant TestPilot {asset} 25");
        var balances = await new EconomyStore(config).BalancesAsync(accountId, ct);
        Assert.That(balances.Single(b => b.AssetId == "experience").Amount, Is.EqualTo(25));
        await Run("world announce 'Server restart soon'");
        Assert.That((await events.LatestAsync(ct))?.Message, Is.EqualTo("Server restart soon"));
        Assert.ThrowsAsync<ArgumentException>(async () => await Run("world grant TestPilot credits -1"));

        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var audit = new NpgsqlCommand("SELECT surface,actor,node_id FROM identity.admin_command_history", db);
            await using var reader = await audit.ExecuteReaderAsync(ct);
            var surfaces = new HashSet<string>();
            while (await reader.ReadAsync(ct))
            {
                surfaces.Add(reader.GetString(0));
                Assert.That(reader.GetString(1), Is.EqualTo("operator-sub"));
                Assert.That(reader.GetString(2), Is.EqualTo("node-a"));
            }
            Assert.That(surfaces, Is.EquivalentTo(new[] { "ServerManagement", "AdminCms", "InGameAdmin" }));
        }
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var failed = new NpgsqlCommand("SELECT COUNT(*) FROM identity.admin_command_history WHERE succeeded=false", db);
            Assert.That((long)(await failed.ExecuteScalarAsync(ct))!, Is.EqualTo(1));
        }
    }

    [Test]
    public void TextParser_KeepsQuotedReasonsAndRejectsIncompleteQuotes()
    {
        Assert.That(AdminCommandDispatcher.Parse("var set a.b 1 'ticket 42'"),
            Is.EqualTo(new[] { "var", "set", "a.b", "1", "ticket 42" }));
        Assert.Throws<ArgumentException>(() => AdminCommandDispatcher.Parse("world announce 'unfinished"));
    }
}
