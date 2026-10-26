using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class MeshBackofficeEndpointTests
{
    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Release(gate);
        }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "infra-admin")], "Test")),
                "Test")));
    }

    [Test]
    public async Task AllMeshAndWorldRoutes_OperateThroughFastEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddFastEndpoints(options => options.Filter = type =>
            type.Namespace == typeof(MeshTopologyEndpoint).Namespace
            && type.Name.EndsWith("Endpoint", StringComparison.Ordinal));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead), p => p.RequireAuthenticatedUser());
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser());
        });
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions()));
        builder.Services.AddSingleton<IServerMeshMutex, Mutex>();
        builder.Services.AddSingleton<IServerMeshRegistry, ServerMeshCoordinator>();
        builder.Services.AddSingleton<WorldSessionStore>();
        builder.Services.AddSingleton<LocalWorkerSpawner>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseFastEndpoints(config =>
        {
            config.Endpoints.RoutePrefix = "api";
            config.Versioning.Prefix = "v";
            config.Versioning.PrependToRoute = true;
        });
        await app.StartAsync();
        try
        {
            var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText?.Contains("backoffice/mesh") == true
                    || e.RoutePattern.RawText?.Contains("backoffice/world") == true).ToArray();
            Assert.That(routes, Has.Length.EqualTo(7));
            foreach (var route in routes)
            {
                var permission = route.RoutePattern.RawText!.EndsWith("/topology")
                    || route.RoutePattern.RawText.EndsWith("/overview")
                    ? EmulatorPermissions.OpsRead : EmulatorPermissions.OpsWrite;
                Assert.That(route.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(p => p.Policy), Does.Contain(EmulatorPermissions.PolicyName(permission)));
            }
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var registry = app.Services.GetRequiredService<IServerMeshRegistry>();
            var now = DateTimeOffset.UtcNow;
            await registry.HeartbeatAsync(new ServerMeshNode("real", "api-game", "http://real",
                "127.0.0.1", 60954, ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0));
            var sessions = app.Services.GetRequiredService<WorldSessionStore>();
            await sessions.EnterAsync(new WorldPilot(123, "mmo", 1, 1, "ship", 0, 0)
                { X = 2000, Y = 1300, Stats = new WorldShipStats(200, 1000, 500, 0, 0, []) }, default);

            var prefix = "/api/v1/backoffice";
            var topology = await client.GetAsync(prefix + "/mesh/topology");
            Assert.That(topology.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using (var json = JsonDocument.Parse(await topology.Content.ReadAsStringAsync()))
                Assert.That(json.RootElement.GetProperty("totalOnlinePilots").GetInt32(), Is.EqualTo(1));
            var scale = await client.PutAsJsonAsync(prefix + "/mesh/scale", new { desiredReplicas = 3, maxMapsPerNode = 8 });
            Assert.That(scale.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await registry.GetSnapshotAsync()).DesiredReplicas, Is.EqualTo(3));
            await registry.HeartbeatAsync(new ServerMeshNode("real", "api-game", "http://real",
                "127.0.0.1", 60954, ServerMeshNodeStatus.Active, now, now, 1, [], 0, 0));
            Assert.That((await client.PostAsync(prefix + "/mesh/nodes/real/drain", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.Conflict));
            await registry.HeartbeatAsync(new ServerMeshNode("real", "api-game", "http://real",
                "127.0.0.1", 60954, ServerMeshNodeStatus.Active, now, now, 0, [], 0, 0));
            Assert.That((await client.PutAsJsonAsync(prefix + "/mesh/scale",
                new { desiredReplicas = 17, maxMapsPerNode = 8 })).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
            var rebalanced = await client.PostAsync(prefix + "/mesh/rebalance", null);
            Assert.That(rebalanced.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var overview = await client.GetAsync(prefix + "/world/overview");
            Assert.That(overview.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using (var json = JsonDocument.Parse(await overview.Content.ReadAsStringAsync()))
                Assert.That(json.RootElement.GetProperty("pilots").GetArrayLength(), Is.EqualTo(1));
            var teleport = await client.PostAsJsonAsync(prefix + "/world/pilots/123/teleport",
                new { mapId = 5, x = 3000, y = 2400, repair = true });
            Assert.That(teleport.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await client.PostAsJsonAsync(prefix + "/world/pilots/123/teleport",
                new { mapId = 17, x = 0, y = 0, repair = false })).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
            var beforeDrain = await registry.GetSnapshotAsync();
            var movedMaps = beforeDrain.Leases.Where(l => l.NodeId == "local-sim-1").ToArray();
            Assert.That(movedMaps, Is.Not.Empty);
            var drain = await client.PostAsync(prefix + "/mesh/nodes/local-sim-1/drain", null);
            Assert.That(drain.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var afterDrain = await registry.GetSnapshotAsync();
            Assert.That(afterDrain.Nodes.Single(n => n.NodeId == "local-sim-1").Status,
                Is.EqualTo(ServerMeshNodeStatus.Draining));
            foreach (var previous in movedMaps)
            {
                var lease = afterDrain.Leases.Single(l => l.MapId == previous.MapId);
                Assert.That(lease.NodeId, Is.Not.EqualTo("local-sim-1"));
                Assert.That(lease.Epoch, Is.GreaterThan(previous.Epoch));
            }
            var survivingPilot = (await sessions.ConnectedAsync(5, default)).Single(p => p.UserId == 123);
            Assert.Multiple(() =>
            {
                Assert.That(survivingPilot.ShipId, Is.EqualTo("ship"));
                Assert.That(survivingPilot.X, Is.EqualTo(3000));
                Assert.That(survivingPilot.Y, Is.EqualTo(2400));
            });
        }
        finally
        {
            await app.StopAsync();
            FastEndpointsTestResolver.Reset();
        }
    }

    [TearDown]
    public void ResetFastEndpointsResolver() => FastEndpointsTestResolver.Reset();
}
