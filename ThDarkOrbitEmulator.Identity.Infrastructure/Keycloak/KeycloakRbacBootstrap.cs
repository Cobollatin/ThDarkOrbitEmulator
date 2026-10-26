using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

internal sealed class KeycloakRbacBootstrap(
    HttpClient httpClient,
    string realmEndpoint,
    string adminToken,
    ILogger logger)
{
    private readonly string _clientsEndpoint = $"{realmEndpoint}/clients";
    private readonly string _rolesEndpoint = $"{realmEndpoint}/roles";

    public async Task EnsureRbacAsync(CancellationToken cancellationToken)
    {
        var clientInternalId = await FindClientInternalIdAsync(KeycloakRbacDefinitions.ApiClientId, cancellationToken);
        if (clientInternalId is null)
        {
            logger.LogWarning("Cannot bootstrap RBAC: client '{ClientId}' not found.", KeycloakRbacDefinitions.ApiClientId);
            return;
        }

        var permissionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var permission in KeycloakRbacDefinitions.AllPermissions)
        {
            var roleId = await EnsureClientRoleAsync(clientInternalId, permission, cancellationToken);
            if (roleId is not null)
            {
                permissionIds[permission] = roleId;
            }
        }

        foreach (var (realmRole, permissions) in KeycloakRbacDefinitions.RealmRolePermissions)
        {
            await EnsureRealmRoleAsync(realmRole, cancellationToken);
            var compositeIds = permissions
                .Where(permissionIds.ContainsKey)
                .Select(p => permissionIds[p])
                .ToList();
            if (compositeIds.Count > 0)
            {
                await SetRealmRoleCompositesAsync(realmRole, compositeIds, cancellationToken);
            }
        }

        logger.LogInformation("Keycloak RBAC roles and permissions are configured.");
    }

    public Task AssignDefaultRealmRoleToUserAsync(string userId, CancellationToken cancellationToken) =>
        AssignRealmRoleToUserAsync(userId, KeycloakRbacDefinitions.DefaultRealmRole, cancellationToken);

    public async Task AssignRealmRoleToUserAsync(string userId, string realmRole, CancellationToken cancellationToken)
    {
        using var getRole = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_rolesEndpoint}/{Uri.EscapeDataString(realmRole)}");
        getRole.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var roleResponse = await httpClient.SendAsync(getRole, cancellationToken);
        if (!roleResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Realm role '{Role}' was not found while assigning to user {UserId}.",
                realmRole,
                userId);
            return;
        }

        var roleJson = await roleResponse.Content.ReadAsStringAsync(cancellationToken);
        using var roleDoc = JsonDocument.Parse(roleJson);
        if (!roleDoc.RootElement.TryGetProperty("id", out var roleIdElement))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new[] { new { id = roleIdElement.GetString(), name = realmRole } });
        using var assignRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{realmEndpoint}/users/{userId}/role-mappings/realm");
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        assignRequest.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var assignResponse = await httpClient.SendAsync(assignRequest, cancellationToken);
        if (!assignResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Failed to assign realm role '{Role}' to user {UserId} (status {Status}).",
                realmRole,
                userId,
                (int)assignResponse.StatusCode);
        }
    }

    private async Task<string?> FindClientInternalIdAsync(string clientId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_clientsEndpoint}?clientId={Uri.EscapeDataString(clientId)}");
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

    private async Task<string?> EnsureClientRoleAsync(
        string clientInternalId,
        string roleName,
        CancellationToken cancellationToken)
    {
        var rolesEndpoint = $"{_clientsEndpoint}/{clientInternalId}/roles";
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{rolesEndpoint}/{Uri.EscapeDataString(roleName)}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        if (getResponse.IsSuccessStatusCode)
        {
            var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        var payload = JsonSerializer.Serialize(new { name = roleName });
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, rolesEndpoint);
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        createRequest.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode && createResponse.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            logger.LogWarning("Failed to create client role '{Role}' (status {Status}).", roleName, (int)createResponse.StatusCode);
            return null;
        }

        using var refetch = new HttpRequestMessage(HttpMethod.Get, $"{rolesEndpoint}/{Uri.EscapeDataString(roleName)}");
        refetch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var refetchResponse = await httpClient.SendAsync(refetch, cancellationToken);
        if (!refetchResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var roleJson = await refetchResponse.Content.ReadAsStringAsync(cancellationToken);
        using var roleDoc = JsonDocument.Parse(roleJson);
        return roleDoc.RootElement.TryGetProperty("id", out var roleId) ? roleId.GetString() : null;
    }

    private async Task EnsureRealmRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{_rolesEndpoint}/{Uri.EscapeDataString(roleName)}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        if (getResponse.IsSuccessStatusCode)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { name = roleName });
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, _rolesEndpoint);
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        createRequest.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (!createResponse.IsSuccessStatusCode && createResponse.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            logger.LogWarning("Failed to create realm role '{Role}' (status {Status}).", roleName, (int)createResponse.StatusCode);
        }
    }

    private async Task SetRealmRoleCompositesAsync(
        string realmRole,
        IReadOnlyList<string> clientRoleIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(clientRoleIds.Select(id => new { id, clientRole = true }));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_rolesEndpoint}/{Uri.EscapeDataString(realmRole)}/composites");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Failed to set composites for realm role '{Role}' (status {Status}).",
                realmRole,
                (int)response.StatusCode);
        }
    }
}
