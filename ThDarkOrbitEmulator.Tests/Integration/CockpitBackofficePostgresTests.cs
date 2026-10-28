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
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class CockpitBackofficePostgresTests
{
    private sealed class AdminAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "catalog-admin"), new Claim(ClaimTypes.Role, EmulatorRoles.Admin)],
                    "Test")), "Test")));
    }

    [Test]
    public async Task CockpitRoutes_RejectNullFieldsWithoutChangingPersistedCatalog()
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

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddSingleton<CockpitCatalogStore>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AdminAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(EmulatorRoles.Admin, policy => policy.RequireRole(EmulatorRoles.Admin)));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapCockpitCatalog();
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string prefix = "/api/v1/backoffice/cockpit";
            var servers = await client.GetFromJsonAsync<ClientServer[]>(prefix + "/servers", ct);
            var original = servers![0];
            var invalidHost = await client.PutAsJsonAsync(prefix + "/servers/" + original.Code,
                original with { Host = null! }, ct);
            var invalidIcon = await client.PutAsJsonAsync(prefix + "/ships/ship_phoenix",
                new CatalogShip("ship_phoenix", "Phoenix", 1, null!, true), ct);
            var invalidLocale = await client.PutAsJsonAsync(prefix + "/locale-overrides/en/ui/menu",
                new LocaleOverride("en", "ui", "menu", null!), ct);
            var valid = await client.PutAsJsonAsync(prefix + "/servers/" + original.Code,
                original with { InstanceName = "Catalog test instance" }, ct);
            var saved = (await client.GetFromJsonAsync<ClientServer[]>(prefix + "/servers", ct))!
                .Single(server => server.Code == original.Code);
            Assert.Multiple(() =>
            {
                Assert.That(invalidHost.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(invalidIcon.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(invalidLocale.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(saved.Host, Is.EqualTo(original.Host));
                Assert.That(saved.InstanceName, Is.EqualTo("Catalog test instance"));
            });
        }
        finally { await app.StopAsync(ct); }
    }
}
