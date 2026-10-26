using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;

// DNS: gbl3.darkorbit.com | GET /xml/CommandCenter.xml | IL2CPP literal; request and response unverified.
public sealed class LegacyXmlCommandCenterEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/CommandCenter.xml");
        RoutePrefixOverride("xml");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override Task HandleAsync(CancellationToken ct) =>
        CommandCenterNewsResponse.WriteAsync(HttpContext.Response, ct);
}
