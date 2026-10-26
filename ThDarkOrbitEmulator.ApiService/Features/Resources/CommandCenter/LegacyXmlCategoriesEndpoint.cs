using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// DNS: gbl3.darkorbit.com | GET /xml/Categories.xml | IL2CPP literal; runtime request and body unverified.
public sealed class LegacyXmlCategoriesEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/Categories.xml");
        RoutePrefixOverride("xml");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await CapturedCommandCenterCategories.WriteResponseAsync(HttpContext, ct);
    }
}
