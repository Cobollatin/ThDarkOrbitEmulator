namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class KeycloakAdminOptions
{
    public const string SectionName = "KeycloakAdmin";

    public string BaseUrl { get; set; } = string.Empty;

    public string Realm { get; set; } = "thdarkorbit";

    public string AdminUsername { get; set; } = string.Empty;

    public string AdminPassword { get; set; } = string.Empty;
}
