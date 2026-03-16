using System.Text.Json;
using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public sealed class UnityLoginEndpoint(ILogger<UnityLoginEndpoint> logger) : EndpointWithoutRequest<UnityLoginResponse>
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
        var sessionId = string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid)
            ? Guid.NewGuid().ToString("N")
            : requestContext.Cookies.Dosid;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        logger.LogInformation(
            "UnityApi login received. UnityVersion: {UnityVersion}, UserAgent: {UserAgent}, DosidPresent: {DosidPresent}",
            requestContext.Metadata.UnityVersion,
            requestContext.Metadata.UserAgent,
            !string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid));

        HttpContext.Response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        HttpContext.Response.Cookies.Append("dosid", sessionId, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None
        });

        var mockData = new
        {
            lang = "es",
            userID = "173217628",
            sessionID = sessionId,
            basePath = "spacemap",
            pid = "1398",
            opid = "1398",
            host = "https://gbl3.darkorbit.com",
            allowChat = "1",
            nextAction = "internalWebGL",
            serverInfo = new
            {
                instanceName = "Global 3 - Titan",
                serverTime = DateTime.UtcNow.ToString("HH:mm"),
                serverDate = DateTime.UtcNow.ToString("dd.MM.yyyy"),
                timeStamp = timestamp,
                localizedTs = timestamp
            }
        };

        await SendOkAsync(new UnityLoginResponse
        {
            Data = JsonSerializer.Serialize(mockData)
        }, ct);
    }
}
