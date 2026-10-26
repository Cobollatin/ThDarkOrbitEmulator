using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-defined layout; no Live Pilot Sheet page body was captured.
public static class LocalPilotSheet
{
    private static readonly Dictionary<string, string> StatusLabels = new()
    {
        ["maxed"] = "Maximum rank", ["locked"] = "Locked", ["no-points"] = "No points left", ["available"] = "Can train"
    };

    // Menu-147: emulator-defined skill filters; a skill can match several (e.g. trained and maxed).
    public static readonly IReadOnlyList<(string Id, string Label)> Filters =
    [
        ("all", "All"), ("trainable", "Can train"), ("trained", "Trained"), ("maxed", "Maximum rank"), ("locked", "Locked")
    ];

    public static bool IsFilter(string? filter) => filter is null || Filters.Any(f => f.Id == filter);

    public static bool Matches(LocalPilotSkill skill, LocalPilotSkillState state, string? filter) => filter switch
    {
        "trainable" => LocalPilotSkillStore.CanAllocate(skill, state),
        "trained" => state.Ranks.GetValueOrDefault(skill.Id) > 0,
        "maxed" => state.Ranks.GetValueOrDefault(skill.Id) >= skill.MaxRank,
        "locked" => !LocalPilotSkillStore.PrerequisiteMet(skill, state),
        _ => true
    };

    private static string FilterSuffix(string? filter) => filter is null or "all" ? "" : $"&amp;filter={filter}";

    public static string ListHref(string? filter = null) => $"/indexInternal.es?action=internalPilotSheet{FilterSuffix(filter)}";

    public static string DetailHref(string skillId, string? filter = null) =>
        $"/indexInternal.es?action=internalPilotSheet&amp;detail={skillId}{FilterSuffix(filter)}";

    private static string FilterTabs(LocalPilotSkillState state, string? filter) =>
        "<nav id=\"pilot-skill-filters\">" + string.Join(" · ", Filters.Select(f =>
        {
            var count = LocalPilotSkillStore.Skills.Count(skill => Matches(skill, state, f.Id));
            var current = (filter ?? "all") == f.Id ? " aria-current=\"page\"" : "";
            return $"<a href=\"{ListHref(f.Id)}\" data-filter=\"{f.Id}\" data-count=\"{count}\"{current}>{f.Label} ({count})</a>";
        })) + "</nav>";

    private static string Effect(LocalPilotSkill skill, int rank) => $"+{skill.Bonus(rank)}% {skill.EffectUnit}";

    private static string AllocateForm(GameAuthSession session, LocalPilotSkill skill, LocalPilotSkillState state,
        DateTimeOffset issued, string? buttonId = null)
    {
        var disabled = LocalPilotSkillStore.CanAllocate(skill, state) ? "" : " disabled";
        var id = buttonId is null ? "" : $" id=\"{buttonId}\"";
        var key = LocalPilotSkillAction.CreateFormKey(session.Dosid, "allocate", skill.Id, state.Revision, issued);
        return "<form method=\"post\" action=\"/indexInternal.es?action=internalPilotSkillAllocate\">" +
            $"<input type=\"hidden\" name=\"skill\" value=\"{skill.Id}\"><input type=\"hidden\" name=\"rev\" value=\"{state.Revision}\">" +
            $"<input type=\"hidden\" name=\"key\" value=\"{key}\"><button type=\"submit\"{id}{disabled}>Allocate point</button></form>";
    }

    private static string Bonuses(LocalPilotSkillState state)
    {
        var trained = LocalPilotSkillStore.Skills.Where(skill => state.Ranks.GetValueOrDefault(skill.Id) > 0)
            .Select(skill => $"<li data-skill=\"{skill.Id}\">{skill.Name}: {Effect(skill, state.Ranks[skill.Id])}</li>").ToList();
        return trained.Count == 0
            ? "<p id=\"pilotSkillBonuses\">No skill bonuses yet.</p>"
            : $"<ul id=\"pilotSkillBonuses\">{string.Join("", trained)}</ul>";
    }

    // Requires/Unlocks links drop the filter because the linked skill may sit outside it; Previous/Next/Close keep it.
    private static string Detail(GameAuthSession session, LocalPilotSkill skill, LocalPilotSkillState state, DateTimeOffset issued,
        string? filter)
    {
        var visible = LocalPilotSkillStore.Skills.Where(other => Matches(other, state, filter)).ToList();
        var index = visible.IndexOf(skill);
        var previous = index > 0 ? $"<a id=\"detailPreviousSkill\" rel=\"prev\" href=\"{DetailHref(visible[index - 1].Id, filter)}\">Previous skill</a> · " : "";
        var following = index >= 0 && index < visible.Count - 1 ? $"<a id=\"detailNextSkill\" rel=\"next\" href=\"{DetailHref(visible[index + 1].Id, filter)}\">Next skill</a> · " : "";
        var rank = state.Ranks.GetValueOrDefault(skill.Id);
        var status = LocalPilotSkillStore.Status(skill, state);
        var next = rank >= skill.MaxRank ? "Maximum rank reached" : Effect(skill, rank + 1);
        var requires = skill.RequiresSkill is null ? "None" :
            $"<a href=\"{DetailHref(skill.RequiresSkill)}\">{LocalPilotSkillStore.Find(skill.RequiresSkill)!.Name}</a> rank {skill.RequiresRank} " +
            (LocalPilotSkillStore.PrerequisiteMet(skill, state) ? "(met)" : "(not met)");
        var unlocks = string.Join(", ", LocalPilotSkillStore.UnlockedBy(skill)
            .Select(other => $"<a href=\"{DetailHref(other.Id)}\">{other.Name}</a> at rank {other.RequiresRank}"));
        return $"<section id=\"pilotSkillDetail\" data-skill=\"{skill.Id}\" data-status=\"{status}\"><h2>{skill.Name}</h2><p>{skill.Description}</p>" +
            $"<dl><dt>Rank</dt><dd id=\"detailRank\">{rank} / {skill.MaxRank}</dd><dt>Effect per rank</dt><dd id=\"detailPerRank\">+{skill.EffectPerRank}% {skill.EffectUnit}</dd>" +
            $"<dt>Current bonus</dt><dd id=\"detailBonus\">{Effect(skill, rank)}</dd><dt>Next rank</dt><dd id=\"detailNext\">{next}</dd>" +
            $"<dt>Requires</dt><dd id=\"detailRequires\">{requires}</dd><dt>Unlocks</dt><dd id=\"detailUnlocks\">{(unlocks.Length == 0 ? "None" : unlocks)}</dd>" +
            $"<dt>Status</dt><dd id=\"detailStatus\">{StatusLabels[status]}</dd></dl>" +
            AllocateForm(session, skill, state, issued, "detailAllocate") +
            $"<p>{previous}{following}<a id=\"detailClose\" href=\"{ListHref(filter)}\">Close details</a></p></section>";
    }

    // Built from the saved ranks, never from the link: a training notice shows only while the skill
    // has at least one rank, and a reset notice only while no points are spent.
    public static string Notice(LocalPilotSkillState state, string? trained, bool reset)
    {
        if (reset)
            return state.Spent != 0 ? "" :
                $"<p id=\"pilotSkillReset\" role=\"status\">Skills reset. Available points: {state.Available} / {LocalPilotSkillState.TotalPoints}.</p>";
        if (trained is null || LocalPilotSkillStore.Find(trained) is not { } skill) return "";
        var rank = state.Ranks.GetValueOrDefault(skill.Id);
        if (rank == 0) return "";
        var unlocked = LocalPilotSkillStore.UnlockedBy(skill).Where(other => rank == other.RequiresRank).Select(other => other.Name).ToList();
        return $"<p id=\"pilotSkillTrained\" role=\"status\" data-skill=\"{skill.Id}\">{skill.Name} trained to rank {rank} / {skill.MaxRank}. " +
            $"Bonus: {Effect(skill, rank)}. Points left: {state.Available}." +
            (rank >= skill.MaxRank ? " Maximum rank reached." : "") +
            (unlocked.Count == 0 ? "" : $" Unlocked: {string.Join(", ", unlocked)}.") + "</p>";
    }

    public static string Page(GameAuthSession session, LocalPilotSkillState state, DateTimeOffset? issuedAt = null,
        LocalPilotSkill? detail = null, string? trained = null, bool wasReset = false, string? filter = null)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var shown = LocalPilotSkillStore.Skills.Where(skill => Matches(skill, state, filter)).ToList();
        var rows = string.Join("", shown.Select(skill =>
        {
            var rank = state.Ranks.GetValueOrDefault(skill.Id);
            var current = skill == detail ? " aria-current=\"true\"" : "";
            var requirement = skill.RequiresSkill is null ? "" :
                $"<br><small>Requires {LocalPilotSkillStore.Find(skill.RequiresSkill)!.Name} rank {skill.RequiresRank}</small>";
            return $"<tr data-skill=\"{skill.Id}\" data-status=\"{LocalPilotSkillStore.Status(skill, state)}\"{current}>" +
                $"<td><a href=\"{DetailHref(skill.Id, filter)}\">{skill.Name}</a>{requirement}</td><td>{skill.Description}</td>" +
                $"<td>{rank} / {skill.MaxRank}</td><td>{Effect(skill, rank)}</td><td>{AllocateForm(session, skill, state, issued)}</td></tr>";
        }));
        var reset = state.Spent == 0 ? "" :
            "<form method=\"post\" action=\"/indexInternal.es?action=internalPilotSkillReset\">" +
            $"<input type=\"hidden\" name=\"rev\" value=\"{state.Revision}\"><input type=\"hidden\" name=\"key\" value=\"{LocalPilotSkillAction.CreateFormKey(session.Dosid, "reset", "", state.Revision, issued)}\">" +
            "<button type=\"submit\">Reset skills</button></form>";
        var company = HtmlEncoder.Default.Encode(LocalCompanySelection.DisplayName(session.Company) ?? session.Company ?? "");
        var detailPanel = detail is null ? "" : Detail(session, detail, state, issued, filter);
        var label = Filters.First(f => f.Id == (filter ?? "all")).Label;
        var empty = shown.Count > 0 ? "" : $"<p id=\"pilotSkillEmpty\" role=\"status\">No skills match the {label} filter.</p>";
        var table = shown.Count == 0 ? "" :
            $"<table><thead><tr><th>Skill</th><th>Effect</th><th>Rank</th><th>Bonus</th><th>Training</th></tr></thead><tbody>{rows}</tbody></table>";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Pilot Sheet - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}tr[aria-current]{background:#0d2a3d}button{background:#1789b5;color:white;padding:8px;border:0}button:disabled{opacity:.5}</style></head><body><main id="pilotSheet"><h1>Pilot Sheet</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · ID {{session.GameUserId}}</p><p id="pilotCompany">Company: {{company}}</p><h2>Skill tree</h2><p>Available points: {{state.Available}} / {{LocalPilotSkillState.TotalPoints}}</p><p id="pilotSkillSpent">Spent points: {{state.Spent}}</p>{{Notice(state, trained, wasReset)}}{{FilterTabs(state, filter)}}{{empty}}{{table}}{{reset}}{{detailPanel}}<h2>Active bonuses</h2>{{Bonuses(state)}}<p><small>Skill training and resets are free and change no Credits, Uridium, or Hangar items.</small></p><p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
