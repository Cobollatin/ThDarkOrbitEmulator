using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class KeycloakService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<KeycloakAdminOptions> adminOptions,
    ILogger<KeycloakService> logger) : IKeycloakService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim();
        var (success, token, errorBody) = await TryRequestAccessTokenAsync(normalizedUsername, password, cancellationToken);
        if (success)
        {
            return token;
        }

        if (await TryRepairUserForLoginAsync(normalizedUsername, password, cancellationToken))
        {
            logger.LogInformation(
                "Repaired Keycloak credentials for '{Username}'; retrying token request.",
                normalizedUsername);
            (success, token, errorBody) = await TryRequestAccessTokenAsync(normalizedUsername, password, cancellationToken);
            if (success)
            {
                return token;
            }
        }

        logger.LogWarning(
            "Keycloak token request failed for user '{Username}': {ErrorBody}",
            normalizedUsername,
            errorBody);
        return null;
    }

    public async Task<bool> CreateUserAsync(
        string username,
        string password,
        string? email,
        CancellationToken cancellationToken,
        string? realmRole = null)
    {
        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.AdminUsername)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            logger.LogWarning("KeycloakAdmin is not configured; cannot create user '{Username}'.", username);
            return false;
        }

        var normalizedUsername = username.Trim();
        var httpClient = CreateClient();
        var adminToken = await RequestAdminTokenAsync(httpClient, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            logger.LogWarning(
                "Keycloak admin login failed for '{AdminUsername}' at {BaseUrl}. Users are created in realm '{Realm}'.",
                options.AdminUsername,
                options.BaseUrl,
                options.Realm);
            return false;
        }

        var usersEndpoint = $"{options.BaseUrl.TrimEnd('/')}/admin/realms/{options.Realm}/users";

        if (!await UserExistsAsync(httpClient, usersEndpoint, adminToken, normalizedUsername, cancellationToken))
        {
            var payload = BuildCreateUserPayload(normalizedUsername, email);
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, usersEndpoint);
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            createRequest.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
            if (!createResponse.IsSuccessStatusCode)
            {
                var errorBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Keycloak user create failed for '{Username}' in realm '{Realm}' ({Url}) with status {StatusCode}: {ErrorBody}",
                    normalizedUsername,
                    options.Realm,
                    usersEndpoint,
                    (int)createResponse.StatusCode,
                    errorBody);
                return false;
            }

            logger.LogInformation(
                "Created Keycloak user '{Username}' in realm '{Realm}'.",
                normalizedUsername,
                options.Realm);
        }
        else
        {
            logger.LogInformation(
                "Keycloak user '{Username}' already exists in realm '{Realm}'; refreshing credentials.",
                normalizedUsername,
                options.Realm);
        }

        if (!await PrepareUserCredentialsAsync(httpClient, usersEndpoint, adminToken, normalizedUsername, password, cancellationToken))
        {
            return false;
        }

        var (tokenOk, _, verifyError) = await TryRequestAccessTokenAsync(normalizedUsername, password, cancellationToken);
        if (!tokenOk)
        {
            logger.LogWarning(
                "Keycloak user '{Username}' was provisioned but password grant failed: {ErrorBody}",
                normalizedUsername,
                verifyError);
            return false;
        }

        var userId = await FindUserIdAsync(httpClient, usersEndpoint, adminToken, normalizedUsername, cancellationToken);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var role = string.IsNullOrWhiteSpace(realmRole)
                ? KeycloakRbacDefinitions.DefaultRealmRole
                : realmRole.Trim();
            await AssignRealmRoleInternalAsync(httpClient, options, adminToken, userId, role, cancellationToken);
        }

        logger.LogInformation(
            "Keycloak user '{Username}' can authenticate in realm '{Realm}'.",
            normalizedUsername,
            options.Realm);
        return true;
    }

    public async Task<bool> EnsureRealmRoleAsync(string username, string realmRole, CancellationToken cancellationToken)
    {
        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.AdminUsername)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return false;
        }

        var httpClient = CreateClient();
        var adminToken = await RequestAdminTokenAsync(httpClient, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return false;
        }

        var usersEndpoint = $"{options.BaseUrl.TrimEnd('/')}/admin/realms/{options.Realm}/users";
        var userId = await FindUserIdAsync(httpClient, usersEndpoint, adminToken, username.Trim(), cancellationToken);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        await AssignRealmRoleInternalAsync(httpClient, options, adminToken, userId, realmRole.Trim(), cancellationToken);
        return true;
    }

    private async Task AssignRealmRoleInternalAsync(
        HttpClient httpClient,
        KeycloakAdminOptions options,
        string adminToken,
        string userId,
        string realmRole,
        CancellationToken cancellationToken)
    {
        var realmEndpoint = $"{options.BaseUrl.TrimEnd('/')}/admin/realms/{options.Realm}";
        var rbac = new KeycloakRbacBootstrap(httpClient, realmEndpoint, adminToken, logger);
        await rbac.AssignRealmRoleToUserAsync(userId, realmRole, cancellationToken);
    }

    private async Task<(bool Success, string? Token, string ErrorBody)> TryRequestAccessTokenAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
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

        var httpClient = CreateClient();
        using var tokenResponse = await httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        if (tokenResponse.IsSuccessStatusCode)
        {
            await using var stream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var token = document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
                ? accessTokenElement.GetString()
                : null;
            return (token is not null, token, string.Empty);
        }

        var errorBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        return (false, null, errorBody);
    }

    private async Task<bool> TryRepairUserForLoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var options = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.AdminUsername)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return false;
        }

        var httpClient = CreateClient();
        var adminToken = await RequestAdminTokenAsync(httpClient, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return false;
        }

        var usersEndpoint = $"{options.BaseUrl.TrimEnd('/')}/admin/realms/{options.Realm}/users";
        if (!await UserExistsAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken))
        {
            return false;
        }

        return await PrepareUserCredentialsAsync(httpClient, usersEndpoint, adminToken, username, password, cancellationToken);
    }

    private static readonly string[] KnownRequiredActions =
    [
        "UPDATE_PASSWORD",
        "VERIFY_EMAIL",
        "CONFIGURE_TOTP",
        "UPDATE_PROFILE",
        "VERIFY_PROFILE",
        "CONFIGURE_RECOVERY_AUTHN_CODES"
    ];

    private async Task<bool> PrepareUserCredentialsAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (!await SetUserPasswordAsync(httpClient, usersEndpoint, adminToken, username, password, cancellationToken))
        {
            return false;
        }

        if (!await ClearRequiredActionsAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken))
        {
            return false;
        }

        return await FinalizeUserAccountAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken);
    }

    private static Dictionary<string, object> BuildCreateUserPayload(string username, string? email)
    {
        var payload = new Dictionary<string, object>
        {
            ["username"] = username,
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["requiredActions"] = Array.Empty<string>()
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            payload["email"] = email.Trim();
        }

        return payload;
    }

    private async Task<string?> RequestAdminTokenAsync(
        HttpClient httpClient,
        KeycloakAdminOptions options,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"{options.BaseUrl.TrimEnd('/')}/realms/master/protocol/openid-connect/token";
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", options.AdminUsername),
            new("password", options.AdminPassword)
        };

        using var tokenResponse = await httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Keycloak admin token request failed with status {StatusCode}: {ErrorBody}",
                (int)tokenResponse.StatusCode,
                errorBody);
            return null;
        }

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
    }

    private async Task<bool> SetUserPasswordAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var userId = await FindUserIdAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken);
        if (userId is null)
        {
            logger.LogWarning("Keycloak user '{Username}' was not found for password reset.", username);
            return false;
        }

        var resetEndpoint = $"{usersEndpoint}/{userId}/reset-password";
        var payload = new
        {
            type = "password",
            value = password,
            temporary = false
        };

        using var resetRequest = new HttpRequestMessage(HttpMethod.Put, resetEndpoint);
        resetRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        resetRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resetResponse = await httpClient.SendAsync(resetRequest, cancellationToken);
        if (!resetResponse.IsSuccessStatusCode)
        {
            var errorBody = await resetResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Keycloak password reset failed for '{Username}' with status {StatusCode}: {ErrorBody}",
                username,
                (int)resetResponse.StatusCode,
                errorBody);
            return false;
        }

        logger.LogInformation("Updated Keycloak password for user '{Username}'.", username);
        return true;
    }

    private async Task<bool> ClearRequiredActionsAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        CancellationToken cancellationToken)
    {
        var userId = await FindUserIdAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken);
        if (userId is null)
        {
            return false;
        }

        var actionsToClear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in KnownRequiredActions)
        {
            actionsToClear.Add(known);
        }

        using (var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{usersEndpoint}/{userId}"))
        {
            getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
            if (getResponse.IsSuccessStatusCode)
            {
                var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("requiredActions", out var requiredActions)
                    && requiredActions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var action in requiredActions.EnumerateArray())
                    {
                        if (action.ValueKind == JsonValueKind.String)
                        {
                            actionsToClear.Add(action.GetString()!);
                        }
                    }
                }
            }
        }

        foreach (var action in actionsToClear)
        {
            using var deleteRequest = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{usersEndpoint}/{userId}/required-actions/{Uri.EscapeDataString(action)}");
            deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var deleteResponse = await httpClient.SendAsync(deleteRequest, cancellationToken);
            if (deleteResponse.IsSuccessStatusCode)
            {
                logger.LogDebug("Removed required action '{Action}' for user '{Username}'.", action, username);
            }
            else
            {
                logger.LogDebug(
                    "Could not delete required action '{Action}' for '{Username}' (status {StatusCode}).",
                    action,
                    username,
                    (int)deleteResponse.StatusCode);
            }
        }

        var remaining = await GetRequiredActionsAsync(httpClient, usersEndpoint, adminToken, userId, cancellationToken);
        if (remaining.Count > 0)
        {
            logger.LogWarning(
                "Keycloak user '{Username}' still has required actions after cleanup: {Actions}",
                username,
                string.Join(", ", remaining));
            return false;
        }

        return true;
    }

    private static async Task<List<string>> GetRequiredActionsAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string userId,
        CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{usersEndpoint}/{userId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            return [];
        }

        var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("requiredActions", out var requiredActions)
            || requiredActions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var action in requiredActions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.String)
            {
                var value = action.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private async Task<bool> FinalizeUserAccountAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        CancellationToken cancellationToken)
    {
        var userId = await FindUserIdAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken);
        if (userId is null)
        {
            logger.LogWarning("Keycloak user '{Username}' was not found while finalizing account.", username);
            return false;
        }

        var payload = new
        {
            id = userId,
            username,
            enabled = true,
            emailVerified = true,
            requiredActions = Array.Empty<string>()
        };

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"{usersEndpoint}/{userId}");
        putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        putRequest.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var putResponse = await httpClient.SendAsync(putRequest, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            var errorBody = await putResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Keycloak finalize user failed for '{Username}' with status {StatusCode}: {ErrorBody}",
                username,
                (int)putResponse.StatusCode,
                errorBody);
            return false;
        }

        logger.LogInformation("Keycloak user '{Username}' is enabled with no required actions.", username);
        return true;
    }

    private static async Task<bool> UserExistsAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        CancellationToken cancellationToken)
        => await FindUserIdAsync(httpClient, usersEndpoint, adminToken, username, cancellationToken) is not null;

    private static async Task<string?> FindUserIdAsync(
        HttpClient httpClient,
        string usersEndpoint,
        string adminToken,
        string username,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{usersEndpoint}?username={Uri.EscapeDataString(username)}&exact=true");
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

    private HttpClient CreateClient() => httpClientFactory.CreateClient(KeycloakHttp.ClientName);
}
