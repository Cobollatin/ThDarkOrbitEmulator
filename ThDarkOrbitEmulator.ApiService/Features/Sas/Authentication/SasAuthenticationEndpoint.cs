using FastEndpoints;
namespace ThDarkOrbitEmulator.ApiService.Features.Sas.Authentication;

public sealed class SasAuthenticationEndpoint(ILogger<SasAuthenticationEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/Bigpoint");
        RoutePrefixOverride("Sas/Authentication");
        AllowAnonymous();
        Description(description => description.WithTags("Sas.Authentication"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = await SasAuthenticationRequestContext.ParseAsync(HttpContext.Request, ct);
        var authUser = string.IsNullOrWhiteSpace(requestContext.Query.AuthUser)
            ? "291"
            : requestContext.Query.AuthUser;
        var token = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "Sas auth received. AuthUser: {AuthUser}, UserAgent: {UserAgent}, HasRecaptcha: {HasRecaptcha}, HasHCaptcha: {HasHCaptcha}",
            requestContext.Query.AuthUser,
            requestContext.Metadata.UserAgent,
            !string.IsNullOrWhiteSpace(requestContext.Payload.GRecaptchaResponse),
            !string.IsNullOrWhiteSpace(requestContext.Payload.HCaptchaResponse));

        var redirectUrl = $"/ProjectApi/Authentication?authUser={Uri.EscapeDataString(authUser)}&token={Uri.EscapeDataString(token)}";

        HttpContext.Response.Headers.Location = redirectUrl;
        HttpContext.Response.Headers.Append("Set-Cookie", "aliyungf_tc=02dda347b489cb6682eff397cf98da008fb62401c7695cef8110273ce0cc87f7; Path=/; HttpOnly");
        HttpContext.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none';");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000");

        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }
}
