using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class WorldCollectiblePostgresTests
{
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Handle(gate); }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }

    [Test]
    public async Task Collection_PersistsEconomyBalanceAndLedgerOnce()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
            { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var accountId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,@g)", db);
            cmd.Parameters.AddWithValue("a", accountId);
            cmd.Parameters.AddWithValue("u", "worldcollector");
            cmd.Parameters.AddWithValue("g", 801234L);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var economy = new EconomyStore(config);
        var collectibles = new WorldCollectibles(cache, mutex, new WorldRewardWallet(economy), TimeProvider.System);
        var sessions = new WorldSessionStore(cache, mutex);
        var box = (await collectibles.VisibleAsync(1, ct)).First(b => b.AssetId == "credits");
        var pilot = new WorldPilot(801234, "mmo", 1, 1, "ship", 0, 0)
        {
            AccountId = accountId, X = box.X, Y = box.Y,
            Stats = new WorldShipStats(100, 1000, 0, 100, 5000, [])
        };
        Assert.That((await collectibles.CollectAsync(pilot, box.Id, sessions, ct)).Status,
            Is.EqualTo("collected"));
        Assert.That((await collectibles.CollectAsync(pilot, box.Id, sessions, ct)).Status,
            Is.EqualTo("claimed"));
        var persisted = new EconomyStore(config);
        Assert.That((await persisted.BalancesAsync(accountId, ct)).Single(b => b.AssetId == "credits").Amount,
            Is.EqualTo(box.Amount));
        Assert.That((await persisted.LedgerAsync(accountId, 10, ct)).Count(r => r.Reason == "world_collect"),
            Is.EqualTo(1));

        var npcs = new WorldNpcs(cache, mutex, new WorldNpcRewards(economy),
            Options.Create(new WorldNpcOptions { Templates =
                [new("Streuner", "npc_streuner", 1, 100, 0, 400, 1, 400, 2, 0)] }), TimeProvider.System);
        var npc = (await npcs.VisibleAsync(1, ct)).Single();
        await sessions.MoveAsync(pilot, npc.X, npc.Y, null, ct, DateTimeOffset.UtcNow.AddSeconds(-100));
        Assert.That((await npcs.AttackAsync(pilot, npc.Id, sessions, ct)).Destroyed, Is.True);
        Assert.That(await persisted.NpcKillsAsync(accountId, ct), Is.EqualTo(1));
        var balances = await persisted.BalancesAsync(accountId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(balances.Single(b => b.AssetId == "credits").Amount, Is.EqualTo(box.Amount + 400));
            Assert.That(balances.Single(b => b.AssetId == "uridium").Amount, Is.EqualTo(1));
            Assert.That(balances.Single(b => b.AssetId == "experience").Amount, Is.EqualTo(400));
            Assert.That(balances.Single(b => b.AssetId == "honor").Amount, Is.EqualTo(2));
        });
        Assert.That((await persisted.LedgerAsync(accountId, 10, ct)).Count(r => r.Reason == "npc_destroyed"),
            Is.EqualTo(4));
    }
}
