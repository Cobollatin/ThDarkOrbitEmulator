using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.GoogleAnalyticsCom;

// DNS: www.google-analytics.com | POST /g/collect | flow ids: 996ba32a-8c4e-4022-b316-09c62e5d6b7d
public sealed class CollectEndpoint(ILogger<CollectEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/g/collect");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.google-analytics.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("google analytics collect mock matched wildcard response.");
        await MockResponseHelpers.WriteNoContentAsync(HttpContext.Response, ct);
    }
}
