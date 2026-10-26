using System.Globalization;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalAccountSettingsAction
{
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "saved", "unchanged"];
    public const string SavedKey = "saved";
    public const string UnchangedKey = "unchanged";
    public const string UnchangedLocation = "/indexInternal.es?action=internalAccount&" + UnchangedKey + "=1";

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // `saved` is set only by the save redirect, so it must be lowercase and a revision from 1 to MaxRevision.
    // `unchanged` is set only by the no-op save redirect: exactly "1" and never together with `saved`.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1
        && !(query.ContainsKey(SavedKey) && query.ContainsKey(UnchangedKey))
        && query.Keys.Contains("action", StringComparer.Ordinal)
        && query["action"].ToString() == "internalAccount"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key != SavedKey || (TryParseRevision(pair.Value.ToString(), out var saved) && saved >= 1))
            && (pair.Key != UnchangedKey || pair.Value.ToString() == "1"));

    public static bool IsUnchangedNotice(IQueryCollection query) =>
        query.TryGetValue(UnchangedKey, out var value) && value.ToString() == "1";

    // The confirmed revision from a valid page query, or null when the page was opened normally.
    public static int? SavedRevision(IQueryCollection query) =>
        query.TryGetValue(SavedKey, out var value) && TryParseRevision(value.ToString(), out var saved) && saved >= 1
            ? saved : null;

    public static string SavedLocation(int revision) =>
        $"/indexInternal.es?action=internalAccount&{SavedKey}={revision.ToString(CultureInfo.InvariantCulture)}";

    // 1-7 ASCII digits up to MaxRevision; signs, spaces, and non-ASCII digits are rejected.
    public static bool TryParseRevision(string text, out int revision)
    {
        revision = 0;
        return text.Length is >= 1 and <= 7 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision <= LocalAccountPreferencesStore.MaxRevision;
    }

    // The save query carries at most one action, spelled exactly as the page form posts it.
    // Query lookups ignore key case, so the key itself is compared ordinally (`ACTION` is tampering).
    public static bool ValidSaveQuery(IQueryCollection query) =>
        query.Count == 0 || (query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
            && string.Equals(query["action"].ToString(), "internalAccountSave", StringComparison.Ordinal));

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Account center error</title></head><body><h1>Account request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalAccount\">Return to Account center</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalAccountPreferencesStore preferences, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Account preferences must be submitted with POST.", ct);
            return;
        }
        if (!ValidSaveQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Account center action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before changing account preferences.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit the Account center form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Account center form could not be read.", ct); return; }
        // Forms without rev predate revision checks; they still save but cannot detect a double submit.
        var hasRevision = form.ContainsKey("rev");
        var valid = hasRevision
            ? LocalHangarError.HasExactly(form, "language", "sound", "notifications", "rev")
            : LocalHangarError.HasExactly(form, "language", "sound", "notifications");
        if (!valid)
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        int? revision = null;
        if (hasRevision)
        {
            if (!TryParseRevision(form["rev"].ToString(), out var parsed))
            { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
            revision = parsed;
        }
        var (change, saved) = await preferences.SaveStateAsync(session.AccountId, form["language"].ToString(),
            form["sound"].ToString(), form["notifications"].ToString(), revision, ct);
        switch (change)
        {
            case AccountPreferencesChange.Applied:
                // The redirect carries the revision this save wrote; the page confirms it only while it is still the latest.
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = saved >= 1 ? SavedLocation(saved) : "/indexInternal.es?action=internalAccount";
                return;
            case AccountPreferencesChange.Unchanged:
                // Same values as stored: no write, no revision bump, and no false "saved" confirmation.
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = UnchangedLocation;
                return;
            case AccountPreferencesChange.Conflict:
                await ErrorAsync(response, 409, "This form was already submitted or your preferences changed. Reload the Account center.", ct);
                return;
            default:
                await ErrorAsync(response, 400, "Language, sound, or notification value is not allowed.", ct);
                return;
        }
    }
}
