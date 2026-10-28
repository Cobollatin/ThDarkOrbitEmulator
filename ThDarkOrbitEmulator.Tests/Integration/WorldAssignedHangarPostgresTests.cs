using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class WorldAssignedHangarPostgresTests
{
    [Test]
    public async Task WorldEntry_UsesPostgresAssignedShipStatsAndSlotEquipment()
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
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id,company) VALUES (@id,'world-pilot',100000003,'mmo')",
                connection);
            insert.Parameters.AddWithValue("id", accountId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var session = new GameAuthSession { AccountId = accountId, GameUserId = 100000003,
            Username = "world-pilot", Company = "mmo" };
        var cockpit = new CockpitCatalogStore(config);
        await cockpit.HangarsAsync(session, 1, ct);
        await cockpit.UpsertAssignmentAsync(new HangarAssignment(accountId, 1, "ship_phoenix", "123456"), ct);

        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var economy = new EconomyStore(config);
        var state = new WorldAccountState(new LocalHangarStore(cache, economy), economy,
            new LocalHangarEquipmentStore(cache, economy), new LocalPilotSkillStore(cache, economy),
            new LocalPetStore(cache), catalog: cockpit);

        var account = await state.GetAsync(session, ct);
        var details = await state.DetailsAsync(session, account.Slot, account.ShipId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(account.Slot, Is.EqualTo(1));
            Assert.That(account.ShipId, Is.EqualTo("ship_phoenix"));
            Assert.That(details, Is.Not.Null);
            Assert.That(details!.Stats.Speed, Is.EqualTo(320));
            Assert.That(details.Stats.MaxHitPoints, Is.EqualTo(4000));
            Assert.That(details.Stats.CargoCapacity, Is.EqualTo(100));
            Assert.That(details.BaseSpeedAtLoad, Is.EqualTo(320));
        });
    }
}
