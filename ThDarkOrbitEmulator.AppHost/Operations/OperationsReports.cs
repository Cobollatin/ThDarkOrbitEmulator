namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class OperationsReports
{
    public static string BuildOverview() =>
        MarkdownReportBuilder.Build("Operations overview", sb =>
        {
            sb.AppendLine("Local development stack for **ThDarkOrbit Emulator**. Use resource commands on each row’s resource for detail.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Service", "Role", "Status"],
                [
                    ["apiservice", "Game + identity API (FastEndpoints)", "Expected: healthy at `/health`"],
                    ["gateway", "YARP reverse proxy (game client + API)", "Expected: healthy at `/health`"],
                    ["thdarkorbit-emulator", "PostgreSQL 16 (identity + Keycloak schemas)", "Expected: running"],
                    ["cache", "Redis (game sessions)", "Expected: running"],
                    ["keycloak", "Authentication (realm `thdarkorbit`)", "Expected: running on :8081"],
                    ["cms", "Player CMS (Angular)", "Expected: running on :4300"],
                    ["pgadmin", "Database admin UI", "Expected: running on :5050"],
                    ["grafana / prometheus", "Metrics (OTEL → collector → Prometheus)", "Expected: :13000 / :19090"],
                    ["redisinsight", "Redis admin UI (auto-linked to cache)", "Expected: :5540"],
                    ["otel-collector", "OTLP fan-out (metrics + Aspire traces/logs)", "Expected: :14318 HTTP"],
                ]);
            MarkdownReportBuilder.WriteLinks(sb, "Quick links",
            [
                ("Aspire dashboard", DevOperationsUrls.AspireDashboard),
                ("pgAdmin", DevOperationsUrls.PgAdmin),
                ("Grafana", DevOperationsUrls.Grafana),
                ("Prometheus", DevOperationsUrls.Prometheus),
                ("Keycloak admin", DevOperationsUrls.KeycloakAdmin),
                ("Player CMS", DevOperationsUrls.Cms),
                ("RedisInsight", DevOperationsUrls.RedisInsight),
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
                    ["apiservice", ".NET project", "FastEndpoints, identity, game routes"],
                    ["gateway", ".NET project", "YARP; game + `/api` proxy"],
                    ["cms", "JavaScript (Angular)", "Port 4300, references gateway"],
                    ["thdarkorbit-emulator", "PostgreSQL", "DB `thdarkorbit`; schemas `identity`, `keycloak`"],
                    ["thdarkorbit", "PostgreSQL database", "Application database resource"],
                    ["cache", "Redis", "Session cache"],
                    ["keycloak", "Container", "Realm `thdarkorbit` via ApiService Admin API bootstrap"],
                    ["pgadmin", "Container (via WithPgAdmin)", "Postgres UI"],
                    ["grafana", "Container", "Dashboards"],
                    ["prometheus", "Container", "Scrapes otel-collector:8889"],
                    ["otel-collector", "Container", "OTLP → Prometheus + Aspire dashboard"],
                    ["redisinsight", "Container", "Redis browser UI"],
                ]);
            sb.AppendLine("**Stack:** .NET 10 · Aspire 13 · PostgreSQL 16 · Redis · Keycloak 25");
            sb.AppendLine();
        });

    public static string BuildDatabase() =>
        MarkdownReportBuilder.Build("Database layout", sb =>
        {
            sb.AppendLine("PostgreSQL server resource **`thdarkorbit-emulator`**. Application data uses schema **`identity`**; Keycloak uses **`keycloak`**. The default `public` schema is unused.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Item", "Value"],
                [
                    ["Server (Aspire / Docker DNS)", "`thdarkorbit-emulator`"],
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

    public static string BuildEmulator() =>
        MarkdownReportBuilder.Build("Emulator configuration", sb =>
        {
            sb.AppendLine("Values below match **AppHost / ApiService** dev defaults. Change env vars in `AppHost.cs` or `appsettings.json` to alter runtime behavior.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Setting", "Value"],
                [
                    ["Cluster.IsMaster", "true"],
                    ["Cluster.DesiredWorkers", "1"],
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
        MarkdownReportBuilder.Build("Redis (cache)", sb =>
        {
            sb.AppendLine("Aspire resource **`cache`**. Used for game session storage via `AddStackExchangeRedisCache` on ApiService.");
            sb.AppendLine();
            MarkdownReportBuilder.WriteTable(
                sb,
                ["Item", "Value"],
                [
                    ["Resource name", "cache"],
                    ["Clear command", "Operations: Clear cache → `FLUSHALL`"],
                    ["When to clear", "Stale sessions, local testing after auth changes"],
                ]);
        });
}
