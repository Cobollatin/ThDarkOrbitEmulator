namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

/// <summary>
/// Keycloak realm roles (composite roles bundle client permissions).
/// </summary>
public static class EmulatorRoles
{
    public const string Admin = "emulator-admin";
    public const string Operator = "emulator-operator";
    public const string Viewer = "emulator-viewer";

    public static IReadOnlyList<string> All { get; } = [Admin, Operator, Viewer];
}
