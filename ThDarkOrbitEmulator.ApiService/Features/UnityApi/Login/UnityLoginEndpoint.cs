using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public sealed class UnityLoginEndpoint(
    ILogger<UnityLoginEndpoint> logger,
    IGameAuthSessionStore gameAuthSessionStore) : EndpointWithoutRequest<UnityLoginResponse>
{
    public override void Configure()
    {
        Get("/login.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("UnityApi"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = UnityLoginRequestContext.Parse(HttpContext.Request);
        var dosid = requestContext.Cookies.Dosid;

        logger.LogInformation(
            "UnityApi login received. UnityVersion: {UnityVersion}, UserAgent: {UserAgent}, DosidPresent: {DosidPresent}",
            requestContext.Metadata.UnityVersion,
            requestContext.Metadata.UserAgent,
            !string.IsNullOrWhiteSpace(dosid));

        var session = !string.IsNullOrWhiteSpace(dosid)
            ? await gameAuthSessionStore.TryGetByDosidAsync(dosid, ct)
            : null;

        if (session is null)
        {
            logger.LogWarning("UnityApi login rejected: missing or unknown dosid session.");
            await Send.UnauthorizedAsync(cancellation: ct);
            return;
        }

        HttpContext.Response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        HttpContext.Response.Cookies.Append("dosid", session.Dosid, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None
        });

        var innerPayload = UnityLoginPayloadBuilder.Build(session, HttpContext.Request);
        await Send.OkAsync(new UnityLoginResponse
        {
            Data = innerPayload
        }, cancellation: ct);
    }
}
