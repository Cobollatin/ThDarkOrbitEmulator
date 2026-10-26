using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links;

// DNS: gbl3.darkorbit.com | GET /unityApi/links.php | flow ids: 33e6fd0c-1df9-47b6-b884-e2f817d55b7f
public sealed class LinksEndpoint(ILogger<LinksEndpoint> logger, IGameAuthSessionStore gameAuthSessionStore)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/links.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity links semantic response.");

        await UnityLinksAction.HandleAsync(HttpContext, gameAuthSessionStore, ct);
    }
}
