using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.RulesQuantcountCom;

// DNS: rules.quantcount.com | GET /rules-p-qB5-sBEDaawtz.js | flow ids: c204d59c-786d-400e-8381-a28ae7331d92
public sealed class RulesEndpoint(ILogger<RulesEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/rules-p-qB5-sBEDaawtz.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:rules.quantcount.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("quantcount rules mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock rules */", "application/javascript; charset=UTF-8", ct);
    }
}
