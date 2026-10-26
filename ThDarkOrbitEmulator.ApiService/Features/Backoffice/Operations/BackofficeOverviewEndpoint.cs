using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Operations;

public sealed class BackofficeOverviewEndpoint : EndpointWithoutRequest<BackofficeOverviewResponse>
{
    public override void Configure()
    {
        Get("backoffice/operations/overview");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var response = new BackofficeOverviewResponse
        {
            Services =
            [
                new ServiceStatusDto("api-game", "Game + identity API", "healthy"),
                new ServiceStatusDto("gateway-players", "YARP reverse proxy", "healthy"),
                new ServiceStatusDto("data-postgres", "PostgreSQL 16", "running"),
                new ServiceStatusDto("sessions-redis", "Redis sessions", "running"),
                new ServiceStatusDto("auth-keycloak", "Authentication", "running"),
                new ServiceStatusDto("cms-players", "Player CMS", "running")
            ],
            Links =
            [
                new QuickLinkDto("Aspire dashboard", "https://localhost:17081"),
                new QuickLinkDto("pgAdmin", "http://localhost:5050"),
                new QuickLinkDto("Grafana", "http://localhost:13000"),
                new QuickLinkDto("Prometheus", "http://localhost:19090"),
                new QuickLinkDto("RedisInsight (Aspire → sessions-redis)", "https://localhost:17081"),
                new QuickLinkDto("Keycloak admin", "http://localhost:8081")
            ]
        };

        return Send.OkAsync(response, ct);
    }
}

public sealed class BackofficeOverviewResponse
{
    public List<ServiceStatusDto> Services { get; init; } = [];
    public List<QuickLinkDto> Links { get; init; } = [];
}

public sealed record ServiceStatusDto(string Name, string Role, string Status);

public sealed record QuickLinkDto(string Label, string Url);
