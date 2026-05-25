using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.BatBingNet;

// DNS: bat.bing.net | POST /actionp/0 | flow ids: 93d872d5-d155-4c42-9d31-1c1917fe54e9, 68e9073d-e655-4149-bc12-040caafcd313, a080c598-e904-45d5-bf70-de97011171cd, c6ba67d9-0c76-4ab3-9319-37606af0018c, ca240372-c8c9-43a1-858c-b9696298d5d0, ec365e42-61b4-4881-827f-49843f1fb082
public sealed class ActionpEndpoint(ILogger<ActionpEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/actionp/0");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:bat.bing.net"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("bat.bing.net actionp mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, string.Empty, "text/plain; charset=UTF-8", ct);
    }
}

// DNS: bat.bing.net | GET /action/0 | flow ids: 19ae2438-1ffe-4d16-a10d-d1744e9f744f, 7819f5e0-c661-4a9b-8ddb-38a8c68af48f
public sealed class ActionEndpoint(ILogger<ActionEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/action/0");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:bat.bing.net"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("bat.bing.net action mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, string.Empty, "text/plain; charset=UTF-8", ct);
    }
}
