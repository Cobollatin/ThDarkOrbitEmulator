using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

namespace ThDarkOrbitEmulator.Tests.Integration;

public class IdentityPostgresMigrationTests
{
    [Test]
    [Category("Integration")]
    public async Task SqlMigrations_CreateIdentitySchemaAndAccountsTable_InPostgresContainer()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit")
            .WithUsername("app")
            .WithPassword("app")
            .Build();

        await postgres.StartAsync(cancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:identity-write"] = postgres.GetConnectionString()
            })
            .Build();

        var options = Options.Create(new IdentityDatabaseOptions
        {
            AutoMigrate = true,
            BackupBeforeMigration = false,
            Schema = "identity"
        });

        var migrationService = new SqlMigrationHostedService(
            configuration,
            options,
            NullLogger<SqlMigrationHostedService>.Instance);

        await migrationService.StartAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        var accountsTable = await ScalarAsync<string?>(connection, "SELECT to_regclass('identity.accounts')::text;", cancellationToken);
        var economyLedger = await ScalarAsync<string?>(connection, "SELECT to_regclass('economy.ledger')::text;", cancellationToken);
        var migrationCount = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM identity.schema_migrations;", cancellationToken);
        var gameUserIdDefault = await ScalarAsync<string?>(connection, """
            SELECT column_default
            FROM information_schema.columns
            WHERE table_schema = 'identity'
              AND table_name = 'accounts'
              AND column_name = 'game_user_id';
            """, cancellationToken);
        var companyColumn = await ScalarAsync<string?>(connection, """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'identity'
              AND table_name = 'accounts'
              AND column_name = 'company';
            """, cancellationToken);
        await using (var insert = new NpgsqlCommand("INSERT INTO identity.accounts (id, username, game_user_id, company) VALUES (gen_random_uuid(), 'test-company', 999999, 'tst');", connection))
            await insert.ExecuteNonQueryAsync(cancellationToken);
        var savedCompany = await ScalarAsync<string?>(connection,
            "SELECT company FROM identity.accounts WHERE username = 'test-company';", cancellationToken);
        var accountId = await ScalarAsync<Guid>(connection,
            "SELECT id FROM identity.accounts WHERE username = 'test-company';", cancellationToken);
        var catalog = new CockpitCatalogStore(configuration);
        var session = new GameAuthSession { AccountId = accountId, GameUserId = 999999, Company = "tst" };
        var adminView = await catalog.AssignmentsAsync(accountId, cancellationToken);
        var starterShips = await catalog.HangarsAsync(session, 1, cancellationToken);
        await catalog.UpsertAssignmentAsync(new HangarAssignment(accountId, 1, "ship_phoenix", "123456"), cancellationToken);
        await catalog.UpsertShipAsync(new CatalogShip("ship_phoenix", "Phoenix Mk II", 1,
            "/do_img/global/items/ship/phoenix_100x100.png", true), cancellationToken);
        var assignedShips = await catalog.HangarsAsync(session, 1, cancellationToken);
        await catalog.UpsertLanguageAsync(new ClientLanguage("en", "English UK", true, true), cancellationToken);
        var defaultLanguage = (await catalog.LanguagesAsync(cancellationToken)).Single(item => item.IsDefault);
        var cockpitCategories = new[]
        {
            "resource_breakingNews", "resource_externalHome", "resource_item_old", "resource_localeBase",
            "resource_premiumExtension", "resource_upsellPackage", "resource_xtm"
        };
        foreach (var category in cockpitCategories)
            await catalog.UpsertLocaleOverrideAsync(new LocaleOverride("en", category, "catalog_probe", category), cancellationToken);
        var locales = await catalog.LocaleOverridesAsync(cancellationToken);
        await catalog.UpsertServerAsync(new ClientServer("global3", "Global 3", "127.0.0.1", 1,
            "online", true, true), cancellationToken);
        var defaultServer = (await catalog.ServersAsync(cancellationToken)).Single(item => item.IsDefault);

        Assert.Multiple(() =>
        {
            Assert.That(accountsTable, Is.EqualTo("identity.accounts"));
            Assert.That(economyLedger, Is.EqualTo("economy.ledger"));
            Assert.That(migrationCount, Is.EqualTo(34));
            Assert.That(gameUserIdDefault, Does.Contain("identity.game_user_id_seq"));
            Assert.That(companyColumn, Is.EqualTo("character varying"));
            Assert.That(savedCompany, Is.EqualTo("tst"));
            Assert.That(starterShips.Select(item => item.ShipId), Is.EqualTo(new[] { "ship_pusat-plus", "ship_phoenix" }));
            Assert.That(adminView.Select(item => item.ShipId), Is.EqualTo(new[] { "ship_pusat-plus", "ship_phoenix" }));
            Assert.That(assignedShips[0].ShipId, Is.EqualTo("ship_phoenix"));
            Assert.That(assignedShips[0].HangarId, Is.EqualTo("123456"));
            Assert.That(assignedShips[0].ShipName, Is.EqualTo("Phoenix Mk II"));
            Assert.That(defaultLanguage.Code, Is.EqualTo("en"));
            Assert.That(locales.Select(item => item.Category), Is.EquivalentTo(cockpitCategories));
            Assert.That(defaultServer.InstanceName, Is.EqualTo("Global 3"));
        });
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)value;
    }
}
