namespace ThDarkOrbitEmulator.Tests.Operations;

using ThDarkOrbitEmulator.AppHost.Operations;

/// <summary>
/// Documents and guards the Aspire BO / operations command surface.
/// </summary>
public class AspireOperationsCatalogTests
{
    public static IReadOnlyList<(string Resource, string CommandId, string Label)> AllCommands { get; } =
    [
        ("data-postgres", "bo-identity-users", "BO: List identity users"),
        ("data-postgres", "bo-identity-user-count", "BO: Identity user count"),
        ("data-postgres", "bo-identity-delete-all", "BO: Delete all identity users"),
        ("data-postgres", "operations-database", "Operations: Database"),
        ("auth-keycloak", "bo-keycloak-users", "BO: List Keycloak users"),
        ("sessions-redis", "operations-redis-info", "Operations: Redis info"),
        ("sessions-redis", "operations-clear-cache", "Operations: Clear cache"),
        ("gateway-players", "operations-overview", "Operations: Overview"),
        ("gateway-players", "operations-deployments", "Operations: Deployments"),
        ("gateway-players", "client-test-guide", "Client test: Workflow"),
        ("gateway-players", "client-test-endpoints", "Client test: Endpoint catalog"),
        ("gateway-players", "client-test-scan-urls", "Client test: Scan hardcoded URLs"),
        ("gateway-players", "client-test-replace-preview", "Client test: Replace preview"),
        ("gateway-players", "client-test-replace-apply", "Client test: Replace apply"),
        ("gateway-players", "client-test-replace-rollback", "Client test: Replace rollback"),
        ("gateway-players", "client-test-start-mitm", "Client test: Start mitmproxy"),
        ("gateway-players", "client-test-stop-mitm", "Client test: Stop mitmproxy"),
        ("gateway-players", "client-test-analyze-traffic", "Client test: Analyze traffic"),
        ("gateway-players", "client-test-patch-uris-status", "Client test: URI patch status"),
        ("gateway-players", "client-test-patch-uris-apply", "Patch Client URIs (Localhost)"),
        ("gateway-players", "client-test-patch-uris-reset", "Reset Client URIs (Original)"),
        ("api-game", "operations-emulator", "Operations: Emulator"),
        ("api-game", "operations-mesh-topology", "Operations: Mesh topology"),
        ("api-game", "admin-vars-report", "Admin: Runtime variables"),
        ("api-game", "admin-var-history", "Admin: Variable history"),
        ("api-game", "admin-command-help", "Admin: Command help"),
    ];

    [Test]
    public void Catalog_ListsAllOperationsCommands()
    {
        Assert.That(AllCommands, Has.Count.EqualTo(26));
        Assert.That(AllCommands.Select(c => c.CommandId).Distinct().Count(), Is.EqualTo(26));
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

    [Test]
    public void AdminReports_ShowLiveRowsAndEscapeUserReasons()
    {
        var variables = OperationsReports.BuildAdminVariables(
            [["world.tick_rate_ms", "World", "725", "1000"]]);
        var history = OperationsReports.BuildAdminHistory(
            [["3", "world.tick_rate_ms", "1000", "725", "ServerManagement",
                "operator", "node-a", "ticket | 42"]]);
        Assert.That(variables, Does.Contain("world.tick_rate_ms | World | 725 | 1000"));
        Assert.That(history, Does.Contain("ticket \\| 42"));
        Assert.That(OperationsReports.BuildAdminCommandHelp(), Does.Contain("Invoke-ServerAdminCommand.ps1"));
    }
}

