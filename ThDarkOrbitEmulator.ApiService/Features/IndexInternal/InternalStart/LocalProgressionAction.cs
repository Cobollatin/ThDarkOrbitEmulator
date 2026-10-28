using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Provisional menu actions; the installed client has no captured contract for these operations.
public static class LocalProgressionAction
{
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
    {
        "internalSkylabUpgradeStart", "internalSkylabUpgradeFinish", "internalSkylabRefineStart",
        "internalSkylabRefineClaim", "internalGalaxyGateAttack"
    };

    public static string FormKey(string dosid, string action, string target, string expected, DateTimeOffset now) =>
        LocalSkylabAction.CreateFormKey(dosid, $"{action}:{target}", expected, now);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalSkylabStore skylab, LocalGalaxyGateStore gates, EconomyStore economy, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var action = request.Query["action"].ToString();
        if (!HttpMethods.IsPost(request.Method) || request.Query.Count != 1 || request.Query.Keys.Single() != "action"
            || request.Query["action"].Count != 1
            || !Actions.Contains(action))
        { await ErrorAsync(context, 400, "Invalid progression action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(context, 401, "Your session has expired.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(context, 403, "Select a company first.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(context, 400, "Submit a form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        { await ErrorAsync(context, 400, "Invalid form.", ct); return; }
        var collectorAction = action is "internalSkylabUpgradeStart" or "internalSkylabUpgradeFinish";
        var waveAction = action == "internalGalaxyGateAttack";
        var target = collectorAction ? form["collector"].ToString() : waveAction ? form["gate"].ToString() : "refinery";
        var expected = action is "internalSkylabUpgradeStart" ? form["level"].ToString()
            : waveAction ? form["wave"].ToString() : "0";
        var fields = action switch
        {
            "internalSkylabUpgradeStart" => new[] { "collector", "level", "key" },
            "internalSkylabUpgradeFinish" => ["collector", "key"],
            "internalGalaxyGateAttack" => ["gate", "wave", "key"],
            _ => new[] { "key" }
        };
        var now = clock.GetUtcNow();
        if (!LocalHangarError.HasExactly(form, fields)
            || collectorAction && !LocalSkylab.IsCollector(target)
            || waveAction && !LocalGalaxyGates.Catalog.Any(g => g.Id == target)
            || action == "internalSkylabUpgradeStart" && (!int.TryParse(expected, out var level) || level is < 1 or > 9)
            || waveAction && (!int.TryParse(expected, out var wave) || wave is < 0 or >= EconomyStore.GalaxyGateWaveCount)
            || !LocalSkylabAction.ValidProgressionKey(session.Dosid, $"{action}:{target}",
                expected, form["key"].ToString(), now))
        { await ErrorAsync(context, 400, "The form is invalid or expired.", ct); return; }
        if (collectorAction || action is "internalSkylabRefineStart" or "internalSkylabRefineClaim")
            await skylab.GetAsync(session.AccountId, ct);
        if (waveAction) await gates.GetAsync(session.AccountId, ct);
        var key = $"progression:{form["key"]}";
        var result = action switch
        {
            "internalSkylabUpgradeStart" => await economy.StartCollectorUpgradeAsync(session.AccountId, target, int.Parse(expected), now, key, ct),
            "internalSkylabUpgradeFinish" => await economy.FinishCollectorUpgradeAsync(session.AccountId, target, now, key, ct),
            "internalSkylabRefineStart" => await economy.StartRefineryAsync(session.AccountId, now, key, ct),
            "internalSkylabRefineClaim" => await economy.ClaimRefineryAsync(session.AccountId, now, key, ct),
            _ => await economy.AttackGateNpcAsync(session.AccountId, target, int.Parse(expected), now, key, ct)
        };
        if (result != ProgressionChange.Applied)
        {
            await ErrorAsync(context, result == ProgressionChange.Invalid ? 400 : 409,
                result switch
                {
                    ProgressionChange.NotReady => "The job is still running.",
                    ProgressionChange.InsufficientResources => "Insufficient resources.",
                    _ => "The saved state changed or this action was already submitted."
                }, ct);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Location = action == "internalGalaxyGateAttack"
            ? "/indexInternal.es?action=internalGalaxyGates&gate=" + target
            : "/indexInternal.es?action=internalSkylab";
    }

    private static async Task ErrorAsync(HttpContext context, int status, string message, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=UTF-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync($"<!doctype html><html><body><p>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(message)}</p></body></html>", ct);
    }
}
