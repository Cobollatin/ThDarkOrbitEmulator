using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;

namespace ThDarkOrbitEmulator.Identity.Infrastructure.Backoffice;

internal sealed class BackofficeKeycloakService(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> adminOptions) : IBackofficeKeycloakService
{
    public async Task<IReadOnlyList<BackofficeKeycloakUserRow>> ListUsersAsync(int limit, CancellationToken cancellationToken)
    {
        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.AdminUsername)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return [];
        }

        var httpClient = httpClientFactory.CreateClient(KeycloakHttp.ClientName);
        var adminToken = await RequestAdminTokenAsync(httpClient, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return [];
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.BaseUrl.TrimEnd('/')}/admin/realms/{options.Realm}/users?max={limit}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseUsers(json);
    }

    private static IReadOnlyList<BackofficeKeycloakUserRow> ParseUsers(string json)
    {
        var rows = new List<BackofficeKeycloakUserRow>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var user in document.RootElement.EnumerateArray())
        {
            var id = user.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var username = user.TryGetProperty("username", out var uEl) ? uEl.GetString() ?? "" : "";
            var email = user.TryGetProperty("email", out var eEl) ? eEl.GetString() ?? "" : "";
            var enabled = user.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
            rows.Add(new BackofficeKeycloakUserRow(id, username, email, enabled));
        }

        return rows;
    }

    private static async Task<string?> RequestAdminTokenAsync(
        HttpClient httpClient,
        KeycloakAdminOptions options,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"{options.BaseUrl.TrimEnd('/')}/realms/master/protocol/openid-connect/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = options.AdminUsername,
            ["password"] = options.AdminPassword
        };

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
