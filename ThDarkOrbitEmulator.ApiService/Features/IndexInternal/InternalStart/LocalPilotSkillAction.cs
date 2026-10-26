using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalPilotSkillAction
{
    // Emulator-defined form lifetime; stale pages must be reloaded before submitting.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxRevision = 1_000_000;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "detail"];
    private const int MaxPageValueLength = 64;

    private enum KeyCheck { Valid, Invalid, Expired }

    public static string CreateFormKey(string dosid, string action, string skillId, int revision, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, action, skillId, revision, nonce, issued)}";
    }

    private static string Sign(string dosid, string action, string skillId, int revision, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"pilot:{action}:{skillId}:{revision}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string action, string skillId, int revision, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, action, skillId, revision, parts[0], parts[1])))) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture));
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }

    // Skill ids are a lowercase ASCII letter followed by ASCII letters or digits, e.g. shieldEngineering.
    public static bool IsWellFormedSkillId(string skillId) =>
        skillId.Length is >= 1 and <= MaxPageValueLength && char.IsAsciiLetterLower(skillId[0])
        && skillId.All(char.IsAsciiLetterOrDigit);

    // The page accepts the shell's navigation parameters once each, plus one of the lowercase
    // post-action notices `trained` (a well-formed skill id) or `reset` (exactly "1"); anything else is rejected.
    // Menu-147: optional exact-case `filter` is 1-16 lowercase ASCII letters; unknown names are 404 after the session.
    // Menu-201: all page keys and the action value use their canonical spelling.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query.Keys.Contains("action", StringComparer.Ordinal) && query["action"].Count == 1
        && query["action"].ToString() == "internalPilotSheet"
        && !(query.ContainsKey("trained") && query.ContainsKey("reset"))
        && query.All(pair => (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal) || pair.Key is "trained" or "reset" or "filter")
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key != "trained" || IsWellFormedSkillId(pair.Value.ToString()))
            && (pair.Key != "reset" || pair.Value.ToString() == "1")
            && (pair.Key != "filter" || pair.Value.ToString() is { Length: >= 1 and <= 16 } filter && filter.All(char.IsAsciiLetterLower)))
        && (!query.ContainsKey("detail") || query["detail"].ToString().Length > 0);

    // Successful changes return to the sheet with an emulator-defined notice parameter.
    public static string SuccessLocation(string? trainedSkillId) => trainedSkillId is null
        ? "/indexInternal.es?action=internalPilotSheet&reset=1"
        : $"/indexInternal.es?action=internalPilotSheet&trained={trainedSkillId}";

    // A known skill id (exact casing) selects the detail panel; unknown ids are 404 after the session checks.
    public static bool TryFindDetail(IQueryCollection query, out LocalPilotSkill? skill)
    {
        skill = null;
        if (!query.ContainsKey("detail")) return true;
        skill = LocalPilotSkillStore.Find(query["detail"].ToString());
        return skill is not null;
    }

    // Menu-147: a well-formed but unknown filter is 404; null means the default `all`.
    public static bool TryFindFilter(IQueryCollection query, out string? filter)
    {
        filter = query.ContainsKey("filter") ? query["filter"].ToString() : null;
        return LocalPilotSheet.IsFilter(filter);
    }

    // Action forms post to exactly one "action" parameter, with the key and value in canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Pilot Sheet error</title></head><body><h1>Pilot Sheet request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalPilotSheet\">Return to Pilot Sheet</a></body></html>", ct);
    }

    public static Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalPilotSkillStore skills, CancellationToken ct) => HandleAsync(context, sessions, skills, TimeProvider.System, ct);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalPilotSkillStore skills, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        var action = request.Query["action"].ToString();
        if (action is not ("internalPilotSkillAllocate" or "internalPilotSkillReset"))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Pilot Sheet actions must be submitted with POST.", ct);
            return;
        }
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Pilot Sheet action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using the Pilot Sheet.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Pilot Sheet form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Pilot Sheet form could not be read.", ct); return; }
        var allocating = action == "internalPilotSkillAllocate";
        var skillId = allocating ? form["skill"].ToString() : "";
        var valid = allocating ? LocalHangarError.HasExactly(form, "skill", "rev", "key")
            : LocalHangarError.HasExactly(form, "rev", "key");
        if (!valid)
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var revText = form["rev"].ToString();
        var key = form["key"].ToString();
        if (skillId.Length > 64 || key.Length > 160 || revText.Length is < 1 or > 7 || !revText.All(char.IsAsciiDigit)
            || !int.TryParse(revText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision > MaxRevision)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var check = CheckKey(session.Dosid, allocating ? "allocate" : "reset", skillId, revision, key, clock.GetUtcNow());
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload the Pilot Sheet and try again.", ct); return; }
        if (allocating && LocalPilotSkillStore.Find(skillId) is null)
        { await ErrorAsync(response, 404, "This skill does not exist.", ct); return; }
        var replayKey = key.Split('.')[0];
        var result = allocating
            ? await skills.AllocateAsync(session.AccountId, skillId, revision, replayKey, ct)
            : await skills.ResetAsync(session.AccountId, revision, replayKey, ct);
        await ApplyAsync(response, result, result == PilotSkillChange.Conflict
            ? RuleConflict(await skills.GetAsync(session.AccountId, ct), revision, LocalPilotSkillStore.Find(skillId))
            : null, allocating ? skillId : null, ct);
    }

    // A conflict at the submitted revision is a rule violation, not a stale or replayed form; name the rule.
    public static string? RuleConflict(LocalPilotSkillState state, int revision, LocalPilotSkill? skill)
    {
        if (state.Revision != revision) return null;
        if (skill is null) return state.Spent == 0 ? "There are no spent skill points to reset." : null;
        return LocalPilotSkillStore.Status(skill, state) switch
        {
            "maxed" => $"{skill.Name} is already at its maximum rank.",
            "locked" => $"{skill.Name} requires {LocalPilotSkillStore.Find(skill.RequiresSkill!)?.Name ?? skill.RequiresSkill} rank {skill.RequiresRank}.",
            "no-points" => "You have no skill points left. Reset your skills to redistribute them.",
            _ => null
        };
    }

    private static async Task ApplyAsync(HttpResponse response, PilotSkillChange result, string? ruleMessage,
        string? trainedSkillId, CancellationToken ct)
    {
        if (result == PilotSkillChange.Conflict)
        { await ErrorAsync(response, 409, ruleMessage ?? "This form was already submitted or your skills have changed. Reload the Pilot Sheet.", ct); return; }
        if (result != PilotSkillChange.Applied)
        { await ErrorAsync(response, 400, "This skill change is not allowed.", ct); return; }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = SuccessLocation(trainedSkillId);
    }
}
