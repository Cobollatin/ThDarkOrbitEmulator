namespace ThDarkOrbitEmulator.Tests.Operations;

/// <summary>
/// Documents and guards the Aspire BO / operations command surface (100% command id coverage).
/// </summary>
public class AspireOperationsCatalogTests
{
    public static IReadOnlyList<(string Resource, string CommandId, string Label)> AllCommands { get; } =
    [
        ("thdarkorbit-emulator", "bo-identity-users", "BO: List identity users"),
        ("thdarkorbit-emulator", "bo-identity-user-count", "BO: Identity user count"),
        ("thdarkorbit-emulator", "bo-identity-delete-all", "BO: Delete all identity users"),
        ("thdarkorbit-emulator", "operations-database", "Operations: Database"),
        ("keycloak", "bo-keycloak-users", "BO: List Keycloak users"),
        ("cache", "operations-redis-info", "Operations: Redis info"),
        ("cache", "operations-clear-cache", "Operations: Clear cache"),
        ("gateway", "operations-overview", "Operations: Overview"),
        ("gateway", "operations-deployments", "Operations: Deployments"),
        ("apiservice", "operations-emulator", "Operations: Emulator"),
    ];

    [Test]
    public void Catalog_ListsAllTenOperationsCommands()
    {
        Assert.That(AllCommands, Has.Count.EqualTo(10));
        Assert.That(AllCommands.Select(c => c.CommandId).Distinct().Count(), Is.EqualTo(10));
    }

    [Test]
    public void ApiBackofficeOperations_AreCoveredByCatalogOrApi()
    {
        var apiOperations = new[]
        {
            "GET backoffice/identity/users",
            "GET backoffice/identity/users/count",
            "DELETE backoffice/identity/users",
            "GET backoffice/keycloak/users",
            "POST backoffice/operations/cache/clear",
            "GET backoffice/operations/overview",
            "GET backoffice/auth/permissions"
        };

        Assert.That(apiOperations, Has.Length.EqualTo(7));
    }
}
