using System.Text.Encodings.Web;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only directory and membership. No post-company Clan contract was captured.
// Member counts and recruiting flags are emulator-defined display data.
public sealed record LocalClanEntry(string Id, string Tag, string Name, string Company, string Description,
    int Members = 0, bool Recruiting = true);

public sealed class LocalClanStore(IDistributedCache cache, EconomyStore? economy = null)
{
    public static readonly IReadOnlyList<LocalClanEntry> Directory =
    [
        new("mmo-guard", "MG", "Mars Guard", "mmo", "Patrols and shared missions", 42),
        new("mmo-orbit", "MO", "Mars Orbit", "mmo", "Exploration and equipment", 17),
        new("mmo-traders", "MT", "Mars Traders", "mmo", "Trade routes and cargo escorts", 8),
        new("mmo-elite", "ME", "Mars Elite", "mmo", "Invitation only", 50, false),
        new("eic-vanguard", "EV", "Earth Vanguard", "eic", "Patrols and shared missions", 39),
        new("eic-orbit", "EO", "Earth Orbit", "eic", "Exploration and equipment", 21),
        new("eic-traders", "ET", "Earth Traders", "eic", "Trade routes and cargo escorts", 11),
        new("eic-elite", "EE", "Earth Elite", "eic", "Invitation only", 50, false),
        new("vru-vanguard", "VV", "Venus Vanguard", "vru", "Patrols and shared missions", 36),
        new("vru-orbit", "VO", "Venus Orbit", "vru", "Exploration and equipment", 19),
        new("vru-traders", "VT", "Venus Traders", "vru", "Trade routes and cargo escorts", 9),
        new("vru-elite", "VE", "Venus Elite", "vru", "Invitation only", 50, false),
        new("tst-vanguard", "TV", "Test Vanguard", "tst", "Local test pilots", 1)
    ];

    public static LocalClanEntry? Find(string? clanId) => Directory.SingleOrDefault(entry => entry.Id == clanId);

    public Task<bool> IsLeaderAsync(Guid accountId, string clanId, CancellationToken ct) =>
        economy?.IsClanLeaderAsync(accountId, clanId, ct) ?? Task.FromResult(false);

    public Task<IReadOnlyList<ClanRelation>> RelationsAsync(string clanId, CancellationToken ct) =>
        economy?.ClanRelationsAsync(clanId, ct) ?? Task.FromResult<IReadOnlyList<ClanRelation>>([]);

    public Task<IReadOnlyList<ClanStationModule>> StationAsync(string clanId, CancellationToken ct) =>
        economy?.ClanStationAsync(clanId, ct) ?? Task.FromResult<IReadOnlyList<ClanStationModule>>([]);

    public Task<ClanOperationChange> RelationAsync(Guid accountId, string clanId, string otherClanId,
        string action, string key, CancellationToken ct) =>
        economy?.ChangeClanRelationAsync(accountId, clanId, otherClanId, action, key, ct)
        ?? Task.FromResult(ClanOperationChange.Invalid);

    public Task<ClanOperationChange> UpgradeAsync(Guid accountId, string clanId, string moduleId,
        string key, DateTimeOffset now, CancellationToken ct) =>
        economy?.UpgradeClanModuleAsync(accountId, clanId, moduleId, key, now, ct)
        ?? Task.FromResult(ClanOperationChange.Invalid);

    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private static string Key(Guid accountId) => $"localclan:{accountId:N}";

    public async Task<string?> GetMembershipAsync(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return null;
        if (economy is null) return await cache.GetStringAsync(Key(accountId), ct);
        var legacy = await cache.GetStringAsync(Key(accountId), ct);
        if (legacy is not null && Directory.Any(entry => entry.Id == legacy))
        {
            await economy.ImportClanMembershipAsync(accountId, legacy, ct);
            await cache.RemoveAsync(Key(accountId), ct);
        }
        return await economy.ClanMembershipAsync(accountId, ct);
    }

    public async Task<ClanChange> ChangeAsync(Guid accountId, string company, string? clanId,
        string? expectedClanId, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || clanId is not null &&
            !Directory.Any(clan => clan.Id == clanId && clan.Company == company && clan.Recruiting))
            return ClanChange.Invalid;
        if (economy is not null)
        {
            await GetMembershipAsync(accountId, ct);
            return await economy.ChangeClanAsync(accountId, clanId, expectedClanId, key, ct);
        }
        await MutationLock.WaitAsync(ct);
        try
        {
            var replayKey = $"localclan-action:{accountId:N}:{key}";
            if (await cache.GetStringAsync(replayKey, ct) is not null ||
                await GetMembershipAsync(accountId, ct) != expectedClanId ||
                clanId == expectedClanId || clanId is not null && expectedClanId is not null)
                return ClanChange.Conflict;
            if (clanId is null) await cache.RemoveAsync(Key(accountId), ct);
            else await cache.SetStringAsync(Key(accountId), clanId,
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            await cache.SetStringAsync(replayKey, "1", new DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30) }, ct);
            return ClanChange.Applied;
        }
        finally { MutationLock.Release(); }
    }

    public async Task<bool> JoinAsync(Guid accountId, string company, string clanId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !Directory.Any(clan => clan.Id == clanId && clan.Company == company && clan.Recruiting))
            return false;
        if (economy is not null)
            return await ChangeAsync(accountId, company, clanId, null, $"legacy:{Guid.NewGuid():N}", ct) == ClanChange.Applied;
        await MutationLock.WaitAsync(ct);
        try
        {
            if (await GetMembershipAsync(accountId, ct) is not null) return false;
            await cache.SetStringAsync(Key(accountId), clanId,
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            return true;
        }
        finally { MutationLock.Release(); }
    }

    public async Task<bool> LeaveAsync(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return false;
        if (economy is not null)
        {
            var current = await GetMembershipAsync(accountId, ct);
            var entry = Directory.SingleOrDefault(clan => clan.Id == current);
            return entry is not null && await ChangeAsync(accountId,
                entry.Company, null, current,
                $"legacy:{Guid.NewGuid():N}", ct) == ClanChange.Applied;
        }
        await MutationLock.WaitAsync(ct);
        try
        {
            if (await GetMembershipAsync(accountId, ct) is null) return false;
            await cache.RemoveAsync(Key(accountId), ct);
            return true;
        }
        finally { MutationLock.Release(); }
    }
}

public static class LocalClan
{
    // Emulator-defined directory filters; an empty value means "all".
    public static readonly IReadOnlyList<string> Filters = ["all", "recruiting", "closed"];

    public static bool IsFilter(string value) => value.Length == 0 || Filters.Contains(value);

    // Emulator-defined search: 1-32 ASCII letters, digits, spaces or hyphens, no edge spaces.
    public static bool IsWellFormedSearch(string? search) =>
        search is { Length: >= 1 and <= 32 }
        && search == search.Trim()
        && search.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-');

    // Case-insensitive match on id, tag, name or focus text.
    public static bool MatchesSearch(LocalClanEntry entry, string? search) =>
        string.IsNullOrEmpty(search)
        || entry.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase);

    // Details open only for directory clans of the pilot's current company that match the search; others are 404.
    public static bool HasDetail(string? company, string clanId, string? search = null) =>
        company is not null && LocalClanStore.Find(clanId) is { } entry && entry.Company == company
        && MatchesSearch(entry, search);

    private static bool Matches(LocalClanEntry entry, string filter) => filter switch
    {
        "recruiting" => entry.Recruiting,
        "closed" => !entry.Recruiting,
        _ => true
    };

    private static string Link(string filter, string? detail, string? search = null)
    {
        var href = "/indexInternal.es?action=internalClan";
        if (filter != "all") href += $"&amp;filter={filter}";
        if (search is not null) href += $"&amp;q={Uri.EscapeDataString(search)}";
        if (detail is not null) href += $"&amp;detail={detail}";
        return href;
    }

    private static string JoinForm(GameAuthSession session, LocalClanEntry entry, string? membership, DateTimeOffset issuedAt) =>
        "<form method=\"post\" action=\"/indexInternal.es?action=internalClanJoin\">" +
        $"<input type=\"hidden\" name=\"clan\" value=\"{entry.Id}\"><input type=\"hidden\" name=\"key\" value=\"{LocalClanAction.CreateFormKey(session.Dosid, "join", entry.Id, issuedAt)}\"><button type=\"submit\"" +
        (membership is not null ? " disabled" : "") + ">Join</button></form>";

    private static string LeaveForm(GameAuthSession session, string membership, DateTimeOffset issuedAt) =>
        $"<form method=\"post\" action=\"/indexInternal.es?action=internalClanLeave\"><input type=\"hidden\" name=\"clan\" value=\"{HtmlEncoder.Default.Encode(membership)}\"><input type=\"hidden\" name=\"key\" value=\"{LocalClanAction.CreateFormKey(session.Dosid, "leave", membership, issuedAt)}\"><button type=\"submit\">Leave clan</button></form>";

    // Post-action notices are built from the saved membership, never from the link text:
    // "joined" shows only while the pilot belongs to that clan, "left" only once they no longer do.
    public static string Notice(string? company, string? membership, string? joined, string? left)
    {
        if (!string.IsNullOrEmpty(joined) && joined == membership &&
            LocalClanStore.Find(joined) is { } clan && clan.Company == company)
            return "<p id=\"clan-notice\" role=\"status\" data-notice=\"joined\">" +
                $"You joined [{HtmlEncoder.Default.Encode(clan.Tag)}] {HtmlEncoder.Default.Encode(clan.Name)}. Members: {clan.Members + 1}.</p>";
        if (!string.IsNullOrEmpty(left) && left != membership && LocalClanAction.IsWellFormedClanId(left))
        {
            var name = LocalClanStore.Find(left) is { } former
                ? $"[{HtmlEncoder.Default.Encode(former.Tag)}] {HtmlEncoder.Default.Encode(former.Name)}"
                : "your previous clan";
            var next = membership is null ? " You can join a recruiting clan now." : "";
            return $"<p id=\"clan-notice\" role=\"status\" data-notice=\"left\">You left {name}.{next}</p>";
        }
        return "";
    }

    public static string Page(GameAuthSession session, string? membership, DateTimeOffset? now = null,
        string? detail = null, string? filter = null, string? joined = null, string? left = null,
        string? search = null, string? operations = null)
    {
        search = string.IsNullOrEmpty(search) ? null : search;
        var company = session.Company ?? "";
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var activeFilter = string.IsNullOrEmpty(filter) ? "all" : filter;
        var clan = LocalClanStore.Directory.SingleOrDefault(entry => entry.Id == membership && entry.Company == company);
        // A membership from a previous company (or a retired clan id) stays visible so it can be left.
        var current = clan is not null
            ? $"Current clan: [{HtmlEncoder.Default.Encode(clan.Tag)}] {HtmlEncoder.Default.Encode(clan.Name)}"
            : membership is null ? "No clan joined"
            : $"Current clan {HtmlEncoder.Default.Encode(membership)} is not available for your company. Leave it to join another.";
        var companyClans = LocalClanStore.Directory.Where(entry => entry.Company == company).ToList();
        var found = companyClans.Where(entry => MatchesSearch(entry, search)).ToList();
        var summary = $"{companyClans.Count(entry => entry.Recruiting)} of {companyClans.Count} clans recruiting";
        var tabs = string.Join(" · ", Filters.Select(name =>
        {
            var label = $"{char.ToUpperInvariant(name[0])}{name[1..]} ({found.Count(entry => Matches(entry, name))})";
            return name == activeFilter
                ? $"<strong aria-current=\"page\" data-filter=\"{name}\">{label}</strong>"
                : $"<a href=\"{Link(name, null, search)}\" data-filter=\"{name}\">{label}</a>";
        }));
        var shown = found.Where(entry => Matches(entry, activeFilter)).ToList();
        var encodedSearch = HtmlEncoder.Default.Encode(search ?? "");
        var searchForm = "<form id=\"clan-search\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalClan\">" +
            (activeFilter == "all" ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{activeFilter}\">") +
            $"<label for=\"clan-query\">Search clans by tag, name or focus</label> <input id=\"clan-query\" name=\"q\" maxlength=\"32\" pattern=\"[A-Za-z0-9 -]+\" required value=\"{encodedSearch}\"><button type=\"submit\">Search</button></form>" +
            (search is null ? "" : $"<p id=\"clan-search-result\">{shown.Count} {(shown.Count == 1 ? "clan matches" : "clans match")} \"{encodedSearch}\". <a id=\"clan-clear-search\" href=\"{Link(activeFilter, null)}\">Clear search</a></p>");
        var rows = shown.Count == 0
            ? $"<tr><td colspan=\"4\" id=\"clan-empty\">No clans match this {(search is null ? "filter" : "search")}.</td></tr>"
            : string.Join("", shown.Select(entry =>
                $"<tr data-clan=\"{entry.Id}\"><td><a href=\"{Link(activeFilter, entry.Id, search)}\">[{entry.Tag}] {HtmlEncoder.Default.Encode(entry.Name)}</a></td><td>{HtmlEncoder.Default.Encode(entry.Description)}</td><td>{entry.Members + (entry.Id == membership ? 1 : 0)}</td>" +
                (!entry.Recruiting ? "<td>Not recruiting</td></tr>" : $"<td>{JoinForm(session, entry, membership, issuedAt)}</td></tr>")));
        var leave = membership is null ? "" : LeaveForm(session, membership, issuedAt);
        var details = detail is null ? "" : Details(session, membership, companyClans, found, detail, activeFilter, search, issuedAt);
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Clan - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}#clan-detail{border:1px solid #4c7896;padding:12px;margin:12px 0}</style></head><body><main id="clan"><h1>Clan</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · {{current}}</p>{{Notice(company, membership, joined, left)}}<p id="clan-summary">{{summary}}</p>{{leave}}{{searchForm}}<nav id="clan-filters">{{tabs}}</nav>{{details}}{{operations}}<table><thead><tr><th>Clan</th><th>Focus</th><th>Members</th><th>Membership</th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }

    // Detail values are emulator-defined: member share and size rank derive from the company directory;
    // position and Previous/Next walk the search results (all company clans without a search).
    private static string Details(GameAuthSession session, string? membership, List<LocalClanEntry> companyClans,
        List<LocalClanEntry> found, string detail, string filter, string? search, DateTimeOffset issuedAt)
    {
        var index = found.FindIndex(entry => entry.Id == detail);
        if (index < 0) return "";
        var entry = found[index];
        int Members(LocalClanEntry item) => item.Members + (item.Id == membership ? 1 : 0);
        var members = Members(entry);
        var total = companyClans.Sum(Members);
        var share = total == 0 ? 0 : (int)Math.Round(100.0 * members / total, MidpointRounding.AwayFromZero);
        var rank = companyClans.Count(item => Members(item) > members) + 1;
        var companyName = CompanyCatalog.Find(entry.Company)?.DisplayName ?? entry.Company;
        var (membershipText, action) = entry.Id == membership
            ? ("You are a member of this clan.", LeaveForm(session, membership, issuedAt))
            : !entry.Recruiting ? ("This clan is not recruiting.", "")
            : membership is not null ? ("Leave your current clan before joining this one.", "")
            : ($"Joining makes you member {members + 1}.", JoinForm(session, entry, membership, issuedAt));
        var previous = index > 0 ? $"<a href=\"{Link(filter, found[index - 1].Id, search)}\" rel=\"prev\">Previous</a> · " : "";
        var next = index < found.Count - 1 ? $"<a href=\"{Link(filter, found[index + 1].Id, search)}\" rel=\"next\">Next</a> · " : "";
        return $"<section id=\"clan-detail\" data-clan=\"{entry.Id}\"><h2>[{entry.Tag}] {HtmlEncoder.Default.Encode(entry.Name)}</h2>" +
            $"<p id=\"clan-detail-position\">Clan {index + 1} of {found.Count}</p><dl>" +
            $"<dt>Company</dt><dd id=\"clan-detail-company\">{HtmlEncoder.Default.Encode(companyName)}</dd>" +
            $"<dt>Focus</dt><dd>{HtmlEncoder.Default.Encode(entry.Description)}</dd>" +
            $"<dt>Members</dt><dd id=\"clan-detail-members\">{members}</dd>" +
            $"<dt>Share of company clan pilots</dt><dd id=\"clan-detail-share\">{share}%</dd>" +
            $"<dt>Size rank</dt><dd id=\"clan-detail-rank\">{rank} of {companyClans.Count}</dd>" +
            $"<dt>Recruiting</dt><dd id=\"clan-detail-recruiting\">{(entry.Recruiting ? "Yes" : "No")}</dd></dl>" +
            $"<p id=\"clan-detail-membership\">{membershipText}</p>{action}" +
            $"<p>{previous}{next}<a href=\"{Link(filter, null, search)}\">Close</a></p></section>";
    }
}
