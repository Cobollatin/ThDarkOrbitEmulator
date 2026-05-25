using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.AssetsBpsecureCom;

// DNS: assets.bpsecure.com | GET /bpid/bpid.js | flow ids: e7ed5c24-df7f-4f74-9442-f3845c6553b1
public sealed class BpidJsEndpoint(ILogger<BpidJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/bpid/bpid.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:assets.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("bpid.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock bpid.js */", "application/javascript; charset=UTF-8", ct);
    }
}
