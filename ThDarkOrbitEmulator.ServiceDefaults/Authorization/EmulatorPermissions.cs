namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

/// <summary>
/// Fine-grained permissions (Keycloak client roles on <c>thdarkorbit-api</c>).
/// Composite realm roles map to these permissions — similar to Azure RBAC custom roles.
/// </summary>
public static class EmulatorPermissions
{
    public const string PolicyPrefix = "Permission:";

    public const string IdentityRead = "backoffice.identity.read";
    public const string IdentityWrite = "backoffice.identity.write";
    public const string IdentityDelete = "backoffice.identity.delete";
    public const string KeycloakRead = "backoffice.keycloak.read";
    public const string CacheWrite = "backoffice.cache.write";
    public const string OpsRead = "backoffice.ops.read";
    public const string OpsWrite = "backoffice.ops.write";

    public static IReadOnlyList<string> All { get; } =
    [
        IdentityRead,
        IdentityWrite,
        IdentityDelete,
        KeycloakRead,
        CacheWrite,
        OpsRead,
        OpsWrite
    ];

    public static string PolicyName(string permission) => PolicyPrefix + permission;
}
