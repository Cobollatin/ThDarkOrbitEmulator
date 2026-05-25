using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.CdpCloudUnity3dCom;

// DNS: cdp.cloud.unity3d.com | POST /v1/events | flow ids: 1c3a1eb4-ac3f-4f11-aadc-26a86bbb9182 [new], eef2b3da-b9e2-4df2-9d3c-e3e611610271 [new]
public sealed class EventsEndpoint(ILogger<EventsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/v1/events");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:cdp.cloud.unity3d.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity cdp events mock matched wildcard response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.Headers.Append("Vary", "Origin");
        HttpContext.Response.Headers.Append("Via", "1.1 google");
        HttpContext.Response.Headers.Append("Alt-Svc", "h3=\":443\"; ma=2592000,h3-29=\":443\"; ma=2592000");
        await HttpContext.Response.CompleteAsync();
    }
}
