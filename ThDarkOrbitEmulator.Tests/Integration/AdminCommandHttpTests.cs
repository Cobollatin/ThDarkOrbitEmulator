using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Diagnostics;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class AdminCommandHttpTests
{
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "http-admin")], "Test")), "Test")));
    }
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Release(gate); }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }

    [Test]
    public async Task HttpRoutesExecuteAndAttributeCommandsAndVariableAliases()
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddFastEndpoints(options => options.Filter = type =>
            type.Namespace == typeof(AdminCommandEndpoint).Namespace && type.Name.EndsWith("Endpoint"));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead), p => p.RequireAuthenticatedUser());
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser());
        });
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "http-node" }));
        builder.Services.AddSingleton<IServerMeshMutex, Mutex>();
        builder.Services.AddSingleton<IRuntimeVariableRegistry, RuntimeVariableStore>();
        builder.Services.AddSingleton<IServerMeshRegistry, ServerMeshCoordinator>();
        builder.Services.AddSingleton<WorldSessionStore>();
        builder.Services.AddSingleton<EconomyStore>();
        builder.Services.AddSingleton<WorldAdminEvents>();
        builder.Services.AddSingleton<IAdminCommandDispatcher, AdminCommandDispatcher>();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.UseFastEndpoints(options =>
        {
            options.Endpoints.RoutePrefix = "api";
            options.Versioning.Prefix = "v";
            options.Versioning.PrependToRoute = true;
        });
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string prefix = "/api/v1/backoffice";
            var help = await client.PostAsJsonAsync(prefix + "/admin/command",
                new { text = "help", surface = "ServerManagement" }, ct);
            Assert.That(help.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var structured = await client.PostAsJsonAsync(prefix + "/admin/command",
                new { verb = "var set", arguments = new[] { "world.tick_rate_ms", "725", "http test" },
                    surface = "AdminCms" }, ct);
            Assert.That(structured.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var cmsSet = await client.PutAsJsonAsync(prefix + "/runtime-vars/world.movement.speed_multiplier",
                new { value = "2", reason = "CMS live edit" }, ct);
            Assert.That(cmsSet.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var cmsChange = await cmsSet.Content.ReadFromJsonAsync<RuntimeVariableChangeEntry>(cancellationToken: ct);
            Assert.That(cmsChange!.Surface, Is.EqualTo("AdminCms"));
            var effective = await client.GetFromJsonAsync<RuntimeVariable>(
                prefix + "/runtime-vars/world.movement.speed_multiplier", ct);
            Assert.That(effective!.CurrentValue, Is.EqualTo("2"));
            var cmsHistory = await client.GetFromJsonAsync<RuntimeVariableChangeEntry[]>(
                prefix + "/runtime-vars/history?key=world.movement.speed_multiplier", ct);
            Assert.That(cmsHistory![0].ChangeId, Is.EqualTo(cmsChange.ChangeId));
            Assert.That(cmsHistory[0].Surface, Is.EqualTo("AdminCms"));
            var cmsRollback = await client.PostAsJsonAsync(
                prefix + $"/runtime-vars/history/{cmsChange.ChangeId}/rollback",
                new { reason = "CMS rollback" }, ct);
            Assert.That(cmsRollback.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var rolledBack = await cmsRollback.Content.ReadFromJsonAsync<RuntimeVariableChangeEntry>(cancellationToken: ct);
            Assert.That(rolledBack!.Surface, Is.EqualTo("AdminCms"));
            Assert.That(rolledBack.NewValue, Is.EqualTo("1"));
            using var put = new HttpRequestMessage(HttpMethod.Put, prefix + "/runtime-vars/world.tick_rate_ms")
            { Content = JsonContent.Create(new { value = "800", reason = "cli test" }) };
            put.Headers.Add("X-Admin-Surface", "ServerManagement");
            var putResponse = await client.SendAsync(put, ct);
            Assert.That(putResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var putChange = await putResponse.Content.ReadFromJsonAsync<RuntimeVariableChangeEntry>(cancellationToken: ct);
            Assert.That(putChange!.Surface, Is.EqualTo("ServerManagement"));
            Assert.That(putChange.ChangedBy, Is.EqualTo("http-admin"));
            Assert.That(putChange.NodeId, Is.EqualTo("http-node"));
            Assert.That((await client.GetAsync(prefix + "/runtime-vars/world.tick_rate_ms", ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await client.GetAsync(prefix + "/runtime-vars/history?key=world.tick_rate_ms", ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.OK));
            var rollback = await client.PostAsJsonAsync(prefix + $"/runtime-vars/history/{putChange!.ChangeId}/rollback",
                new { reason = "http rollback" }, ct);
            Assert.That(rollback.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var reset = await client.PostAsJsonAsync(prefix + "/runtime-vars/world.tick_rate_ms/reset",
                new { reason = "http reset" }, ct);
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var history = await client.GetFromJsonAsync<AdminCommandHistoryEntry[]>(
                prefix + "/admin/command/history", ct);
            Assert.That(history, Has.Length.EqualTo(2));
            Assert.That(history!.Select(h => h.Surface), Is.EquivalentTo(new[] { "ServerManagement", "AdminCms" }));
            Assert.That(history.All(h => h.Actor == "http-admin" && h.NodeId == "http-node"), Is.True);
            if (OperatingSystem.IsWindows())
            {
                var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "../../../../scripts/admin/Invoke-ServerAdminCommand.ps1"));
                var start = new ProcessStartInfo("powershell")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                };
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-CommandText", "var set world.tick_rate_ms 775 cli", "-BaseUri", client.BaseAddress!.ToString() })
                    start.ArgumentList.Add(argument);
                start.Environment["THDARKORBIT_ADMIN_TOKEN"] = Guid.NewGuid().ToString("N");
                using var process = Process.Start(start)!;
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var error = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                Assert.That(process.ExitCode, Is.Zero, error);
                Assert.That(output, Does.Contain("world.tick_rate_ms"));
                var afterCli = await client.GetFromJsonAsync<AdminCommandHistoryEntry[]>(
                    prefix + "/admin/command/history", ct);
                Assert.That(afterCli, Has.Length.EqualTo(3));
                Assert.That(afterCli![0].Surface, Is.EqualTo("ServerManagement"));
                Assert.That(afterCli[0].Actor, Is.EqualTo("http-admin"));
            }
            var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText?.Contains("backoffice/admin/command") == true).ToArray();
            Assert.That(routes, Has.Length.EqualTo(2));
        }
        finally
        {
            await app.StopAsync(ct);
            FastEndpointsTestResolver.Reset();
        }
    }

    [TearDown]
    public void ResetFastEndpointsResolver() => FastEndpointsTestResolver.Reset();
}

internal static class FastEndpointsTestResolver
{
    private static readonly System.Reflection.FieldInfo? InstanceField =
        typeof(IServiceResolver).Assembly
            .GetType("FastEndpoints.ServiceResolver")?
            .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    private static readonly System.Reflection.FieldInfo? UnitTestModeField =
        typeof(IServiceResolver).Assembly
            .GetType("FastEndpoints.ServiceResolver")?
            .GetField("<isUnitTestMode>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static void Reset()
    {
        if (InstanceField?.GetValue(null) is { } current
            && UnitTestModeField?.GetValue(current) is false)
        {
            InstanceField.SetValue(null, null);
        }
    }
}
