using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// DNS: gbl3.darkorbit.com | GET /resources/resource_command_center.xml | flow ids: 47e00d96-50e7-4a4c-9962-19c587613abd
public sealed class ResourceCommandCenterEndpoint(ILogger<ResourceCommandCenterEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/resource_command_center.xml");
        RoutePrefixOverride("resources");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Serving captured command center resource manifest.");

        await CapturedCommandCenterResources.WriteResponseAsync(HttpContext.Response, ct);
    }
}
