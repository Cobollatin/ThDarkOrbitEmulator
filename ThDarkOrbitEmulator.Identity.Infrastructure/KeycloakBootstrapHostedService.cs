using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

/// <summary>
/// Creates the thdarkorbit realm via Admin API when missing, then ensures the ROPC client and required actions.
/// </summary>
public sealed class KeycloakBootstrapHostedService(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> adminOptions,
    ILogger<KeycloakBootstrapHostedService> logger) : IHostedService
{
    private const string ApiClientId = "thdarkorbit-api";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.AdminUsername)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            logger.LogWarning("Keycloak admin is not configured; skipping API client bootstrap.");
            return;
        }

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (await TryBootstrapAsync(options, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Keycloak bootstrap attempt {Attempt} failed; retrying.", attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        logger.LogWarning("Keycloak API client bootstrap did not complete after {Attempts} attempts.", maxAttempts);
    }

    private async Task<bool> TryBootstrapAsync(KeycloakAdminOptions options, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(KeycloakHttp.ClientName);
        var adminToken = await RequestAdminTokenAsync(httpClient, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return false;
        }

        var realm = options.Realm;
        var realmsRoot = $"{options.BaseUrl.TrimEnd('/')}/admin/realms";
        var realmEndpoint = $"{realmsRoot}/{realm}";

        if (!await EnsureRealmExistsAsync(httpClient, realmsRoot, realmEndpoint, realm, adminToken, cancellationToken))
        {
            return false;
        }

        var clientsEndpoint = $"{realmEndpoint}/clients";
        var existingId = await FindClientInternalIdAsync(httpClient, clientsEndpoint, adminToken, cancellationToken);

        if (existingId is null)
        {
            await CreateApiClientAsync(httpClient, clientsEndpoint, adminToken, cancellationToken);
            logger.LogInformation("Created Keycloak client '{ClientId}'.", ApiClientId);
        }
        else
        {
            await EnsureDirectAccessGrantsAsync(httpClient, clientsEndpoint, existingId, adminToken, cancellationToken);
            logger.LogInformation("Keycloak client '{ClientId}' is configured for direct access grants.", ApiClientId);
        }

        await DisableRealmDefaultRequiredActionsAsync(httpClient, realmEndpoint, adminToken, cancellationToken);

        var rbac = new KeycloakRbacBootstrap(httpClient, realmEndpoint, adminToken, logger);
        await rbac.EnsureRbacAsync(cancellationToken);
        return true;
    }

    private async Task DisableRealmDefaultRequiredActionsAsync(
        HttpClient httpClient,
        string realmEndpoint,
        string adminToken,
        CancellationToken cancellationToken)
    {
        var actionsEndpoint = $"{realmEndpoint}/authentication/required-actions";
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, actionsEndpoint);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Could not read Keycloak required actions (status {StatusCode}).",
                (int)getResponse.StatusCode);
            return;
        }

        var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var actionElement in document.RootElement.EnumerateArray())
        {
            if (!actionElement.TryGetProperty("alias", out var aliasElement))
            {
                continue;
            }

            var alias = aliasElement.GetString();
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            var node = JsonNode.Parse(actionElement.GetRawText()) as JsonObject;
            if (node is null)
            {
                continue;
            }

            node["enabled"] = false;
            node["defaultAction"] = false;

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"{actionsEndpoint}/{Uri.EscapeDataString(alias)}");
            putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            putRequest.Content = JsonContent.Create(node);
            using var putResponse = await httpClient.SendAsync(putRequest, cancellationToken);
            if (!putResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to disable required action '{Action}' (status {StatusCode}).",
                    alias,
                    (int)putResponse.StatusCode);
            }
        }

        logger.LogInformation("Disabled default Keycloak required actions for realm.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> EnsureRealmExistsAsync(
        HttpClient httpClient,
        string realmsRoot,
        string realmEndpoint,
        string realm,
        string adminToken,
        CancellationToken cancellationToken)
    {
        using (var realmRequest = new HttpRequestMessage(HttpMethod.Get, realmEndpoint))
        {
            realmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var realmResponse = await httpClient.SendAsync(realmRequest, cancellationToken);
            if (realmResponse.IsSuccessStatusCode)
            {
                return true;
            }

            if (realmResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning(
                    "Unexpected status {StatusCode} reading Keycloak realm '{Realm}'.",
                    (int)realmResponse.StatusCode,
                    realm);
                return false;
            }
        }

        var realmJson = KeycloakRealmBootstrap.LoadEmbeddedRealmJson();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, realmsRoot);
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        createRequest.Content = new StringContent(realmJson, Encoding.UTF8, "application/json");

        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (createResponse.IsSuccessStatusCode)
        {
            logger.LogInformation("Created Keycloak realm '{Realm}' via Admin API.", realm);
            return true;
        }

        if (createResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger.LogInformation("Keycloak realm '{Realm}' already exists (created concurrently).", realm);
            return true;
        }

        var body = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "Failed to create Keycloak realm '{Realm}' (status {StatusCode}): {Body}",
            realm,
            (int)createResponse.StatusCode,
            body);
        return false;
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

    private static async Task<string?> FindClientInternalIdAsync(
        HttpClient httpClient,
        string clientsEndpoint,
        string adminToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{clientsEndpoint}?clientId={ApiClientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        return document.RootElement[0].TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : null;
    }

    private static async Task CreateApiClientAsync(
        HttpClient httpClient,
        string clientsEndpoint,
        string adminToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            clientId = ApiClientId,
            name = "ThDarkOrbit API",
            enabled = true,
            publicClient = true,
            directAccessGrantsEnabled = true,
            standardFlowEnabled = true,
            protocol = "openid-connect",
            fullScopeAllowed = true,
            redirectUris = new[] { "*" },
            webOrigins = new[] { "+" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, clientsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = JsonContent.Create(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task EnsureDirectAccessGrantsAsync(
        HttpClient httpClient,
        string clientsEndpoint,
        string clientInternalId,
        string adminToken,
        CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{clientsEndpoint}/{clientInternalId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Unexpected Keycloak client representation.");

        node["directAccessGrantsEnabled"] = true;
        node["enabled"] = true;
        node["publicClient"] = true;
        node["serviceAccountsEnabled"] = false;
        node.Remove("secret");

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"{clientsEndpoint}/{clientInternalId}");
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        updateRequest.Content = JsonContent.Create(node);
        using var updateResponse = await httpClient.SendAsync(updateRequest, cancellationToken);
        updateResponse.EnsureSuccessStatusCode();
    }
}
