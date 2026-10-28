using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.Progression;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class ProgressionBackofficePostgresTests
{
    private sealed class AdminAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, EmulatorRoles.Admin)], "Test")), "Test")));
    }

    [Test]
    public async Task AdminRoutes_InspectAndOverridePersistedProgressionWithoutAwardingGateRewards()
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
            await using var seed = new NpgsqlCommand("""
                INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'backoffice-test',991122);
                INSERT INTO economy.skylab_collectors(account_id,collector_id,level,last_collected) VALUES (@a,'prometium',1,now());
                INSERT INTO economy.skylab_upgrade_jobs(account_id,collector_id,expected_level,ready_at,action_key) VALUES (@a,'prometium',1,now(),'upgrade-test');
                INSERT INTO economy.skylab_refinery_jobs(account_id,ready_at,action_key) VALUES (@a,now(),'refine-test');
                INSERT INTO economy.gate_runs(account_id,gate_id,wave,total_waves) VALUES (@a,'alpha',0,10);
                INSERT INTO economy.gate_runs(account_id,gate_id,wave,total_waves) VALUES (@a,'beta',9,10);
                INSERT INTO economy.clan_station_modules(clan_id,module_id,level,ready_at) VALUES ('mmo-guard','hull',1,now());
                INSERT INTO economy.clan_relations(clan_low,clan_high,proposer,status) VALUES ('mmo-guard','mmo-orbit','mmo-guard','pending');
                """, db);
            seed.Parameters.AddWithValue("a", accountId);
            await seed.ExecuteNonQueryAsync(ct);
        }
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<EconomyStore>(); builder.Services.AddSingleton<CockpitCatalogStore>();
        builder.Services.AddSingleton<LocalHangarStore>(); builder.Services.AddSingleton<ProgressionBackofficeStore>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AdminAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(EmulatorRoles.Admin, p => p.RequireRole(EmulatorRoles.Admin)));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapProgressionBackoffice();
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var root = $"/api/v1/backoffice/progression/accounts/{accountId}";
            var hangar = await client.GetFromJsonAsync<HangarResponse>(root + "/hangar", ct);
            Assert.That(hangar!.ActiveSlot, Is.EqualTo(1));
            Assert.That(hangar.Assignments, Has.Length.EqualTo(2));
            Assert.That((await client.PutAsJsonAsync(root + "/hangar", new ActiveHangarOverride(2), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.GetFromJsonAsync<HangarResponse>(root + "/hangar", ct))!.ActiveSlot, Is.EqualTo(2));
            var ready = DateTimeOffset.UtcNow.AddMinutes(-2);
            Assert.That((await client.PutAsJsonAsync(root + "/skylab/collectors/prometium", new TimerOverride(ready), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.PutAsJsonAsync(root + "/skylab/refinery", new TimerOverride(ready), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            var skylab = await client.GetFromJsonAsync<SkylabResponse>(root + "/skylab", ct);
            Assert.That(skylab!.Collectors.Single().ReadyAt, Is.EqualTo(ready).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(skylab.Refinery!.ReadyAt, Is.EqualTo(ready).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That((await client.PostAsync(root + "/skylab/collectors/prometium/complete-timer", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.PostAsync(root + "/skylab/refinery/complete-timer", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            skylab = await client.GetFromJsonAsync<SkylabResponse>(root + "/skylab", ct);
            Assert.That(skylab!.Collectors.Single().ReadyAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
            Assert.That(skylab.Refinery!.ReadyAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
            Assert.That((await client.PutAsJsonAsync(root + "/gates/alpha", new GateOverride(0, 19, 1000, 500, 3), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.PutAsJsonAsync(root + "/gates/alpha", new GateOverride(0, 20, 1000, 500, 3), ct)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var gate = (await client.GetFromJsonAsync<GateBackofficeRow[]>(root + "/gates", ct))!.Single(g => g.GateId == "alpha");
            Assert.That((gate.Kills, gate.ShipHp, gate.Lives), Is.EqualTo((19, 500, 3)));
            Assert.That(gate.CompletedAt, Is.Null);
            Assert.That((await client.PostAsync(root + "/gates/alpha/reset-wave", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            gate = (await client.GetFromJsonAsync<GateBackofficeRow[]>(root + "/gates", ct))!.Single(g => g.GateId == "alpha");
            Assert.That((gate.Wave, gate.Kills, gate.NpcHp), Is.EqualTo((0, 0, 1000)));
            Assert.That((await client.PostAsync(root + "/gates/alpha/complete-wave", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.PostAsync(root + "/gates/beta/complete-wave", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.PostAsync(root + "/gates/beta/complete-wave", null, ct)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            var gates = await client.GetFromJsonAsync<GateBackofficeRow[]>(root + "/gates", ct);
            Assert.That(gates!.Single(g => g.GateId == "alpha").Wave, Is.EqualTo(1));
            Assert.That(gates!.Single(g => g.GateId == "beta").CompletedAt, Is.Not.Null);
            Assert.That((await client.PutAsJsonAsync("/api/v1/backoffice/progression/clans/mmo-guard/station/hull", new StationOverride(2, ready), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            var station = (await client.GetFromJsonAsync<ClanStationModule[]>("/api/v1/backoffice/progression/clans/mmo-guard/station", ct))!.Single();
            Assert.That(station.Level, Is.EqualTo(2));
            Assert.That(station.ReadyAt, Is.EqualTo(ready).Within(TimeSpan.FromMilliseconds(1)));
            var relationRoot = "/api/v1/backoffice/progression/clans/mmo-guard/relations";
            Assert.That((await client.PutAsJsonAsync(relationRoot + "/mmo-orbit", new RelationOverride("allied", "mmo-guard"), ct)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.GetFromJsonAsync<ClanRelation[]>(relationRoot, ct))!.Single().Status, Is.EqualTo("allied"));
            await using var db = new NpgsqlConnection(postgres.GetConnectionString()); await db.OpenAsync(ct);
            await using var rewards = new NpgsqlCommand("SELECT count(*) FROM economy.gate_achievements WHERE account_id=@a", db);
            rewards.Parameters.AddWithValue("a", accountId);
            Assert.That(await rewards.ExecuteScalarAsync(ct), Is.EqualTo(0L));
        }
        finally { await app.StopAsync(ct); }
    }

    private sealed record HangarResponse(int ActiveSlot, HangarAssignment[] Assignments);
    private sealed record SkylabResponse(CollectorUpgradeJob[] Collectors, RefineryJob? Refinery);
}
