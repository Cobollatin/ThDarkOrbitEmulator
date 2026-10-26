using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

// DNS: gbl3.darkorbit.com | GET /unityApi/news.php | flow ids: b77d5c17-ed5a-4db8-abb6-b24338b7eba0
public sealed class NewsEndpoint(ILogger<NewsEndpoint> logger, TimeProvider clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/news.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity news semantic response.");

        await UnityNewsAction.WriteAsync(HttpContext.Response, clock, ct);
    }
}
