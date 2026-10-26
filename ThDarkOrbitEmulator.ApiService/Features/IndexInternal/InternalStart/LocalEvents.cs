using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only event rules. IL2CPP supplies path prefixes, not this page or economy.
public sealed record LocalBattlePassTier(string Id, string Name, int Target, int RewardCredits,
    Func<LocalEquipmentState, int> Progress);

public enum LocalEventResult { Applied, UnknownTier, Unavailable, AlreadyClaimed, NotReady, InsufficientCredits, InventoryFull, Rejected }

public static class LocalEvents
{
    public const int ExpeditionCost = 100;
    public const int ExpeditionMinutes = 2;
    public const string ExpeditionRewardItemId = "shield_sg3n_b02";

    public static readonly IReadOnlyList<LocalBattlePassTier> Tiers =
    [
        new("supplier", "Buy one Shop item", 1, 200, state => state.PurchasedItems),
        new("engineer", "Craft one Assembly item", 1, 300, state => state.CraftedItems),
        // Emulator-defined higher tiers; no Live battle pass levels or rewards were captured.
        new("quartermaster", "Buy three Shop items", 3, 400, state => state.PurchasedItems),
        new("artisan", "Craft three Assembly items", 3, 600, state => state.CraftedItems),
        new("tycoon", "Buy ten Shop items", 10, 1000, state => state.PurchasedItems)
    ];

    public static bool IsTierIdShape(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    public static string InProgress(long remaining) => $"In progress ({remaining} more needed)";

    // Whole minutes until the expedition is ready, rounded up so "0 min left" never shows before it is claimable.
    public static int MinutesLeft(DateTimeOffset readyAt, DateTimeOffset now) =>
        Math.Max(1, (int)Math.Ceiling((readyAt - now).TotalMinutes));

    public static bool HasTier(string id, IReadOnlyList<EconomyRewardOffer>? offers = null) =>
        offers is null ? Tiers.Any(tier => tier.Id == id) : offers.Any(offer => offer.Id == id);

    // A claimed tier stays "Claimed" even if an admin later disables its offer; status markers are emulator-defined.
    public static string TierStatus(bool enabled, bool claimed, long progress, long target) =>
        claimed ? "claimed" : !enabled ? "unavailable" : progress < target ? "in-progress" : "ready";

    public static string ExpeditionStatus(LocalEquipmentState state, DateTimeOffset now) =>
        state.ExpeditionClaimed ? "claimed"
            : state.ExpeditionReadyAt is null ? state.Credits >= ExpeditionCost ? "available" : "insufficient-credits"
            : state.ExpeditionReadyAt > now ? "running"
            : state.Inventory.FirstOrDefault(item => item.Id == ExpeditionRewardItemId) is not { Quantity: < int.MaxValue }
                ? "reward-unavailable" : "ready";

    // Menu-211: battle pass status tabs. Values match the per-tier data-status markers; labels are emulator-defined.
    public static readonly IReadOnlyList<(string Id, string Label)> Filters =
    [
        ("all", "All"), ("ready", "Ready"), ("in-progress", "In progress"), ("claimed", "Claimed"), ("unavailable", "Unavailable")
    ];

    public static bool IsFilter(string value) => Filters.Any(filter => filter.Id == value);

    private sealed record TierView(string Id, string Name, long Target, long Progress, string Reward, bool Enabled, string Status);

    private static TierView[] Views(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers) =>
        (offers is null
            ? Tiers.Select(tier => (tier.Id, tier.Name, Target: (long)tier.Target, Progress: (long)tier.Progress(state),
                Reward: $"{tier.RewardCredits} local credits", Enabled: true))
            : offers.Select(offer => (offer.Id, offer.Name, offer.Target,
                Progress: LocalHangarEquipmentStore.OfferProgress(offer, state),
                Reward: $"{offer.Amount} {offer.AssetId}", offer.Enabled)))
        .Select(tier => new TierView(tier.Id, tier.Name, tier.Target, Math.Min(tier.Target, tier.Progress), tier.Reward, tier.Enabled,
            TierStatus(tier.Enabled, state.ClaimedBattlePassTiers.Contains(tier.Id), Math.Min(tier.Target, tier.Progress), tier.Target)))
        .ToArray();

    private static bool InFilter(TierView tier, string? filter) => filter is null or "all" || tier.Status == filter;

    public static bool IsSearch(string value) => value.Length is >= 2 and <= 40
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_');

    private static bool Matches(TierView tier, string? search) => search is null
        || tier.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || tier.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || tier.Reward.Contains(search, StringComparison.OrdinalIgnoreCase);

    // True when the tier exists and its current status matches the filter (no filter or "all" matches every tier).
    public static bool TierInFilter(LocalEquipmentState state, string id, string? filter,
        IReadOnlyList<EconomyRewardOffer>? offers = null, string? search = null) =>
        Views(state, offers).Any(tier => tier.Id == id && InFilter(tier, filter) && Matches(tier, search));

    public static string Page(LocalEquipmentState state, DateTimeOffset now,
        IReadOnlyList<EconomyRewardOffer>? offers = null, string? detail = null,
        string? claimedTier = null, string? expeditionNotice = null, string? filter = null, string? search = null)
    {
        const string eventsUrl = "/indexInternal.es?action=internalEvents";
        if (filter == "all") filter = null;
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var filterLabel = Filters.FirstOrDefault(item => item.Id == filter).Label;
        var filterUrl = filter is null ? eventsUrl : $"{eventsUrl}&amp;filter={filter}";
        var searchQuery = search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}";
        var baseUrl = filterUrl + searchQuery;
        var allViews = Views(state, offers);
        var matching = allViews.Where(tier => Matches(tier, search)).ToArray();
        var views = matching.Where(tier => InFilter(tier, filter)).ToArray();
        string ClaimForm(string id) =>
            $"<form method=\"post\" action=\"/indexInternal.es?action=internalBattlePassClaim\"><input type=\"hidden\" name=\"tier\" value=\"{HtmlEncoder.Default.Encode(id)}\"><button type=\"submit\">Claim</button></form>";
        var detailIndex = detail is null ? -1 : Array.FindIndex(views, tier => tier.Id == detail);
        var detailHtml = "";
        if (detailIndex >= 0)
        {
            var tier = views[detailIndex];
            var code = tier.Status;
            var status = code switch
            {
                "claimed" => "Claimed",
                "unavailable" => "Unavailable",
                "in-progress" => InProgress(tier.Target - tier.Progress),
                _ => "Ready to claim"
            };
            var claim = code == "ready" ? ClaimForm(tier.Id) : "";
            var previous = detailIndex > 0 ? $"<a href=\"{baseUrl}&amp;detail={HtmlEncoder.Default.Encode(views[detailIndex - 1].Id)}\">Previous</a> · " : "";
            var next = detailIndex + 1 < views.Length ? $" · <a href=\"{baseUrl}&amp;detail={HtmlEncoder.Default.Encode(views[detailIndex + 1].Id)}\">Next</a>" : "";
            var position = filter is null ? $"Tier {detailIndex + 1} of {views.Length}" : $"{filterLabel} tier {detailIndex + 1} of {views.Length}";
            detailHtml = $"<section id=\"battlePassDetail\" data-tier=\"{HtmlEncoder.Default.Encode(tier.Id)}\" data-status=\"{code}\"><h3>{HtmlEncoder.Default.Encode(tier.Name)}</h3><p>{position} · {HtmlEncoder.Default.Encode(status)}</p><p>Progress: {tier.Progress}/{tier.Target} · Remaining: {Math.Max(0, tier.Target - tier.Progress)}</p><p>Reward: {HtmlEncoder.Default.Encode(tier.Reward)}</p>{claim}<nav>{previous}<a href=\"{baseUrl}\">Close</a>{next}</nav></section>";
        }
        var tiers = views.Length == 0
            ? $"<tr id=\"battlePassEmpty\"><td colspan=\"4\">{(search is null ? $"No {filterLabel?.ToLowerInvariant()} tiers right now." : $"No {(filter is null ? "" : filter.Replace('-', ' ') + " ")}tiers match this search.")}</td></tr>"
            : string.Join("", views.Select(tier =>
            {
                var action = tier.Status switch
                {
                    "claimed" => "Claimed",
                    "unavailable" => "Unavailable",
                    "in-progress" => InProgress(tier.Target - tier.Progress),
                    _ => ClaimForm(tier.Id)
                };
                return $"<tr data-tier=\"{HtmlEncoder.Default.Encode(tier.Id)}\" data-status=\"{tier.Status}\"><td><a href=\"{baseUrl}&amp;detail={HtmlEncoder.Default.Encode(tier.Id)}\">{HtmlEncoder.Default.Encode(tier.Name)}</a></td><td>{tier.Progress}/{tier.Target}</td><td>{HtmlEncoder.Default.Encode(tier.Reward)}</td><td>{action}</td></tr>";
            }));
        // Tab counts cover every tier (including admin-disabled ones) so each tab predicts its list length.
        var tabs = "<nav id=\"battlePassFilters\">" + string.Join(" · ", Filters.Select(item =>
        {
            var count = item.Id == "all" ? matching.Length : matching.Count(tier => tier.Status == item.Id);
            var href = (item.Id == "all" ? eventsUrl : $"{eventsUrl}&amp;filter={item.Id}") + searchQuery;
            var current = (filter ?? "all") == item.Id ? " aria-current=\"page\"" : "";
            return $"<a href=\"{href}\" data-filter=\"{item.Id}\"{current}>{item.Label} ({count})</a>";
        })) + "</nav>";
        var showing = filter is null ? "" : $"<p id=\"battlePassShowing\">Showing {views.Length} of {matching.Length} tiers</p>";
        var searchForm = $"<form id=\"battlePassSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalEvents\">" +
            (filter is null ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
            $"<label>Find tier <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>";
        var searchSummary = search is null ? "" : $"<p id=\"battlePassSearchSummary\">{views.Length} of {allViews.Count(tier => InFilter(tier, filter))} tiers match. <a href=\"{filterUrl}\">Clear search</a></p>";
        var expeditionStatus = ExpeditionStatus(state, now);
        var expeditionBody = expeditionStatus switch
        {
            "claimed" => "Reward claimed",
            "available" => "<form method=\"post\" action=\"/indexInternal.es?action=internalExpeditionStart\"><input type=\"hidden\" name=\"start\" value=\"1\"><button type=\"submit\">Launch expedition</button></form>",
            "insufficient-credits" => $"You need {ExpeditionCost - state.Credits} more local credits to launch an expedition.",
            "ready" => "<form method=\"post\" action=\"/indexInternal.es?action=internalExpeditionClaim\"><input type=\"hidden\" name=\"claim\" value=\"1\"><button type=\"submit\">Claim shield</button></form>",
            "reward-unavailable" => "The shield reward cannot be added to your Hangar inventory. Claim is unavailable.",
            _ => $"Ready at {state.ExpeditionReadyAt:yyyy-MM-dd HH:mm} UTC ({MinutesLeft(state.ExpeditionReadyAt!.Value, now)} min left)"
        };
        var expedition = $"<div id=\"expedition\" data-status=\"{expeditionStatus}\">{expeditionBody}</div>";
        // Enabled tiers plus any tier already claimed before an admin disabled it.
        var counted = allViews.Where(tier => tier.Enabled || state.ClaimedBattlePassTiers.Contains(tier.Id)).ToArray();
        var tierTotal = counted.Length;
        var tierClaimed = counted.Count(tier => state.ClaimedBattlePassTiers.Contains(tier.Id));
        // Built from the saved event state only, so a link alone cannot fake a claim or launch.
        var notice = "";
        var claimedIndex = claimedTier is null ? -1 : Array.FindIndex(allViews, tier => tier.Id == claimedTier);
        if (claimedIndex >= 0 && state.ClaimedBattlePassTiers.Contains(allViews[claimedIndex].Id))
        {
            var claimed = allViews[claimedIndex];
            var nextReady = Array.FindIndex(allViews, tier => tier.Status == "ready");
            var next = tierClaimed == tierTotal ? " Every tier has been claimed."
                : nextReady >= 0 ? $" Next ready: {HtmlEncoder.Default.Encode(allViews[nextReady].Name)}." : "";
            notice = $"<p id=\"eventsNotice\" role=\"status\" data-tier=\"{HtmlEncoder.Default.Encode(claimed.Id)}\">Claimed {HtmlEncoder.Default.Encode(claimed.Name)}: +{HtmlEncoder.Default.Encode(claimed.Reward)}. " +
                $"Balance: {state.Credits} local credits. Tiers claimed: {tierClaimed} of {tierTotal}.{next}</p>";
        }
        else if (expeditionNotice == "started" && expeditionStatus is "running" or "ready" or "reward-unavailable")
        {
            var when = expeditionStatus == "ready" ? "It is ready to claim now."
                : expeditionStatus == "reward-unavailable" ? "It is ready, but the shield reward cannot be added to your Hangar inventory."
                : $"Ready at {state.ExpeditionReadyAt:yyyy-MM-dd HH:mm} UTC ({MinutesLeft(state.ExpeditionReadyAt!.Value, now)} min left).";
            notice = $"<p id=\"eventsNotice\" role=\"status\" data-expedition=\"started\">Expedition launched for {ExpeditionCost} local credits. {when} Balance: {state.Credits} local credits.</p>";
        }
        else if (expeditionNotice == "claimed" && state.ExpeditionClaimed)
        {
            var reward = state.Inventory.FirstOrDefault(item => item.Id == ExpeditionRewardItemId);
            notice = $"<p id=\"eventsNotice\" role=\"status\" data-expedition=\"claimed\">Expedition reward claimed: 1 {HtmlEncoder.Default.Encode(reward?.Name ?? "SG3N-B02 shield")} (owned: {reward?.Quantity ?? 0}). Balance: {state.Credits} local credits.</p>";
        }
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Events - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="events"><h1>Events</h1><p>Local practice events · Balance: {{state.Credits}} local credits</p>{{notice}}<h2>Battle pass</h2><p id="battle-pass-summary">{{tierClaimed}} of {{tierTotal}} tiers claimed</p>{{tabs}}{{searchForm}}{{searchSummary}}{{showing}}<table><thead><tr><th>Goal</th><th>Progress</th><th>Reward</th><th>Status</th></tr></thead><tbody>{{tiers}}</tbody></table>{{detailHtml}}<h2>Expedition</h2><p>One expedition costs {{ExpeditionCost}} local credits, takes {{ExpeditionMinutes}} minutes, and rewards one SG3N-B02 shield.</p>{{expedition}}<p><a href="/indexInternal.es?action=internalShop">Shop</a> · <a href="/indexInternal.es?action=internalAssembly">Assembly</a> · <a href="/indexInternal.es?action=internalDock">Hangar</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to menu</a></p></main></body></html>
            """;
    }
}
