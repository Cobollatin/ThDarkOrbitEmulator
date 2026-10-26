using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class EconomyAdminEndpointTests
{
    [Test]
    public async Task EconomyRoutes_HaveOpenApiSummaries()
    {
        await using var app = await StartAppAsync();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/economy", StringComparison.Ordinal) == true).ToArray();
        Assert.That(endpoints, Has.Length.GreaterThanOrEqualTo(20));
        Assert.That(endpoints.All(endpoint => !string.IsNullOrWhiteSpace(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary)), Is.True);
    }

    [TestCase("PUT", "/api/v1/economy/assets/test", "{\"id\":\"test\",\"kind\":\"cash\",\"name\":\"Test\",\"enabled\":true}")]
    [TestCase("PUT", "/api/v1/economy/items/test", "{\"id\":\"test\",\"name\":\"Test\",\"type\":\"laser\",\"rarity\":\"common\",\"statsJson\":\"[]\",\"enabled\":true}")]
    [TestCase("PUT", "/api/v1/economy/recipes/test", "{\"id\":\"test\",\"name\":\"Test\",\"inputsJson\":\"[]\",\"outputsJson\":\"[]\",\"craftSeconds\":0,\"enabled\":true}")]
    [TestCase("PUT", "/api/v1/economy/listings/test", "{\"id\":\"test\",\"itemId\":\"test\",\"pricesJson\":\"{\\\"credits\\\":-1}\",\"enabled\":true}")]
    [TestCase("POST", "/api/v1/economy/listings/test/stock", "{\"delta\":0,\"reason\":\"\"}")]
    public async Task EconomyRoutes_RejectInvalidInputs(string method, string path, string json)
    {
        await using var app = await StartAppAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-Test-Role", EmulatorRoles.Admin);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("error"));
    }

    [TestCase("/api/v1/economy/listings/test/stock-audit?limit=0")]
    [TestCase("/api/v1/economy/listings/test/stock-audit?beforeId=-1")]
    [TestCase("/api/v1/economy/accounts/00000000-0000-0000-0000-000000000001/ledger?limit=201")]
    public async Task AuditRoutes_RejectInvalidPaging(string path)
    {
        await using var app = await StartAppAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-Test-Role", EmulatorRoles.Admin);
        using var response = await client.GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase(null, HttpStatusCode.Unauthorized)]
    [TestCase("emulator-viewer", HttpStatusCode.Forbidden)]
    [TestCase("emulator-operator", HttpStatusCode.Forbidden)]
    [TestCase("backoffice.identity.write", HttpStatusCode.Forbidden)]
    public async Task EconomyRoutes_RejectNonAdmins(string? role, HttpStatusCode expected)
    {
        await using var app = await StartAppAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);

        foreach (var path in new[] { "/api/v1/economy/assets", "/api/v1/economy/items",
            "/api/v1/economy/recipes", "/api/v1/economy/listings",
            "/api/v1/economy/accounts/00000000-0000-0000-0000-000000000001/ledger" })
        {
            using var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(expected), path);
        }
        foreach (var (method, path) in new[] {
            (HttpMethod.Put, "/api/v1/economy/items/test"),
            (HttpMethod.Delete, "/api/v1/economy/items/test"),
            (HttpMethod.Post, "/api/v1/economy/listings/test/stock") })
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Delete) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(expected), $"{method} {path}");
        }
    }

    private static async Task<WebApplication> StartAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<EconomyStore>();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddEmulatorAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEconomyAdmin();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
