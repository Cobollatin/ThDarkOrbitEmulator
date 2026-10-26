using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class PlatformOwnerSeedOptions
{
    public const string SectionName = "PlatformOwnerSeed";

    public string Username { get; set; } = "owner";

    public string Password { get; set; } = "owner";

    public string Email { get; set; } = "owner@thdarkorbit.local";

    public string RealmRole { get; set; } = KeycloakRbacDefinitions.AdminRole;

    public string AdminUsername { get; set; } = "admin";

    public string AdminPassword { get; set; } = "Admin123!";

    public string AdminEmail { get; set; } = "admin@darkorbit.local";

    public string DefaultCompany { get; set; } = "mmo";

    public bool SeedRichStartingState { get; set; } = true;
}