using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.ProjectApi.Authentication;

public sealed class ProjectApiAuthenticationEndpoint(ILogger<ProjectApiAuthenticationEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/Authentication");
        RoutePrefixOverride("ProjectApi");
        AllowAnonymous();
        Description(description => description.WithTags("ProjectApi.Authentication"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = ProjectApiAuthenticationRequestContext.Parse(HttpContext.Request);

        var dosid = string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid)
            ? Guid.NewGuid().ToString("N")
            : requestContext.Cookies.Dosid;

        var acr = string.IsNullOrWhiteSpace(requestContext.Cookies.Acr)
            ? "22"
            : requestContext.Cookies.Acr;

        var token = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "ProjectApi authentication redirect. AuthUser: {AuthUser}, Acr: {Acr}, UserAgent: {UserAgent}",
            requestContext.Query.AuthUser,
            acr,
            requestContext.Metadata.UserAgent);

        var redirectUrl = string.Equals(requestContext.Query.AuthUser, "22", StringComparison.Ordinal)
            ? "/indexInternal.es?action=internalStart&prc=100"
            : $"/ProjectApi/Authentication?authUser={Uri.EscapeDataString(acr)}&token={Uri.EscapeDataString(token)}";

        HttpContext.Response.Headers.Location = redirectUrl;
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");

        if (!string.Equals(requestContext.Query.AuthUser, "22", StringComparison.Ordinal))
        {
            HttpContext.Response.Headers.Append("Set-Cookie", "aid=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
            HttpContext.Response.Headers.Append("Set-Cookie", "aip=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
            HttpContext.Response.Headers.Append("Set-Cookie", "aig=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
            HttpContext.Response.Headers.Append("Set-Cookie", $"acr={acr}; expires={DateTime.UtcNow.AddDays(30):R}; Max-Age=2592000; path=/; samesite=none; secure");
        }

        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }
}
