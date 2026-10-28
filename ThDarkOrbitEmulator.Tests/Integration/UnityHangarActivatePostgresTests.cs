using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class UnityHangarActivatePostgresTests
{
    private sealed record Reply(int Success, string? Message);

    [Test]
    public async Task Activate_UsesAssignedHangarIdAndPersistsSelection_RejectsBadRequests()
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
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id,company) VALUES (@id,'activate-pilot',100000078,'mmo')", db);
            insert.Parameters.AddWithValue("id", accountId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<GameAuthSessionStore>();
        builder.Services.AddSingleton<IGameAuthSessionStore>(sp => sp.GetRequiredService<GameAuthSessionStore>());
        builder.Services.AddSingleton<EconomyStore>();
        builder.Services.AddSingleton<LocalHangarStore>();
        builder.Services.AddSingleton<CockpitCatalogStore>();
        await using var app = builder.Build();
        app.MapCockpitCatalog();
        var sessions = app.Services.GetRequiredService<GameAuthSessionStore>();
        var hangars = app.Services.GetRequiredService<LocalHangarStore>();
        var catalog = app.Services.GetRequiredService<CockpitCatalogStore>();
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, "activate-pilot", null, 100000078, "mmo"), null, ct)).Session;
        var assigned = await catalog.HangarsAsync(session, 1, ct);
        await catalog.UpsertAssignmentAsync(new HangarAssignment(accountId, 2, "ship_phoenix", "654321"), ct);
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string path = "/unityApi/activate.php";
            async Task<HttpResponseMessage> PostAsync(string? cookie, params (string Key, string Value)[] fields)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                { Content = new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))) };
                if (cookie is not null) request.Headers.Add("Cookie", $"dosid={cookie}");
                return await client.SendAsync(request, ct);
            }

            using var noSession = await PostAsync(null, [("hangarId", "654321")]);
            using var badSession = await PostAsync("unknown", [("hangarId", "654321")]);
            using var missing = await PostAsync(session.Dosid);
            using var malformed = await PostAsync(session.Dosid, [("hangarId", "not-an-id")]);
            using var foreign = await PostAsync(session.Dosid, [("hangarId", "999999")]);
            Assert.That(await hangars.GetActiveSlotAsync(accountId, ct), Is.EqualTo(1));
            using var success = await PostAsync(session.Dosid, [("hangarId", "654321")]);
            var payload = await success.Content.ReadFromJsonAsync<Reply>(ct);
            using var again = await PostAsync(session.Dosid, [("hangarId", "654321")]);
            Assert.Multiple(() =>
            {
                Assert.That(noSession.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(badSession.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(success.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(payload, Is.EqualTo(new Reply(1, null)));
                Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(assigned.Select(item => item.HangarId), Does.Not.Contain("654321"));
            });
            Assert.That(await hangars.GetActiveSlotAsync(accountId, ct), Is.EqualTo(2));
            Assert.That((await catalog.HangarsAsync(session, 2, ct)).Single(item => item.HangarId == "654321").HangarActive, Is.True);
        }
        finally { await app.StopAsync(ct); }
    }
}
