using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class KeycloakService(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IKeycloakService
{
    public async Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken)
    {
        var authority = configuration["Keycloak:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException("Keycloak authority is not configured.");
        }

        var clientId = configuration["KeycloakClient:ClientId"] ?? configuration["Keycloak:Audience"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Keycloak client id is not configured.");
        }

        var tokenEndpoint = $"{authority.TrimEnd('/')}/protocol/openid-connect/token";

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "password"),
            new("client_id", clientId),
            new("username", username),
            new("password", password),
            new("scope", "openid profile email")
        };

        var clientSecret = configuration["KeycloakClient:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        }

        var httpClient = httpClientFactory.CreateClient();
        using var tokenResponse = await httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
    }
}
