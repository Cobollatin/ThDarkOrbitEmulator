using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Sas.Authentication;

// DNS: sas.bpsecure.com | POST /Sas/Authentication/Bigpoint | flow ids: b51912eb-9ae5-4ec6-91dc-c5444e09a232 [redirect_flow]
public sealed class SasAuthenticationEndpoint(
    ILogger<SasAuthenticationEndpoint> logger,
    IKeycloakService keycloakService,
    IIdentityQueryService identityQueryService,
    IGameAuthSessionStore gameAuthSessionStore) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/Bigpoint");
        RoutePrefixOverride("Sas/Authentication");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:sas.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = await SasAuthenticationRequestContext.ParseAsync(HttpContext.Request, ct);
        var username = requestContext.Payload.Username?.Trim();
        var password = requestContext.Payload.Password;

        logger.LogInformation(
            "Sas auth received. AuthUser: {AuthUser}, UserAgent: {UserAgent}, HasRecaptcha: {HasRecaptcha}, HasHCaptcha: {HasHCaptcha}",
            requestContext.Query.AuthUser,
            requestContext.Metadata.UserAgent,
            !string.IsNullOrWhiteSpace(requestContext.Payload.GRecaptchaResponse),
            !string.IsNullOrWhiteSpace(requestContext.Payload.HCaptchaResponse));

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            await RedirectLoginFailureAsync(ct);
            return;
        }

        var account = await identityQueryService.ExecuteAsync(new GetAccountByUsernameQuery(username), ct);
        if (account is null)
        {
            logger.LogWarning("Sas auth failed: account not found for {Username}", username);
            await RedirectLoginFailureAsync(ct);
            return;
        }

        var accessToken = await keycloakService.RequestAccessTokenAsync(username, password, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            logger.LogWarning("Sas auth failed: Keycloak rejected credentials for {Username}", username);
            await RedirectLoginFailureAsync(ct);
            return;
        }

        var sessionResult = await gameAuthSessionStore.CreateSessionAsync(account, accessToken, ct);
        var redirectToken = sessionResult.RedirectToken;

        var redirectUrl = EmulatorRedirectHelper.ToRedirectLocation(
            HttpContext.Request,
            $"https://www.darkorbit.com/ProjectApi/Authentication?authUser=291&token={Uri.EscapeDataString(redirectToken)}",
            $"/ProjectApi/Authentication?authUser=291&token={Uri.EscapeDataString(redirectToken)}");

        HttpContext.Response.Headers.Location = redirectUrl;
        HttpContext.Response.Headers.Append("Set-Cookie", $"aliyungf_tc={Guid.NewGuid():N}; Path=/; HttpOnly");
        HttpContext.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none';");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000");

        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }

    private async Task RedirectLoginFailureAsync(CancellationToken ct)
    {
        HttpContext.Response.Headers.Location = EmulatorRedirectHelper.ToRedirectLocation(
            HttpContext.Request,
            "https://www.darkorbit.com/?loginError=94",
            "/?loginError=94");
        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }
}
