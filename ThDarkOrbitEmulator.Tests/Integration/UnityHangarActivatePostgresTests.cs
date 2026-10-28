using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        builder.Services.AddSingleton<LocalAccountPreferencesStore>();
        builder.Services.AddSingleton<CockpitCatalogStore>();
        await using var app = builder.Build();
        app.MapCockpitCatalog();
        app.MapUnityConfigRoutes();
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

    [Test]
    public async Task ConfigRoutes_RequireSession_PropagateItThroughServerLanguageAndNavigation()
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
                "INSERT INTO identity.accounts(id,username,game_user_id,company) VALUES (@id,'config-pilot',100000079,'mmo')", db);
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
        builder.Services.AddSingleton<LocalAccountPreferencesStore>();
        builder.Services.AddSingleton<CockpitCatalogStore>();
        await using var app = builder.Build();
        app.MapCockpitCatalog();
        app.MapUnityConfigRoutes();
        var session = (await app.Services.GetRequiredService<GameAuthSessionStore>().CreateSessionAsync(
            new AccountSummary(accountId, "config-pilot", null, 100000079, "mmo"), null, ct)).Session;
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, bool authenticated,
                params (string Key, string Value)[] fields)
            {
                using var request = new HttpRequestMessage(method, path);
                if (authenticated) request.Headers.Add("Cookie", $"dosid={session.Dosid}");
                if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(
                    fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
                return await client.SendAsync(request, ct);
            }

            var cases = new (HttpMethod Method, string Path, (string Key, string Value)[] Fields)[]
            {
                (HttpMethod.Get, "/unityApi/instance.php", []),
                (HttpMethod.Post, "/unityApi/changeInstance.php", [("instanceId", "1")]),
                (HttpMethod.Post, "/unityApi/agent.php", [("functionId", "Unity_OpenProfile")]),
                (HttpMethod.Post, "/unityApi/setLang.php", [("lang", "en")]),
                (HttpMethod.Get, "/unityApi/evoucher.php", []),
                (HttpMethod.Get, "/unityApi/package.php", []),
                (HttpMethod.Get, "/unityApi/close.php", [])
            };
            foreach (var (method, path, fields) in cases)
            {
                using var unauthenticated = await SendAsync(method, path, false, fields);
                Assert.That(unauthenticated.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), path);
            }

            using var instance = await SendAsync(HttpMethod.Get, "/unityApi/instance.php", true);
            using var instanceJson = System.Text.Json.JsonDocument.Parse(await instance.Content.ReadAsStringAsync(ct));
            var instanceData = instanceJson.RootElement.GetProperty("data");
            var euWest = instanceData.GetProperty("euWest");
            var firstServer = euWest.GetProperty("serverList")[0];
            Assert.Multiple(() =>
            {
                Assert.That(instance.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(instanceData.GetProperty("hangars").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Array));
                Assert.That(euWest.GetProperty("serverCount").GetInt64(), Is.GreaterThan(0));
                Assert.That(firstServer.GetProperty("region").GetString(), Is.EqualTo("eu_west"));
                Assert.That(firstServer.GetProperty("id").GetInt64(), Is.GreaterThan(0));
                Assert.That(firstServer.GetProperty("url").GetString(), Does.Contain("/indexInternal.es"));
                Assert.That(firstServer.GetProperty("active").GetInt64(), Is.EqualTo(1));
            });
            var loginContext = new DefaultHttpContext { RequestServices = app.Services };
            loginContext.Request.Host = new HostString("127.0.0.1");
            var loginPayload = await UnityLoginAction.HandleAsync(loginContext, session.Dosid,
                app.Services.GetRequiredService<IGameAuthSessionStore>(),
                app.Services.GetRequiredService<LocalHangarStore>(),
                app.Services.GetRequiredService<LocalAccountPreferencesStore>(), ct,
                catalog: app.Services.GetRequiredService<CockpitCatalogStore>());
            Assert.That(loginPayload, Is.Not.Null);
            Assert.That((await app.Services.GetRequiredService<GameAuthSessionStore>()
                .TryGetByDosidAsync(session.Dosid, ct))!.InstanceId, Is.EqualTo(1));
            using var choose = await SendAsync(HttpMethod.Post, "/unityApi/changeInstance.php", true, ("instanceId", "1"));
            using var chooseJson = System.Text.Json.JsonDocument.Parse(await choose.Content.ReadAsStringAsync(ct));
            Assert.That(chooseJson.RootElement.GetProperty("status").GetInt64(), Is.Zero);
            Assert.That(chooseJson.RootElement.GetProperty("link").GetString(), Does.Contain("/indexInternal.es?action=internalWebGL"));
            Assert.That((await app.Services.GetRequiredService<GameAuthSessionStore>()
                .TryGetByDosidAsync(session.Dosid, ct))!.InstanceId, Is.EqualTo(1));
            using var badChoose = await SendAsync(HttpMethod.Post, "/unityApi/changeInstance.php", true, ("instanceId", "999"));
            Assert.That(badChoose.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            using var agent = await SendAsync(HttpMethod.Post, "/unityApi/agent.php", true, ("functionId", "Unity_OpenProfile"));
            Assert.That(agent.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var language = await SendAsync(HttpMethod.Post, "/unityApi/setLang.php", true, ("lang", "en"));
            Assert.That(language.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await app.Services.GetRequiredService<LocalAccountPreferencesStore>().GetAsync(accountId, ct)).Language,
                Is.EqualTo("en"));
            using var voucher = await SendAsync(HttpMethod.Get, "/unityApi/evoucher.php", true);
            using var package = await SendAsync(HttpMethod.Get, "/unityApi/package.php", true);
            Assert.That(voucher.Headers.Location?.ToString(), Is.EqualTo("/indexInternal.es?action=internalPayment"));
            Assert.That(package.Headers.Location?.ToString(), Is.EqualTo("/indexInternal.es?action=internalShop"));
            using var close = await SendAsync(HttpMethod.Get, "/unityApi/close.php", true);
            Assert.That(close.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var afterClose = await SendAsync(HttpMethod.Get, "/unityApi/instance.php", true);
            Assert.That(afterClose.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally { await app.StopAsync(ct); }
    }
}
