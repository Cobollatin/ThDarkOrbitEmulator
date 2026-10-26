using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// The official logout redirects were observed, but session invalidation is local behavior.
public static class LocalLogoutAction
{
    public const string Action = "externalLogout";
    public const string LogoutLocation = "/indexInternal.es?action=internalWebGL&from=externalLogout";
    public const string SignedOutLocation = "/index.es?action=externalHome&loginError=94";
    public const string ExpiredCookie =
        "dosid=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT; samesite=none; secure; HttpOnly";

    // Longer cookies cannot be local session ids (32 hex characters); they are not looked up.
    public const int MaxDosidLength = 128;

    public static bool IsLogout(string? action) => string.Equals(action, Action, StringComparison.OrdinalIgnoreCase);

    public static bool IsSignedOutStep(string? action, string? from)
        => (string.Equals(action, "internalWebGL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "internalMenu", StringComparison.OrdinalIgnoreCase))
           && IsLogout(from);

    // Only the logout and its follow-up redirect skip the fresh dosid cookie; other actions keep it.
    public static bool SuppressesSessionCookie(string? action, string? from) => IsLogout(action) || IsSignedOutStep(action, from);

    // Logout always succeeds: the query is ignored and never reflected, and a missing or stale cookie is still expired.
    public static async Task HandleAsync(HttpResponse response, string? dosid,
        IGameAuthSessionStore sessions, CancellationToken ct)
    {
        await InvalidateAsync(response, dosid, sessions, ct);
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ClientRedirectResponse.Apply(response,
            IndexInternalStartEndpoint.AdaptLocationForClient(response.HttpContext.Request, LogoutLocation));
    }

    public static void SignedOut(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ClientRedirectResponse.Apply(response, SignedOutLocation);
    }

    public static async Task InvalidateAsync(HttpResponse response, string? dosid,
        IGameAuthSessionStore sessions, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dosid) && dosid.Length <= MaxDosidLength)
            await sessions.InvalidateAsync(dosid, ct);

        response.Headers.Append("Set-Cookie", ExpiredCookie);
    }
}
