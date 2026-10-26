using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Menu-97: Assembly crafts sent at once through separate store instances (like separate API
// instances) never overspend inputs, never craft one form key twice and keep ledger, inventory
// and craft counts in step.
[TestFixture]
[Category("Integration")]
public class LocalAssemblyCraftPostgresTests
{
    private PostgreSqlContainer postgres = null!;
    private IConfiguration config = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await postgres.DisposeAsync();

    [Test]
    public async Task ConcurrentCrafts_AcrossInstances_ApplyOnlyWhatInputsCover()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var account = await NewAccountAsync(ct);
        var balances = await new EconomyStore(config).BalancesAsync(account, ct);
        var components = balances.Single(b => b.AssetId == "assembly_components").Amount;
        var credits = balances.Single(b => b.AssetId == "credits").Amount;
        // Seeded flax recipe: 200 components + 600 Credits -> 1 Flax drone.
        var affordable = (int)Math.Min(components / 200, credits / 600);
        Assert.That(affordable, Is.InRange(1, 10), "starter wallet should afford a few Flax crafts");
        var attempts = affordable + 3;

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(index =>
            new EconomyStore(config).CraftAsync(account, "flax", $"race:{index}", ct)));

        Assert.That(results.Count(r => r == EconomyCraftResult.Applied), Is.EqualTo(affordable));
        Assert.That(results.Count(r => r == EconomyCraftResult.InsufficientInputs), Is.EqualTo(attempts - affordable));
        var after = await new EconomyStore(config).BalancesAsync(account, ct);
        Assert.That(after.Single(b => b.AssetId == "assembly_components").Amount, Is.EqualTo(components - 200L * affordable));
        Assert.That(after.Single(b => b.AssetId == "credits").Amount, Is.EqualTo(credits - 600L * affordable));
        Assert.That((await new EconomyStore(config).InventoryAsync(account, ct)).GetValueOrDefault("drone_flax"), Is.EqualTo(affordable));
        Assert.That(await ScalarAsync("SELECT count(*) FROM economy.ledger WHERE account_id=@a AND reason='recipe_craft'", account, ct),
            Is.EqualTo(2L * affordable));
        Assert.That(await ScalarAsync("SELECT count(*) FROM economy.craft_requests WHERE account_id=@a", account, ct),
            Is.EqualTo((long)affordable), "refused crafts must roll back their request key");
        Assert.That(await ScalarAsync("SELECT COALESCE(sum(quantity),0)::bigint FROM economy.crafts WHERE account_id=@a", account, ct),
            Is.EqualTo((long)affordable));
        Assert.That(await new EconomyStore(config).CraftCountAsync(account, ct), Is.EqualTo((long)affordable));
    }

    [Test]
    public async Task SameKeySentConcurrently_CraftsOnce()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var account = await NewAccountAsync(ct);
        var before = await new EconomyStore(config).BalancesAsync(account, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            new EconomyStore(config).CraftAsync(account, "lf3", "double-submit", ct)));

        Assert.That(results.Count(r => r == EconomyCraftResult.Applied), Is.EqualTo(1));
        Assert.That(results.Count(r => r == EconomyCraftResult.Duplicate), Is.EqualTo(5));
        var after = await new EconomyStore(config).BalancesAsync(account, ct);
        Assert.That(after.Single(b => b.AssetId == "assembly_components").Amount,
            Is.EqualTo(before.Single(b => b.AssetId == "assembly_components").Amount - 150));
        Assert.That(after.Single(b => b.AssetId == "credits").Amount,
            Is.EqualTo(before.Single(b => b.AssetId == "credits").Amount - 500));
        Assert.That(await ScalarAsync("SELECT count(*) FROM economy.ledger WHERE account_id=@a AND reason='recipe_craft'", account, ct),
            Is.EqualTo(2L));
        // Replaying the stored key later is still refused.
        Assert.That(await new EconomyStore(config).CraftAsync(account, "lf3", "double-submit", ct), Is.EqualTo(EconomyCraftResult.Duplicate));
    }

    [Test]
    public async Task RefusedCrafts_ChangeNothing()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var account = await NewAccountAsync(ct);
        var store = new EconomyStore(config);
        var before = await store.BalancesAsync(account, ct);
        var inventory = await store.InventoryAsync(account, ct);

        Assert.That(await store.CraftAsync(account, "no_such_recipe", "missing", ct), Is.EqualTo(EconomyCraftResult.NotFound));
        Assert.That(await store.CraftAsync(account, "tech_speed", "tech", ct), Is.EqualTo(EconomyCraftResult.NotFound));
        Assert.That(await store.CraftAsync(account, "flax", " ", ct), Is.EqualTo(EconomyCraftResult.Invalid));
        Assert.That(await store.CraftAsync(Guid.Empty, "flax", "empty-account", ct), Is.EqualTo(EconomyCraftResult.Invalid));

        Assert.That(await store.BalancesAsync(account, ct), Is.EqualTo(before));
        Assert.That(await store.InventoryAsync(account, ct), Is.EqualTo(inventory));
        Assert.That(await ScalarAsync("SELECT count(*) FROM economy.craft_requests WHERE account_id=@a", account, ct), Is.EqualTo(0L));
        // The keys were never stored, so they still craft once the request is valid.
        Assert.That(await store.CraftAsync(account, "flax", "missing", ct), Is.EqualTo(EconomyCraftResult.Applied));
    }

    private async Task<Guid> NewAccountAsync(CancellationToken ct)
    {
        var account = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,(SELECT COALESCE(max(game_user_id),5000)+1 FROM identity.accounts))", db);
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("u", $"crafter-{account:N}"[..20]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await new EconomyStore(config).ImportAsync(account, new LocalEquipmentState([], []), ct);
        return account;
    }

    private async Task<object?> ScalarAsync(string sql, Guid account, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("a", account);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
