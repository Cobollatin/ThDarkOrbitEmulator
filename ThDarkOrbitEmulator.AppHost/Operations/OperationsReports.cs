using ThDarkOrbitEmulator.AppHost.Dashboard;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class OperationsReports
{
    private static IEnumerable<IReadOnlyList<string>> SafeRows(IReadOnlyList<IReadOnlyList<string>> rows) =>
        rows.Select(row => (IReadOnlyList<string>)row.Select(cell => cell.Replace("|", "\\|")
            .Replace('\r', ' ').Replace('\n', ' ')).ToArray());

    public static string BuildAdminVariables(IReadOnlyList<IReadOnlyList<string>> rows) => MarkdownReportBuilder.Build("Runtime variables", sb =>
    {
        if (rows.Count == 0) sb.AppendLine("No overrides have been recorded. Use the CLI for the full variable catalogue.");
        else MarkdownReportBuilder.WriteTable(sb, ["Key", "Category", "Current", "Default"], SafeRows(rows));
        sb.AppendLine("Use `var get <key>`, `var set <key> <value> [reason]` and `var reset <key> [reason]`.");
        sb.AppendLine("The authenticated API is `/api/v1/backoffice/runtime-vars`.");
    });

    public static string BuildAdminHistory(IReadOnlyList<IReadOnlyList<string>> rows) => MarkdownReportBuilder.Build("Runtime variable history", sb =>
    {
        if (rows.Count == 0) sb.AppendLine("No variable changes recorded.");
        else MarkdownReportBuilder.WriteTable(sb,
            ["ID", "Key", "Previous", "New", "Surface", "Actor", "Node", "Reason"], SafeRows(rows));
        sb.AppendLine("Run `scripts/admin/Invoke-ServerAdminCommand.ps1 -CommandText 'var history [key] [limit]'`.");
        sb.AppendLine("Rollback appends a new attributed change: `var rollback <changeId> [reason]`.");
        sb.AppendLine("The authenticated API is `/api/v1/backoffice/runtime-vars/history`.");
    });

    public static string BuildAdminCommandHelp() => MarkdownReportBuilder.Build("Admin commands", sb =>
    {
        sb.AppendLine("Run `scripts/admin/Invoke-ServerAdminCommand.ps1 -CommandText 'help'` for current syntax.");
        sb.AppendLine("The CLI sends commands to `/api/v1/backoffice/admin/command` with ServerManagement attribution.");
        sb.AppendLine("Set `THDARKORBIT_ADMIN_TOKEN` in the operator's current process before invoking the CLI.");
    });

    public static string BuildMeshTopology(int desiredReplicas) =>
        MarkdownReportBuilder.Build("Game mesh topology", sb =>
        {
            sb.AppendLine($"Configured `api-game` replicas: **{desiredReplicas}** (allowed 1–16).");
            sb.AppendLine();
            sb.AppendLine("`gateway-players` uses Aspire service discovery for HTTP destinations. Each API replica binds an available loopback world TCP port when multiple replicas are configured.");
            sb.AppendLine();
            sb.AppendLine("This report shows desired configuration. Live node registration, Redis map leases, and pilot presence are available through `mesh status` in the Server Management CLI or `/api/v1/backoffice/mesh/topology`. Native Unity map handoff and cross-node observer delivery still require direct client verification. Inspect per-instance health and telemetry in the Aspire Dashboard.");
        });

    public static string BuildOverview() =>
        MarkdownReportBuilder.Build("Operations overview", sb =>
        {
            sb.AppendLine("Local development stack for **ThDarkOrbit Emulator**. Use resource commands on each row’s resource for detail.");
            sb.AppendLine();
            sb.AppendLine("Resource IDs follow **`{purpose}-{technology}`** — see `docs/aspire-dashboard-naming.md`.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Resource", "Role", "Status"],
                [
                    [DashboardResourceNames.ApiGame, "Game + identity API (FastEndpoints)", "Expected: healthy at `/health`"],
                    [DashboardResourceNames.GatewayPlayers, "YARP reverse proxy (game client + API)", "Expected: healthy at `/health`"],
                    [DashboardResourceNames.DataPostgres, "PostgreSQL 16 (identity + Keycloak schemas)", "Expected: running"],
                    [DashboardResourceNames.SessionsRedis, "Redis (game sessions)", "Expected: running"],
                    [DashboardResourceNames.AuthKeycloak, "Authentication (realm `thdarkorbit`)", "Expected: running on :8081"],
                    [DashboardResourceNames.CmsPlayers, "Player CMS (Angular)", "Expected: running on :4300"],
                    [DashboardResourceNames.DashboardsGrafana, "Metrics dashboards", "Expected: :13000"],
                    [DashboardResourceNames.MetricsPrometheus, "Metrics storage", "Expected: :19090"],
                    [DashboardResourceNames.SessionsRedisInsight, "Redis admin UI (linked to sessions-redis)", "Expected: :5540 via Aspire"],
                    [DashboardResourceNames.TelemetryOtel, "OTLP fan-out (metrics + Aspire traces/logs)", "Expected: :14318 HTTP"],
                ]);
            MarkdownReportBuilder.WriteLinks(sb, "Quick links",
            [
                ("Aspire dashboard", DevOperationsUrls.AspireDashboard),
                ("pgAdmin", DevOperationsUrls.PgAdmin),
                ("Grafana", DevOperationsUrls.Grafana),
                ("Prometheus", DevOperationsUrls.Prometheus),
                ("Keycloak admin", DevOperationsUrls.KeycloakAdmin),
                ("Player CMS", DevOperationsUrls.Cms),
                ($"RedisInsight (Aspire → {DashboardResourceNames.SessionsRedis})", DevOperationsUrls.AspireDashboard),
            ]);
        });

    public static string BuildDeployments() =>
        MarkdownReportBuilder.Build("Deployments (AppHost resources)", sb =>
        {
            sb.AppendLine("Resources registered in `AppHost.cs`. Status reflects local Aspire run mode, not production.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Name", "Type", "Notes"],
                [
                    [DashboardResourceNames.ApiGame, ".NET project", "FastEndpoints, identity, game routes"],
                    [DashboardResourceNames.GatewayPlayers, ".NET project", "YARP; game + `/api` proxy"],
                    [DashboardResourceNames.CmsPlayers, "JavaScript (Angular)", "Port 4300, references gateway-players"],
                    [DashboardResourceNames.DataPostgres, "PostgreSQL", "DB `thdarkorbit`; schemas `identity`, `keycloak`"],
                    [DashboardResourceNames.AppDatabase, "PostgreSQL database", "Logical DB `thdarkorbit`"],
                    [DashboardResourceNames.SessionsRedis, "Redis", "Session cache"],
                    [DashboardResourceNames.SessionsRedisInsight, "RedisInsight", "Cache browser UI"],
                    [DashboardResourceNames.AuthKeycloak, "Container", "Realm `thdarkorbit` via ApiService Admin API bootstrap"],
                    ["pgadmin (child of data-postgres)", "Container (via WithPgAdmin)", "Postgres UI"],
                    [DashboardResourceNames.DashboardsGrafana, "Container", "Dashboards"],
                    [DashboardResourceNames.MetricsPrometheus, "Container", "Scrapes telemetry-otel:8889"],
                    [DashboardResourceNames.TelemetryOtel, "Container", "OTLP → Prometheus + Aspire dashboard"],
                ]);
            sb.AppendLine("**Stack:** .NET 10 · Aspire 13 · PostgreSQL 16 · Redis · Keycloak 25");
            sb.AppendLine();
        });

    public static string BuildDatabase() =>
        MarkdownReportBuilder.Build("Database layout", sb =>
        {
            sb.AppendLine($"PostgreSQL server resource **`{DashboardResourceNames.DataPostgres}`**. Application data uses schema **`identity`**; Keycloak uses **`keycloak`**. The default `public` schema is unused.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Item", "Value"],
                [
                    ["Server (Aspire / Docker DNS)", $"`{DashboardResourceNames.DataPostgres}`"],
                    ["Host port (dev)", "`5432`"],
                    ["Application database", "`thdarkorbit`"],
                    ["Maintenance DB", "`postgres` (no app tables)"],
                    ["App user", "`app` (from AppHost parameters)"],
                ]);
            sb.AppendLine("## Schemas");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Schema", "Purpose"],
                [
                    ["identity", "Player accounts, game user IDs"],
                    ["keycloak", "Keycloak IdP tables"],
                    ["public", "Empty by convention — do not use for app data"],
                ]);
            MarkdownReportBuilder.WriteLinks(sb, "Tools",
            [
                ("pgAdmin", DevOperationsUrls.PgAdmin),
                ("Aspire dashboard", DevOperationsUrls.AspireDashboard),
            ]);
        });

    public static string BuildEmulator(int desiredReplicas = 1) =>
        MarkdownReportBuilder.Build("Emulator configuration", sb =>
        {
            sb.AppendLine("Values below match **AppHost / ApiService** dev defaults. Change env vars in `AppHost.cs` or `appsettings.json` to alter runtime behavior.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Setting", "Value"],
                [
                    ["Cluster.IsMaster", "true"],
                    ["Cluster.DesiredWorkers", desiredReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    ["Emulator.Mesh.ApiReplicas", desiredReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    ["WorldServer.DynamicTcpPort", (desiredReplicas > 1).ToString()],
                    ["Cluster.EnableSshSpawner", "false"],
                    ["IdentityDatabase.Schema", "identity"],
                    ["IdentityDatabase.AutoMigrate", "true"],
                ]);
            sb.AppendLine("## Feature flags (ApiService)");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Flag", "Enabled"],
                [
                    ["Identity.Register", "true"],
                    ["Identity.Login", "true"],
                ]);
            sb.AppendLine("## Gateway routes (sample)");
            sb.AppendLine();
            sb.AppendLine("Full route table: `ThDarkOrbitEmulator.Gateway/appsettings.json`.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Path", "Purpose"],
                [
                    ["/api/v1/identity/login", "CMS login"],
                    ["/api/v1/identity/register", "CMS register"],
                    ["/api/v1/identity/me", "CMS profile (auth)"],
                    ["/unityApi/*", "Unity / game API"],
                    ["/flashAPI/*", "Flash client API"],
                    ["/resources/*", "Game resources"],
                    ["/api/*", "Protected API catch-all"],
                ]);
        });

    public static string BuildRedisInfo() =>
        MarkdownReportBuilder.Build("Redis (sessions)", sb =>
        {
            sb.AppendLine($"Aspire resource **`{DashboardResourceNames.SessionsRedis}`**. Used for game session storage via `AddStackExchangeRedisCache` on ApiService.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Item", "Value"],
                [
                    ["Resource name", DashboardResourceNames.SessionsRedis],
                    ["Admin UI", $"{DashboardResourceNames.SessionsRedisInsight} (via Aspire dashboard)"],
                    ["Clear command", "Operations: Clear cache → `FLUSHALL`"],
                    ["When to clear", "Stale sessions, local testing after auth changes"],
                ]);
        });
}
