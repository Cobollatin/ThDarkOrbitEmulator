using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldPortalOperationEndpointTests
{
    private sealed class AdminAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Test-Admin")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Role, EmulatorRoles.Admin)], "Test")), "Test"))
                : AuthenticateResult.NoResult());
    }

    [Test]
    public async Task AdminPortalOverride_ChangesSharedWorldMapAndRejectsUnknownPortals()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<WorldPortalOperations>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AdminAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(EmulatorRoles.Admin,
            policy => policy.RequireRole(EmulatorRoles.Admin)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorldPortalOperations();
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string route = "/api/v1/backoffice/world/maps/1/portals";
            Assert.That((await client.GetAsync(route, ct)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            client.DefaultRequestHeaders.Add("X-Test-Admin", "yes");
            Assert.That((await client.PutAsJsonAsync(route + "/1", new PortalOperationOverride(false), ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.GetFromJsonAsync<WorldPortal[]>(route, ct))!.Single().Working, Is.False);
            var replica = new WorldPortalOperations(app.Services.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
            Assert.That((await replica.MapAsync(1, ct)).AllowsPortalRepair, Is.False);
            Assert.That((await client.PutAsJsonAsync(route + "/999", new PortalOperationOverride(true), ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.GetAsync("/api/v1/backoffice/world/maps/99/portals", ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.PutAsJsonAsync(route + "/1", new PortalOperationOverride(true), ct)).StatusCode,
                Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await replica.MapAsync(1, ct)).AllowsPortalRepair, Is.True);
        }
        finally { await app.StopAsync(ct); }
    }
}
