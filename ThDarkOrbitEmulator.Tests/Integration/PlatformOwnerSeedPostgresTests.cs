using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

[Category("Integration")]
public class PlatformOwnerSeedPostgresTests
{
    [Test]
    public async Task ProvisionAdminEconomyState_ExecutesAndCanRepeat()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config,
            Options.Create(new IdentityDatabaseOptions { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);

        var accountId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(ct);
        await using (var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username) VALUES (@id,@username)", connection))
        {
            insert.Parameters.AddWithValue("id", accountId);
            insert.Parameters.AddWithValue("username", $"seed-{accountId:N}");
            await insert.ExecuteNonQueryAsync(ct);
        }

        var dbOptions = new DbContextOptionsBuilder<IdentityWriteDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var context = new IdentityWriteDbContext(dbOptions,
            Options.Create(new IdentityDatabaseOptions { Schema = "identity" }));
        await PlatformOwnerSeedHostedService.ProvisionAdminEconomyStateAsync(context, accountId, ct);
        await PlatformOwnerSeedHostedService.ProvisionAdminEconomyStateAsync(context, accountId, ct);

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM economy.balances WHERE account_id = @id AND asset_id = 'credits' AND amount >= 10000000", connection);
        count.Parameters.AddWithValue("id", accountId);
        Assert.That(await count.ExecuteScalarAsync(ct), Is.EqualTo(1L));
    }
}
