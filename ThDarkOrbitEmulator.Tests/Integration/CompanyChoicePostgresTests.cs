using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Company confirmation across API instances: each IdentityCommandService gets its own
// DbContext and connection, like separate processes sharing the identity database.
[Category("Integration")]
public class CompanyChoicePostgresTests
{
    private PostgreSqlContainer _postgres = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await _postgres.StartAsync(ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:identity-write"] = _postgres.GetConnectionString()
        }).Build();
        await new SqlMigrationHostedService(configuration,
            Options.Create(new IdentityDatabaseOptions { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _postgres.DisposeAsync();

    [Test]
    public async Task FirstChoiceWins_SameCompanyRepeats_DifferentCompanyRefused()
    {
        var accountId = await CreateAccountAsync(null);

        Assert.That(await SetAsync(accountId, "eic"), Is.True);
        Assert.Multiple(async () =>
        {
            Assert.That(await SetAsync(accountId, "eic"), Is.True);
            Assert.That(await SetAsync(accountId, "mmo"), Is.False);
            Assert.That(await SetAsync(accountId, "tst"), Is.False);
            Assert.That(await StoredAsync(accountId), Is.EqualTo("eic"));
        });
    }

    [Test]
    public async Task ConcurrentInstances_ChoosingDifferentCompanies_StoreExactlyOne()
    {
        var companies = new[] { "mmo", "eic", "vru", "tst" };
        for (var round = 0; round < 5; round++)
        {
            var accountId = await CreateAccountAsync(null);
            using var start = new ManualResetEventSlim(false);
            var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                start.Wait();
                return (Company: companies[i % companies.Length], Ok: await SetAsync(accountId, companies[i % companies.Length]));
            })).ToArray();
            start.Set();
            var results = await Task.WhenAll(attempts);
            var stored = await StoredAsync(accountId);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.AnyOf(companies));
                // Only callers that asked for the stored company may report success.
                Assert.That(results.Where(r => r.Ok).Select(r => r.Company), Is.All.EqualTo(stored));
                Assert.That(results.Where(r => r.Company == stored).Select(r => r.Ok), Is.All.True);
                Assert.That(results.Count(r => !r.Ok), Is.EqualTo(6));
            });
        }
    }

    [Test]
    public void Database_RejectsUnknownStoredCode()
    {
        // ck_identity_accounts_company keeps every stored value a catalog code (or null).
        var error = Assert.ThrowsAsync<PostgresException>(() => CreateAccountAsync("xyz"));
        Assert.That(error!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
    }

    [Test]
    public async Task InvalidCompanyOrMissingAccount_ChangesNothing()
    {
        var accountId = await CreateAccountAsync(null);

        Assert.Multiple(async () =>
        {
            Assert.That(await SetAsync(accountId, "MMO"), Is.False);
            Assert.That(await SetAsync(accountId, "xyz"), Is.False);
            Assert.That(await SetAsync(Guid.NewGuid(), "mmo"), Is.False);
            Assert.That(await StoredAsync(accountId), Is.Null);
        });
    }

    private async Task<bool> SetAsync(Guid accountId, string company)
    {
        var options = new DbContextOptionsBuilder<IdentityWriteDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var context = new IdentityWriteDbContext(options,
            Options.Create(new IdentityDatabaseOptions { Schema = "identity" }));
        return await new IdentityCommandService(context, null!)
            .SetCompanyAsync(accountId, company, TestContext.CurrentContext.CancellationToken);
    }

    private async Task<Guid> CreateAccountAsync(string? company)
    {
        var accountId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO identity.accounts (id, username, company) VALUES (@id, @name, @company)", connection);
        insert.Parameters.AddWithValue("id", accountId);
        insert.Parameters.AddWithValue("name", $"company-{accountId:N}");
        insert.Parameters.AddWithValue("company", (object?)company ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
        return accountId;
    }

    private async Task<string?> StoredAsync(Guid accountId)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand("SELECT company FROM identity.accounts WHERE id = @id", connection);
        select.Parameters.AddWithValue("id", accountId);
        return await select.ExecuteScalarAsync() as string;
    }
}
