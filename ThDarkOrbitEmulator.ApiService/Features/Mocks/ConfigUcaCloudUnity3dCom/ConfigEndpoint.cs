using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.ConfigUcaCloudUnity3dCom;

// DNS: config.uca.cloud.unity3d.com | POST / | flow ids: 65d1d8c1-84c4-4036-893f-ffedc42657fd [new]
public sealed class ConfigEndpoint(ILogger<ConfigEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:config.uca.cloud.unity3d.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity config mock matched wildcard response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "application/json;charset=utf-8";
        HttpContext.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        HttpContext.Response.Headers.Append("Server", "Jetty(9.4.z-SNAPSHOT)");
        HttpContext.Response.Headers.Append("Via", "1.1 google");
        HttpContext.Response.Headers.Append("Alt-Svc", "h3=\":443\"; ma=2592000");

        var body = FlowCaptureLoader.LoadResponseBody("65d1d8c1-84c4-4036-893f-ffedc42657fd");
        await HttpContext.Response.WriteAsync(body, ct);
    }
}
