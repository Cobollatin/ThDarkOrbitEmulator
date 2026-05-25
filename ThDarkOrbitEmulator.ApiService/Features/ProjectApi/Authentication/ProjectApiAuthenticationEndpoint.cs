using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.ProjectApi.Authentication;

// DNS: www.darkorbit.com | DNS: gbl3.darkorbit.com | GET /ProjectApi/Authentication | flow ids: 2acdf22a-546b-4759-b1c7-c6c4ecdb138c, 60820c81-903a-4177-a068-59a745d7c933 [redirect_flow]
public sealed class ProjectApiAuthenticationEndpoint(
    ILogger<ProjectApiAuthenticationEndpoint> logger,
    IGameAuthSessionStore gameAuthSessionStore) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/Authentication");
        RoutePrefixOverride("ProjectApi");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.darkorbit.com", "DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = ProjectApiAuthenticationRequestContext.Parse(HttpContext.Request);
        var isInternalGbl3Step = string.Equals(requestContext.Query.AuthUser, "22", StringComparison.OrdinalIgnoreCase);
        var isEmulator = EmulatorRedirectHelper.IsEmulatorHost(HttpContext.Request);

        var rotation = await gameAuthSessionStore.RotateRedirectTokenAsync(requestContext.Query.Token ?? string.Empty, ct);
        if (rotation is null)
        {
            logger.LogWarning(
                "ProjectApi authentication rejected. Invalid or expired token. AuthUser: {AuthUser}",
                requestContext.Query.AuthUser);
            HttpContext.Response.Headers.Location = "/indexInternal.es?action=externalLogout";
            HttpContext.Response.StatusCode = StatusCodes.Status302Found;
            await HttpContext.Response.CompleteAsync();
            return;
        }

        var session = rotation.Session;
        var dosid = session.Dosid;

        logger.LogInformation(
            "ProjectApi authentication redirect. Variant: {Variant}, AuthUser: {AuthUser}, Username: {Username}, UserAgent: {UserAgent}",
            isInternalGbl3Step ? "gbl3-internal-start" : "www-to-gbl3",
            requestContext.Query.AuthUser,
            session.Username,
            requestContext.Metadata.UserAgent);

        if (isInternalGbl3Step)
        {
            HttpContext.Response.Headers.Location = EmulatorRedirectHelper.ToRedirectLocation(
                HttpContext.Request,
                "/indexInternal.es?action=internalStart&prc=100",
                "/indexInternal.es?action=internalStart&prc=100");

            AppendSessionCookies(dosid, isEmulator, clearAcr: true);
        }
        else
        {
            var nextToken = rotation.RedirectToken;
            HttpContext.Response.Headers.Location = EmulatorRedirectHelper.ToRedirectLocation(
                HttpContext.Request,
                $"https://gbl3.darkorbit.com/ProjectApi/Authentication?authUser=22&token={Uri.EscapeDataString(nextToken)}",
                $"/ProjectApi/Authentication?authUser=22&token={Uri.EscapeDataString(nextToken)}");

            AppendSessionCookies(dosid, isEmulator, clearAcr: false);
        }

        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }

    private void AppendSessionCookies(string dosid, bool isEmulator, bool clearAcr)
    {
        HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Set-Cookie", "aid=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aip=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aig=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");

        if (clearAcr)
        {
            HttpContext.Response.Headers.Append("Set-Cookie", "acr=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
            var lastLoginUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lastLoginCookie = isEmulator
                ? $"lastLogin={lastLoginUnix}; expires={DateTime.UtcNow.AddDays(30):R}; Max-Age=2592000; path=/"
                : $"lastLogin={lastLoginUnix}; expires={DateTime.UtcNow.AddDays(30):R}; Max-Age=2592000; path=/; domain=.gbl3.darkorbit.com";
            HttpContext.Response.Headers.Append("Set-Cookie", lastLoginCookie);
        }
        else
        {
            HttpContext.Response.Headers.Append("Set-Cookie", $"acr=22; expires={DateTime.UtcNow.AddDays(30):R}; Max-Age=2592000; path=/; samesite=none; secure");
        }
    }
}
