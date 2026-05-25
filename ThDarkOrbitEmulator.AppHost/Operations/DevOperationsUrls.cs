namespace ThDarkOrbitEmulator.AppHost.Operations;

/// <summary>
/// Fixed local-dev URLs for operations reports. Adjust ports here when AppHost endpoints change.
/// </summary>
internal static class DevOperationsUrls
{
    public const string AspireDashboard = "https://localhost:17081";
    public const string PgAdmin = "http://localhost:5050";
    public const string Grafana = "http://localhost:13000";
    public const string Prometheus = "http://localhost:19090";
    public const string KeycloakAdmin = "http://localhost:8081";
    public const string Cms = "http://localhost:4300";
    public const string RedisInsight = "http://localhost:5540";
}
