using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// DNS: gbl3.darkorbit.com | GET /xml/resource_command_center.xml | IL2CPP literal; runtime request unverified
public sealed class LegacyXmlCommandCenterResourcesEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/resource_command_center.xml");
        RoutePrefixOverride("xml");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await CapturedCommandCenterResources.WriteResponseAsync(HttpContext.Response, ct);
    }
}
