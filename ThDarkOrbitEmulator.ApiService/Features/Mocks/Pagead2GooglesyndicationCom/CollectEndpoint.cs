using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Pagead2GooglesyndicationCom;

// DNS: pagead2.googlesyndication.com | POST /ccm/collect | flow ids: 51742572-aa85-4a53-acb4-5722b99607f4
public sealed class CollectEndpoint(ILogger<CollectEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/ccm/collect");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:pagead2.googlesyndication.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("googlesyndication collect mock matched wildcard response.");
        await MockResponseHelpers.WriteNoContentAsync(HttpContext.Response, ct);
    }
}
