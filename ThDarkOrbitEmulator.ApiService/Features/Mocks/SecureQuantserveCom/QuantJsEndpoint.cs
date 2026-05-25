using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.SecureQuantserveCom;

// DNS: secure.quantserve.com | GET /quant.js | flow ids: f5cc14cb-b411-4b97-bc64-bfa1827593db
public sealed class QuantJsEndpoint(ILogger<QuantJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/quant.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:secure.quantserve.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("quant.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock quant.js */", "application/javascript; charset=UTF-8", ct);
    }
}
