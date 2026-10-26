namespace ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

internal static class KeycloakRbacDefinitions
{
    public const string ApiClientId = "thdarkorbit-api";

    public const string AdminRole = "emulator-admin";
    public const string OperatorRole = "emulator-operator";
    public const string ViewerRole = "emulator-viewer";

    public const string IdentityRead = "backoffice.identity.read";
    public const string IdentityWrite = "backoffice.identity.write";
    public const string IdentityDelete = "backoffice.identity.delete";
    public const string KeycloakRead = "backoffice.keycloak.read";
    public const string CacheWrite = "backoffice.cache.write";
    public const string OpsRead = "backoffice.ops.read";
    public const string OpsWrite = "backoffice.ops.write";

    public static IReadOnlyList<string> AllPermissions { get; } =
    [
        IdentityRead,
        IdentityWrite,
        IdentityDelete,
        KeycloakRead,
        CacheWrite,
        OpsRead,
        OpsWrite
    ];

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RealmRolePermissions { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [AdminRole] = AllPermissions,
            [OperatorRole] = [IdentityRead, IdentityWrite, KeycloakRead, CacheWrite, OpsRead, OpsWrite],
            [ViewerRole] = [IdentityRead, KeycloakRead, OpsRead]
        };

    public static string DefaultRealmRole => ViewerRole;
}
