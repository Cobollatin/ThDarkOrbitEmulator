using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Provisional menu contract: the first member is leader; only that account can change diplomacy
// or construct a module. Relations are company scoped; modules have three levels and five-minute timers.
public static class LocalClanOperations
{
    private static readonly string[] RelationActions = ["propose", "accept", "reject", "war", "end"];
    private static readonly string[] Modules = ["hull", "defense", "relay"];

    private static string Form(GameAuthSession session, string clanId, string verb, string target,
        DateTimeOffset now, string label) =>
        $"<form method=\"post\" action=\"/indexInternal.es?action=internalClanOperate\">" +
        $"<input type=\"hidden\" name=\"clan\" value=\"{clanId}\">" +
        $"<input type=\"hidden\" name=\"verb\" value=\"{verb}\">" +
        $"<input type=\"hidden\" name=\"target\" value=\"{target}\">" +
        $"<input type=\"hidden\" name=\"key\" value=\"{LocalClanAction.CreateFormKey(session.Dosid, verb, clanId + ":" + target, now)}\">" +
        $"<button type=\"submit\">{HtmlEncoder.Default.Encode(label)}</button></form>";

    public static async Task<string> PanelAsync(GameAuthSession session, string? membership,
        LocalClanStore store, DateTimeOffset now, CancellationToken ct)
    {
        if (membership is null || LocalClanStore.Find(membership)?.Company != session.Company) return "";
        var leader = await store.IsLeaderAsync(session.AccountId, membership, ct);
        var relations = await store.RelationsAsync(membership, ct);
        var station = await store.StationAsync(membership, ct);
        var relationRows = string.Join("", relations.Select(relation =>
        {
            var other = LocalClanStore.Find(relation.OtherClanId);
            var name = other is null ? relation.OtherClanId : $"[{other.Tag}] {other.Name}";
            var action = relation.Status == "pending"
                ? relation.Proposer == membership ? "" :
                    Form(session, membership, "accept", relation.OtherClanId, now, "Accept alliance") +
                    Form(session, membership, "reject", relation.OtherClanId, now, "Reject")
                : Form(session, membership, "end", relation.OtherClanId, now, "End relation") +
                    (relation.Status == "allied" ? Form(session, membership, "war", relation.OtherClanId, now, "Declare war") : "");
            return $"<li>{HtmlEncoder.Default.Encode(name)}: {relation.Status}{(leader ? action : "")}</li>";
        }));
        var candidates = leader ? string.Join("", LocalClanStore.Directory
            .Where(entry => entry.Company == session.Company && entry.Id != membership &&
                relations.All(relation => relation.OtherClanId != entry.Id))
            .Select(entry => $"<li>{HtmlEncoder.Default.Encode(entry.Name)}" +
                Form(session, membership, "propose", entry.Id, now, "Propose alliance") +
                Form(session, membership, "war", entry.Id, now, "Declare war") + "</li>")) : "";
        var moduleRows = string.Join("", Modules.Select(module =>
        {
            var state = station.SingleOrDefault(item => item.ModuleId == module);
            var level = state?.Level ?? 0;
            var ready = state?.ReadyAt > now;
            var timer = ready ? $" · Ready {state!.ReadyAt:O}" : "";
            return $"<li>{module}: level {level}{timer}" +
                (leader && level < 3 && !ready ? Form(session, membership, "upgrade", module, now, "Build next level") : "") + "</li>";
        }));
        return $"<section id=\"clan-operations\"><h2>Diplomacy</h2><p>{(leader ? "Clan leader" : "Clan member")}</p><ul>{relationRows}{candidates}</ul>" +
            $"<h2>Station modules</h2><ul>{moduleRows}</ul></section>";
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalClanStore store, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        { response.Headers.Allow = "POST"; await LocalClanAction.ErrorAsync(response, 405, "Use POST.", ct); return; }
        if (!LocalClanAction.ValidActionQuery(request.Query) || request.Query["action"].ToString() != "internalClanOperate")
        { await LocalClanAction.ErrorAsync(response, 400, "Invalid Clan action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid) ||
            await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await LocalClanAction.ErrorAsync(response, 401, "Sign in again.", ct); return; }
        if (session.Company is null || !request.HasFormContentType)
        { await LocalClanAction.ErrorAsync(response, 400, "Invalid Clan form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await LocalClanAction.ErrorAsync(response, 400, "Invalid Clan form.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "clan", "verb", "target", "key"))
        { await LocalClanAction.ErrorAsync(response, 400, "Invalid Clan fields.", ct); return; }
        var clan = form["clan"].ToString();
        var verb = form["verb"].ToString();
        var target = form["target"].ToString();
        var key = form["key"].ToString();
        if (LocalClanStore.Find(clan)?.Company != session.Company ||
            !(RelationActions.Contains(verb) && LocalClanStore.Find(target)?.Company == session.Company && target != clan
                || verb == "upgrade" && Modules.Contains(target)) ||
            !LocalClanAction.IsValidOperationKey(session.Dosid, verb, clan + ":" + target, key, clock.GetUtcNow()))
        { await LocalClanAction.ErrorAsync(response, 400, "Invalid or expired Clan form.", ct); return; }
        if (await store.GetMembershipAsync(session.AccountId, ct) != clan)
        { await LocalClanAction.ErrorAsync(response, 403, "Join this clan first.", ct); return; }
        var result = verb == "upgrade"
            ? await store.UpgradeAsync(session.AccountId, clan, target, key, clock.GetUtcNow(), ct)
            : await store.RelationAsync(session.AccountId, clan, target, verb, key, ct);
        if (result != ClanOperationChange.Applied)
        {
            await LocalClanAction.ErrorAsync(response, result == ClanOperationChange.Forbidden ? 403 :
                result == ClanOperationChange.Conflict ? 409 : 400, "Clan operation was rejected.", ct);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = "/indexInternal.es?action=internalClan";
    }
}
