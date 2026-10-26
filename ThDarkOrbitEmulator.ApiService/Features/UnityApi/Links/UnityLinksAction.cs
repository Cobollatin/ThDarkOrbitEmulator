using System.Text;
using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links;

// Live (20261021-224636-login flow 83) returned 200 JSON with an exact Content-Length and refreshed the dosid
// cookie (path=/; samesite=none; secure; HttpOnly). Anonymous Live behavior is unknown, so the links stay public
// (they carry no account data) and only a live, bounded dosid gets its cookie refreshed (emulator-defined).
public static class UnityLinksAction
{
    public const string CacheControl = "no-store, no-cache, must-revalidate";
    public const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        var dosid = request.Cookies.TryGetValue("dosid", out var value) ? value : null;
        var session = !string.IsNullOrWhiteSpace(dosid) && dosid.Length <= LocalLogoutAction.MaxDosidLength
            ? await sessions.TryGetByDosidAsync(dosid, ct)
            : null;

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        if (session is not null)
        {
            response.Cookies.Append("dosid", session.Dosid, new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None
            });
        }

        response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        response.Headers.CacheControl = CacheControl;
        response.Headers.Pragma = "no-cache";
        response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        var body = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(ClientStubResponses.Links(ClientFacingGameHost.Resolve(request))));
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, ct);
    }
}
