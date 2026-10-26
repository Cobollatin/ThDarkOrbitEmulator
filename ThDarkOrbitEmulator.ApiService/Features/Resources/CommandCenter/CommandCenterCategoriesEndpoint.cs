using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// DNS: darkorbit-22.bpsecure.com | GET /resources/command-center/categories.xml | public manifest asset, runtime request unverified
public sealed class CommandCenterCategoriesEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/command-center/categories.xml");
        RoutePrefixOverride("resources");
        AllowAnonymous();
        Description(description => description.WithTags("CDN:darkorbit-22.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await CapturedCommandCenterCategories.WriteResponseAsync(HttpContext, ct);
    }
}
