using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// DNS: gbl3.darkorbit.com | GET /resources/resource_command_center.xml | flow ids: 47e00d96-50e7-4a4c-9962-19c587613abd
public sealed class ResourceCommandCenterEndpoint(ILogger<ResourceCommandCenterEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/resource_command_center.xml");
        RoutePrefixOverride("resources");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Resource command center semantic response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "application/xml";
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        const string body = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <filecollection>
              <UnknownResourceEntry name="UnknownCommandCenterAsset" />
            </filecollection>
            """;

        await HttpContext.Response.WriteAsync(body, ct);
    }
}
