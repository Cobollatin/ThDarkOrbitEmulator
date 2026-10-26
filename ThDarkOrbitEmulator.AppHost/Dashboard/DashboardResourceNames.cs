namespace ThDarkOrbitEmulator.AppHost.Dashboard;

/// <summary>
/// Aspire resource IDs: <c>{purpose}-{technology}</c> (kebab-case).
/// Dashboard shows the ID; purpose comes first so the list reads by role, tech suffix clarifies the stack.
/// </summary>
internal static class DashboardResourceNames
{
    public const string SessionsRedis = "sessions-redis";
    public const string SessionsRedisInsight = "sessions-redisinsight";
    public const string DataPostgres = "data-postgres";
    public const string AppDatabase = "app-db";
    public const string AuthKeycloak = "auth-keycloak";
    public const string TelemetryOtel = "telemetry-otel";
    public const string MetricsPrometheus = "metrics-prometheus";
    public const string DashboardsGrafana = "dashboards-grafana";
    public const string ApiGame = "api-game";
    public const string GatewayPlayers = "gateway-players";
    public const string CmsPlayers = "cms-players";
}
