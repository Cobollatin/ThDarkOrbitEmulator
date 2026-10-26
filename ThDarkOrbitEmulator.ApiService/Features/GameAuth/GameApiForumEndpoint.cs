using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.GameAuth;

// DNS: gbl3.darkorbit.com | GET /GameAPI.php
// The action=portal.redirectToBoard link was captured in unityApi/links.php; the successful response is synthetic.
public sealed class GameApiForumEndpoint(IGameAuthSessionStore sessionStore) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/GameAPI.php");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await LocalLinksRequest.WriteAsync(HttpContext.Response,
            await GameApiForumAction.ResolveAsync(HttpContext.Request, sessionStore, ct), ct);
    }
}

public static class GameApiForumResponse
{
    public static async Task WriteAsync(HttpResponse response, bool knownAction, bool authorized, CancellationToken ct,
        string? topic = null)
    {
        if (!knownAction)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!authorized)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=UTF-8";
        await response.WriteAsync(LocalForumHelp.Page(true, topic), ct);
    }
}
