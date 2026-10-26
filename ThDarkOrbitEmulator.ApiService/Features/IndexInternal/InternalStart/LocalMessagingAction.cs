using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalMessagingAction
{
    // Emulator-defined form lifetime; stale pages must be reloaded before submitting.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxRevision = 1_000_000;
    private const string PageUrl = "/indexInternal.es?action=internalMessages";
    // Menu-130: confirmation keys set by the action redirects; at most one per page, text always comes from saved state.
    public static readonly string[] NoticeKeys = ["sent", "deleted", "added", "removed", "readall"];
    // Emulator-defined page query: shell navigation parameters plus an optional message id, once each.
    private static readonly string[] AllowedPageKeys = ["action", "message", "folder", "q", "from", "prc", "lang", .. NoticeKeys];
    private const int MaxPageValueLength = 64;

    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
        { "internalMessageSend", "internalMessageDelete", "internalFriendAdd", "internalFriendRemove", "internalMessageReadAll" };

    private enum KeyCheck { Valid, Invalid, Expired }

    public static string CreateFormKey(string dosid, string action, string target, int revision, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, action, target, revision, nonce, issued)}";
    }

    private static string Sign(string dosid, string action, string target, int revision, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"messaging:{action}:{target}:{revision}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string action, string target, int revision, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, action, target, revision, parts[0], parts[1])))) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture));
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }

    // Ids are positive integers of at most nine digits; signs, spaces, and leading zeros are rejected.
    public static bool TryParseId(string text, out int id)
    {
        id = 0;
        return text.Length is >= 1 and <= 9 && text[0] != '0' && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalMessages"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl))
        && (query["message"].Count == 0 || TryParseId(query["message"].ToString(), out _))
        && (query["folder"].Count == 0 || query["folder"].ToString() is "all" or "inbox" or "sent")
        && (query["q"].Count == 0 || ValidSearch(query["q"].ToString()))
        && NoticeKeys.Count(key => query[key].Count > 0) <= 1
        && (query["readall"].Count == 0 || query["readall"].ToString() == "1")
        && NoticeKeys.Where(key => key != "readall").All(key => query[key].Count == 0 || TryParseId(query[key].ToString(), out _));

    public static bool ValidSearch(string value) => value.Length is >= 2 and <= 40
        && value == value.Trim() && !value.Any(char.IsControl);

    public static (string Key, int Value)? NoticeOf(IQueryCollection query) =>
        NoticeKeys.FirstOrDefault(key => query[key].Count == 1) is { } key
            ? (key, key == "readall" ? 1 : TryParseId(query[key].ToString(), out var value) ? value : 0) : null;

    // Action POSTs carry exactly one canonical lower-case "action" key; query keys otherwise match case-insensitively.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Messages error</title></head><body><h1>Messages request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"{PageUrl}\">Return to Messages</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalMessagingStore messaging, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        var action = request.Query["action"].ToString();
        if (!Actions.Contains(action))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Messages actions must be submitted with POST.", ct);
            return;
        }
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Messages action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Messages.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Messages form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Messages form could not be read.", ct); return; }
        var sending = action == "internalMessageSend";
        var readingAll = action == "internalMessageReadAll";
        var targetField = action == "internalMessageDelete" ? "message" : "pilot";
        var valid = sending ? LocalHangarError.HasExactly(form, "to", "subject", "body", "rev", "key")
            : readingAll ? LocalHangarError.HasExactly(form, "rev", "key")
            : LocalHangarError.HasExactly(form, targetField, "rev", "key");
        if (!valid)
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var target = sending || readingAll ? "" : form[targetField].ToString();
        var revText = form["rev"].ToString();
        var key = form["key"].ToString();
        if (key.Length > 160 || revText.Length is < 1 or > 7 || !revText.All(char.IsAsciiDigit)
            || !int.TryParse(revText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision > MaxRevision)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var id = 0;
        if (!sending && !readingAll && !TryParseId(target, out id))
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var toPilot = 0;
        var subject = sending ? form["subject"].ToString().Trim() : "";
        var body = sending ? form["body"].ToString().Trim() : "";
        if (sending && (!TryParseId(form["to"].ToString(), out toPilot) || subject.Length is 0 or > LocalMessagingStore.MaxSubjectLength
            || body.Length > LocalMessagingStore.MaxBodyLength || subject.Any(char.IsControl)
            || body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))))
        { await ErrorAsync(response, 400, $"Enter a recipient, a subject of 1-{LocalMessagingStore.MaxSubjectLength} characters, and at most {LocalMessagingStore.MaxBodyLength} characters of text.", ct); return; }
        var now = clock.GetUtcNow();
        var check = CheckKey(session.Dosid, action, target, revision, key, now);
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload Messages and try again.", ct); return; }
        if (!sending && !readingAll && id == session.GameUserId && targetField == "pilot")
        { await ErrorAsync(response, 400, "You cannot add yourself as a friend.", ct); return; }
        var replayKey = key.Split('.')[0];
        var result = action switch
        {
            "internalMessageSend" => await messaging.SendAsync(session.AccountId, toPilot, subject, body, now, revision, replayKey, ct),
            "internalMessageDelete" => await messaging.DeleteMessageAsync(session.AccountId, id, revision, replayKey, ct),
            "internalMessageReadAll" => await messaging.MarkAllReadAsync(session.AccountId, revision, replayKey, ct),
            "internalFriendAdd" => await messaging.AddFriendAsync(session.AccountId, id, revision, replayKey, ct),
            _ => await messaging.RemoveFriendAsync(session.AccountId, id, revision, replayKey, ct)
        };
        switch (result)
        {
            case MessagingChange.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = PageUrl + action switch
                {
                    "internalMessageSend" => (await messaging.GetAsync(session.AccountId, now, ct)).Messages
                        .Where(message => message.Sent).Select(message => message.Id).DefaultIfEmpty(0).Max() is var sentId and > 0
                        ? $"&sent={sentId}" : "",
                    "internalMessageDelete" => $"&deleted={id}",
                    "internalMessageReadAll" => "&readall=1",
                    "internalFriendAdd" => $"&added={id}",
                    _ => $"&removed={id}"
                };
                return;
            case MessagingChange.Unchanged:
                await ErrorAsync(response, 409, "There are no unread messages.", ct);
                return;
            case MessagingChange.NotFound:
                await ErrorAsync(response, 404, sending || targetField == "pilot" ? "This pilot does not exist." : "This message does not exist.", ct);
                return;
            case MessagingChange.Full:
                await ErrorAsync(response, 409, sending ? "This mailbox has reached its message ID limit."
                    : $"Your friend list is full ({LocalMessagingStore.MaxFriends} pilots).", ct);
                return;
            case MessagingChange.Invalid:
                await ErrorAsync(response, 400, "Mail can only be sent to pilots on your friend list.", ct);
                return;
            default:
                await ErrorAsync(response, 409, "This form was already submitted or your messages have changed. Reload Messages.", ct);
                return;
        }
    }
}
