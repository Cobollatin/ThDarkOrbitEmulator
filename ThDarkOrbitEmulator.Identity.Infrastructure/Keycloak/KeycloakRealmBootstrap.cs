using System.Reflection;

namespace ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

internal static class KeycloakRealmBootstrap
{
    private const string ResourceName = "ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak.thdarkorbit-realm.json";

    public static string LoadEmbeddedRealmJson()
    {
        var assembly = typeof(KeycloakRealmBootstrap).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded realm resource '{ResourceName}' was not found. Rebuild {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
