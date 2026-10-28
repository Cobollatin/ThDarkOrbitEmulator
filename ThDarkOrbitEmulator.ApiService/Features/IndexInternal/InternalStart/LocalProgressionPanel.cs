using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalProgressionPanel
{
    private static string Form(GameAuthSession session, string action, string target, string expected,
        string fields, string label, DateTimeOffset now) =>
        $"<form method=\"post\" action=\"/indexInternal.es?action={action}\">{fields}" +
        $"<input type=\"hidden\" name=\"key\" value=\"{LocalProgressionAction.FormKey(session.Dosid, action, target, expected, now)}\">" +
        $"<button type=\"submit\">{label}</button></form>";

    public static async Task<string> SkylabAsync(GameAuthSession session, LocalSkylabState state,
        EconomyStore economy, DateTimeOffset now, CancellationToken ct)
    {
        var jobs = await economy.CollectorUpgradeJobsAsync(session.AccountId, ct);
        var refinery = await economy.RefineryJobAsync(session.AccountId, ct);
        var html = new StringBuilder("<section id=\"skylabProgression\"><h2>Timed upgrades and refinery</h2><p>Local provisional rules: upgrades take one minute per current level; a refinery batch uses 10 Prometium and 10 Endurium to make 10 Prometid after five minutes.</p>");
        foreach (var collector in state.Collectors)
        {
            var job = jobs.FirstOrDefault(j => j.CollectorId == collector.Id);
            var id = HtmlEncoder.Default.Encode(collector.Id);
            html.Append($"<p>{HtmlEncoder.Default.Encode(collector.Name)}: ");
            if (job is not null)
            {
                html.Append($"upgrade to level {job.ExpectedLevel + 1} ready at {job.ReadyAt:u}</p>");
                if (now >= job.ReadyAt) html.Append(Form(session, "internalSkylabUpgradeFinish", collector.Id, "0",
                    $"<input type=\"hidden\" name=\"collector\" value=\"{id}\">", "Finish upgrade", now));
            }
            else
            {
                html.Append("no timed upgrade running.</p>");
                if (collector.Level < LocalSkylab.MaxLevel) html.Append(Form(session, "internalSkylabUpgradeStart", collector.Id,
                    collector.Level.ToString(), $"<input type=\"hidden\" name=\"collector\" value=\"{id}\"><input type=\"hidden\" name=\"level\" value=\"{collector.Level}\">",
                    $"Start timed upgrade ({collector.Level * 100} {HtmlEncoder.Default.Encode(collector.Name)})", now));
            }
        }
        if (refinery is null) html.Append(Form(session, "internalSkylabRefineStart", "refinery", "0", "", "Start refinery batch", now));
        else
        {
            html.Append($"<p>Refinery batch ready at {refinery.ReadyAt:u}.</p>");
            if (now >= refinery.ReadyAt) html.Append(Form(session, "internalSkylabRefineClaim", "refinery", "0", "", "Claim refinery batch", now));
        }
        return html.Append("</section>").ToString();
    }

    public static async Task<string> GatesAsync(GameAuthSession session, LocalGalaxyGateState state,
        EconomyStore economy, DateTimeOffset now, CancellationToken ct)
    {
        var html = new StringBuilder("<section id=\"gateWaves\"><h2>Gate waves</h2><p>Local provisional progression: three waves per activated gate.</p>");
        foreach (var gate in state.Gates.Where(g => g.Activated))
        {
            var run = await economy.GateRunAsync(session.AccountId, gate.Id, ct);
            if (run is null) continue;
            html.Append($"<p>{HtmlEncoder.Default.Encode(gate.Name)}: {run.Wave}/{run.TotalWaves} waves complete.</p>");
            if (run.CompletedAt is null) html.Append(Form(session, "internalGalaxyGateWave", gate.Id, run.Wave.ToString(),
                $"<input type=\"hidden\" name=\"gate\" value=\"{gate.Id}\"><input type=\"hidden\" name=\"wave\" value=\"{run.Wave}\">",
                "Complete next wave", now));
        }
        return html.Append("</section>").ToString();
    }
}
