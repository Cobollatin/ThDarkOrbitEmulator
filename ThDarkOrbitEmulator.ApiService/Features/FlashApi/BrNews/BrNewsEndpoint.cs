using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;

// DNS: gbl3.darkorbit.com | GET /flashAPI/brNews.php | flow ids: c0bc044d-98ff-4108-97ad-a360660eab5a
public sealed class BrNewsEndpoint(ILogger<BrNewsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/brNews.php");
        RoutePrefixOverride("flashAPI");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Flash brNews semantic response.");

        await CommandCenterNewsResponse.WriteAsync(HttpContext.Response, ct);
    }
}
