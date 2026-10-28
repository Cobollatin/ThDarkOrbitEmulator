using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

// StreamingAssets/config.txt hangerChange; LaunchShow.GetHangerPost sends hangarId as a WWWForm field.
public static class UnityHangarActivateAction
{
    public static async Task<IResult> HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        CockpitCatalogStore catalog, LocalHangarStore hangars, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("UnityHangarActivate");
        context.Response.Headers.CacheControl = UnityLoginAction.CacheControl;
        var dosid = context.Request.Cookies.TryGetValue("dosid", out var cookie) ? cookie : null;
        var session = !string.IsNullOrWhiteSpace(dosid) && dosid.Length <= LocalLogoutAction.MaxDosidLength
            ? await sessions.TryGetByDosidAsync(dosid, ct) : null;
        if (session is null)
        {
            logger.LogWarning("Unity hangar activation rejected: missing or invalid dosid session.");
            return Results.Json(new { success = 0, message = "Your session is missing or expired." }, statusCode: 401);
        }

        if (!context.Request.HasFormContentType)
            return Results.Json(new { success = 0, message = "Expected a hangar form." }, statusCode: 400);
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        {
            logger.LogWarning("Unity hangar activation rejected: unreadable form.");
            return Results.Json(new { success = 0, message = "Invalid hangar form." }, statusCode: 400);
        }
        if (form.Count != 1 || !form.TryGetValue("hangarId", out var ids) || ids.Count != 1
            || ids[0] is not { Length: 6 } hangarId || !hangarId.All(char.IsAsciiDigit))
        {
            logger.LogWarning("Unity hangar activation rejected: missing or invalid hangarId.");
            return Results.Json(new { success = 0, message = "Invalid hangarId." }, statusCode: 400);
        }

        var assigned = await catalog.HangarsAsync(session, await hangars.GetActiveSlotAsync(session.AccountId, ct), ct);
        var selected = assigned.FirstOrDefault(item => item.HangarId == hangarId);
        if (selected is null)
        {
            logger.LogWarning("Unity hangar activation rejected: hangarId is not assigned to account {AccountId}.", session.AccountId);
            return Results.Json(new { success = 0, message = "Hangar is not assigned to this account." }, statusCode: 404);
        }

        if (!selected.HangarActive && await hangars.ChangeAsync(session.AccountId, selected.SlotNumber, ct) is not LocalHangarChange.Applied
            && await hangars.GetActiveSlotAsync(session.AccountId, ct) != selected.SlotNumber)
            return Results.Json(new { success = 0, message = "Hangar selection could not be changed." }, statusCode: 409);

        return Results.Json(new { success = 1, message = (string?)null });
    }
}
