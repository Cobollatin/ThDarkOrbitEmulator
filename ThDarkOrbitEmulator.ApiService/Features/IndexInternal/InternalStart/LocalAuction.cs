using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only auction. The official post-company route and bidding contract are unknown.
public sealed record LocalAuctionListing(string ItemId, int OpeningBid);

public enum LocalAuctionResult
{
    Applied,
    Rejected,
    UnknownLot,
    Unavailable,
    BidTooLow,
    InsufficientCredits,
    Ended,
    AlreadyClaimed,
    NoBid,
    StillRunning,
    InventoryFull
}

public static class LocalAuction
{
    // Lot ids are admin-defined slugs such as `laser_lf3`; anything else is never looked up.
    public static bool IsLotIdShape(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    // Bids are plain positive decimal credit amounts; nine digits keeps them far below wallet overflow.
    public static bool TryParseBid(string value, out long amount)
    {
        amount = 0;
        if (value.Length is < 1 or > 9 || value[0] == '0' || !value.All(char.IsAsciiDigit)) return false;
        amount = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string KeyField(GameAuthSession session, string operation, string lotId, DateTimeOffset now) =>
        $"<input type=\"hidden\" name=\"key\" value=\"{HtmlEncoder.Default.Encode(LocalAuctionAction.CreateFormKey(session.Dosid, operation, lotId, now))}\">";

    // Menu-144: emulator-defined status filters, in tab order. `all` is the default list.
    public static readonly IReadOnlyList<(string Id, string Label)> Filters =
    [
        ("all", "All"), ("open", "Open"), ("claimable", "Ready to claim"), ("claimed", "Claimed"),
        ("ended", "Ended without bid"), ("unavailable", "Unavailable")
    ];

    public static bool IsKnownFilter(string? filter) => filter is null || Filters.Any(entry => entry.Id == filter);

    // Filter values are short lowercase slugs; anything else is rejected before the session check.
    public static bool IsFilterShape(string value) =>
        value.Length is >= 1 and <= 16 && value.All(c => c is >= 'a' and <= 'z' or '-');

    public static bool IsSearchShape(string value)
    {
        var trimmed = value.Trim();
        return !value.Any(char.IsControl) &&
            (trimmed.Length == 0 || trimmed.Length is >= 2 and <= 40 &&
                trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_'));
    }

    // One status per lot: disabled wins, then claimed, then the timer.
    public static string StatusId(EconomyAuctionLot lot, DateTimeOffset now) =>
        !lot.Enabled ? "unavailable" : lot.Claimed ? "claimed"
            : Ended(lot, now) ? (lot.Bid ?? 0) > 0 ? "claimable" : "ended" : "open";

    private static bool InFilter(EconomyAuctionLot lot, string? filter, DateTimeOffset now) =>
        filter is null or "all" || StatusId(lot, now) == filter;

    // Detail ids match exactly (case-sensitive) and must be in the selected filter; a null id means the plain list.
    public static bool HasLot(IReadOnlyList<EconomyAuctionLot> catalog, string? lotId, string? filter = null,
        DateTimeOffset? now = null) =>
        lotId is null || catalog.Any(lot => lot.Id == lotId && InFilter(lot, filter, now ?? DateTimeOffset.UtcNow));

    private static long MinimumBid(EconomyAuctionLot lot) =>
        (lot.Bid ?? 0) == 0 ? lot.OpeningBid : lot.Bid!.Value + Math.Max(1, lot.Bid.Value / 10);

    private static bool Ended(EconomyAuctionLot lot, DateTimeOffset now) => lot.EndsAt is not null && now >= lot.EndsAt;

    private static string Status(EconomyAuctionLot lot, DateTimeOffset now) =>
        !lot.Enabled ? "Disabled" : lot.Claimed ? "Claimed"
            : Ended(lot, now) ? "Auction ended" : lot.EndsAt is null ? "Bidding open" : $"Ends {lot.EndsAt:u}";

    private static bool ClaimInventoryFull(EconomyAuctionLot lot, IReadOnlyDictionary<string, long> inventory) =>
        inventory.GetValueOrDefault(lot.ItemId) >= long.MaxValue;

    private static string Action(GameAuthSession session, EconomyAuctionLot lot, long credits, DateTimeOffset now,
        IReadOnlyDictionary<string, long> inventory) =>
        !lot.Enabled || lot.Claimed ? ""
            : Ended(lot, now) ? (lot.Bid ?? 0) <= 0 || ClaimInventoryFull(lot, inventory) ? "" : $"<form method=\"post\" action=\"/indexInternal.es?action=internalAuctionClaim\"><input type=\"hidden\" name=\"item\" value=\"{HtmlEncoder.Default.Encode(lot.Id)}\">{KeyField(session, "claim", lot.Id, now)}<button type=\"submit\">Claim item</button></form>"
            : MinimumBid(lot) - (lot.Bid ?? 0) > credits ? ""
            : $"<form method=\"post\" action=\"/indexInternal.es?action=internalAuctionBid\"><input type=\"hidden\" name=\"item\" value=\"{HtmlEncoder.Default.Encode(lot.Id)}\">{KeyField(session, "bid", lot.Id, now)}<input type=\"number\" name=\"amount\" min=\"{MinimumBid(lot)}\" value=\"{MinimumBid(lot)}\"><button type=\"submit\">Bid</button></form>";

    private static string ListHref(string? filter, string? search = null)
    {
        var href = filter is null or "all" ? "/indexInternal.es?action=internalAuction" : $"/indexInternal.es?action=internalAuction&amp;filter={filter}";
        return search is null ? href : href + "&amp;q=" + Uri.EscapeDataString(search);
    }

    private static string LotLink(EconomyAuctionLot lot, string text, string? filter = null) =>
        $"<a href=\"{ListHref(filter)}&amp;lot={Uri.EscapeDataString(lot.Id)}\">{text}</a>";

    // Explains the lot's state in one sentence; emulator-defined wording.
    private static string Explanation(EconomyAuctionLot lot, long credits, DateTimeOffset now,
        IReadOnlyDictionary<string, long> inventory)
    {
        if (!lot.Enabled) return "This lot is disabled by the operator; bids and claims are refused.";
        if (lot.Claimed) return "You already claimed this item; it is in your Hangar inventory.";
        if (Ended(lot, now))
            return (lot.Bid ?? 0) <= 0 ? "The timer has ended without a bid."
                : ClaimInventoryFull(lot, inventory) ? "Your Hangar inventory stack is full. Make room before claiming this item."
                : "The timer has ended. Claim the item to move it to your Hangar.";
        var minimum = MinimumBid(lot);
        var held = lot.Bid ?? 0;
        // A raise only needs the difference above the credits already held by your previous bid.
        var needed = minimum - held;
        return needed > credits
            ? $"You need {needed - credits:N0} more Credits to place the minimum bid."
            : lot.EndsAt is null ? "Your first bid opens the lot and starts its five-minute timer."
            : "Raise your bid by at least 10% before the timer ends.";
    }

    private static string DetailPanel(GameAuthSession session, IReadOnlyList<EconomyAuctionLot> catalog,
        long credits, string lotId, DateTimeOffset now, string? filter, IReadOnlyDictionary<string, long> inventory)
    {
        var index = catalog.Select((lot, position) => (lot, position)).Single(entry => entry.lot.Id == lotId).position;
        var lot = catalog[index];
        var bid = lot.Bid ?? 0;
        var minimum = MinimumBid(lot);
        var biddable = lot.Enabled && !lot.Claimed && !Ended(lot, now);
        var after = biddable ? $"<dt>Credits after minimum bid</dt><dd>{Math.Max(0, credits - (minimum - bid)):N0}</dd>" : "";
        var previous = index > 0 ? LotLink(catalog[index - 1], "Previous", filter) + " · " : "";
        var next = index < catalog.Count - 1 ? LotLink(catalog[index + 1], "Next", filter) + " · " : "";
        return $"<section id=\"auction-lot\"><h2>{HtmlEncoder.Default.Encode(lot.Name)}</h2><p>Lot {index + 1} of {catalog.Count}</p><dl><dt>Lot</dt><dd>{HtmlEncoder.Default.Encode(lot.Id)}</dd><dt>Item</dt><dd>{HtmlEncoder.Default.Encode(lot.ItemId)}</dd><dt>Opening bid</dt><dd>{lot.OpeningBid:N0}</dd><dt>Your bid</dt><dd>{bid:N0}</dd><dt>Minimum bid</dt><dd>{minimum:N0}</dd><dt>Status</dt><dd>{Status(lot, now)}</dd><dt>Credits</dt><dd>{credits:N0}</dd>{after}</dl><p class=\"explanation\">{Explanation(lot, credits, now, inventory)}</p>{Action(session, lot, credits, now, inventory)}<p>{previous}{next}<a href=\"{ListHref(filter)}\">Close</a></p></section>";
    }

    // Menu-124: confirms a bid or claim from the account's saved lot state; a link alone never produces text.
    private static string Notice(IReadOnlyList<EconomyAuctionLot> catalog, long credits, DateTimeOffset now,
        string? placed, string? claimed)
    {
        if (placed is not null && catalog.SingleOrDefault(lot => lot.Id == placed) is { Bid: > 0, Claimed: false } bidLot)
        {
            var timer = bidLot.EndsAt is { } ends
                ? Ended(bidLot, now) ? "The auction has ended; claim the item." : $"Ends {ends:u}."
                : "Bidding open.";
            return $"<p id=\"auctionPlaced\" role=\"status\" data-lot=\"{HtmlEncoder.Default.Encode(placed)}\">" +
                $"Bid placed on {HtmlEncoder.Default.Encode(bidLot.Name)}: {bidLot.Bid:N0} Credits held. {timer} " +
                $"Next minimum bid: {MinimumBid(bidLot):N0}. Credits left: {Math.Max(0, credits):N0}.</p>";
        }
        if (claimed is not null && catalog.SingleOrDefault(lot => lot.Id == claimed) is { Claimed: true } claimedLot)
            return $"<p id=\"auctionClaimed\" role=\"status\" data-lot=\"{HtmlEncoder.Default.Encode(claimed)}\">" +
                $"Claimed {HtmlEncoder.Default.Encode(claimedLot.Name)} for {claimedLot.Bid ?? 0:N0} Credits. " +
                $"1 {HtmlEncoder.Default.Encode(claimedLot.ItemId)} moved to your Hangar inventory.</p>";
        return "";
    }

    public static string PageFromDatabase(GameAuthSession session, IReadOnlyList<EconomyAuctionLot> catalog,
        IReadOnlyList<EconomyBalance> balances, DateTimeOffset now, string? lotId = null,
        string? placed = null, string? claimed = null, string? filter = null,
        IReadOnlyDictionary<string, long>? inventory = null, string? search = null)
    {
        inventory ??= new Dictionary<string, long>();
        filter = string.IsNullOrEmpty(filter) || filter == "all" ? null : filter;
        search = string.IsNullOrWhiteSpace(search) || lotId is not null ? null : search.Trim();
        var credits = balances.Single(balance => balance.AssetId == "credits").Amount;
        var notice = Notice(catalog, credits, now, placed, claimed);
        // Notices and the Credits line use every lot; the table, detail position and Previous/Next use the filter.
        var matching = catalog.Where(lot => search is null || lot.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            lot.ItemId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            lot.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = matching.Where(lot => InFilter(lot, filter, now)).ToList();
        var tabs = "<nav id=\"auction-filters\">" + string.Join(" · ", Filters.Select(entry =>
        {
            var count = matching.Count(lot => InFilter(lot, entry.Id, now));
            return (filter ?? "all") == entry.Id
                ? $"<strong data-filter=\"{entry.Id}\" aria-current=\"page\">{entry.Label} ({count})</strong>"
                : $"<a href=\"{ListHref(entry.Id, search)}\" data-filter=\"{entry.Id}\">{entry.Label} ({count})</a>";
        })) + "</nav>";
        var searchForm = $"<form id=\"auctionSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalAuction\">" +
            (filter is null ? "" : $"<input type=\"hidden\" name=\"filter\" value=\"{filter}\">") +
            $"<label>Find lot <input name=\"q\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "") }\"></label><button type=\"submit\">Search</button></form>";
        var searchSummary = search is null ? "" : $"<p id=\"auctionSearchSummary\">{shown.Count} of {catalog.Count(lot => InFilter(lot, filter, now))} lots match. <a href=\"{ListHref(filter)}\">Clear search</a></p>";
        var rows = shown.Count == 0
            ? $"<tr><td colspan=\"6\" id=\"auction-empty\">{(search is null ? "No lots match this filter." : "No lots match this search.")}</td></tr>"
            : string.Join("", shown.Select(lot =>
                $"<tr data-lot=\"{HtmlEncoder.Default.Encode(lot.Id)}\" data-status=\"{StatusId(lot, now)}\"><td>{HtmlEncoder.Default.Encode(lot.Name)}</td><td>{lot.Bid ?? 0:N0}</td><td>{MinimumBid(lot):N0}</td><td>{Status(lot, now)}</td><td>{Action(session, lot, credits, now, inventory)}</td><td>{LotLink(lot, "Details", filter)}</td></tr>"));
        var detail = lotId is null ? "" : DetailPanel(session, shown, credits, lotId, now, filter, inventory);
        return $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Auction - DarkOrbit</title></head><body><main id=\"auction\"><h1>Auction</h1><p>Pilot {HtmlEncoder.Default.Encode(session.Username)} · Credits: {credits:N0}</p>{notice}{tabs}{searchForm}{searchSummary}{detail}<table><thead><tr><th>Item</th><th>Bid</th><th>Minimum</th><th>Status</th><th>Action</th><th>Details</th></tr></thead><tbody>{rows}</tbody></table></main></body></html>";
    }

    public static readonly IReadOnlyList<LocalAuctionListing> Catalog =
    [
        new("laser_lf3", 700),
        new("shield_sg3n_b02", 450),
        new("drone_flax", 200)
    ];

    public static string Page(GameAuthSession session, LocalEquipmentState state, DateTimeOffset now)
    {
        var rows = string.Join("", Catalog.Select(listing =>
        {
            var item = LocalShop.Find(listing.ItemId)!;
            var lot = state.AuctionLots.SingleOrDefault(candidate => candidate.ItemId == listing.ItemId);
            var bid = lot?.Bid ?? 0;
            var minimum = bid == 0 ? listing.OpeningBid : bid + Math.Max(1, bid / 10);
            var ended = lot is not null && now >= lot.EndsAt;
            var status = lot is null ? "Bidding opens with your first bid"
                : lot.Claimed ? "Claimed"
                : ended ? "Auction ended" : $"Ends {lot.EndsAt:u}";
            var action = ended && bid > 0 && !lot!.Claimed
                ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalAuctionClaim\"><input type=\"hidden\" name=\"item\" value=\"{listing.ItemId}\">{KeyField(session, "claim", listing.ItemId, now)}<button type=\"submit\">Claim item</button></form>"
                : !ended && ! (lot?.Claimed ?? false)
                    ? $"<form method=\"post\" action=\"/indexInternal.es?action=internalAuctionBid\"><input type=\"hidden\" name=\"item\" value=\"{listing.ItemId}\">{KeyField(session, "bid", listing.ItemId, now)}<label>Bid <input type=\"number\" name=\"amount\" min=\"{minimum}\" value=\"{minimum}\"></label><button type=\"submit\">Place bid</button></form>"
                    : "";
            return $"<tr><td>{HtmlEncoder.Default.Encode(item.Name)}</td><td>{bid:N0}</td><td>{minimum:N0}</td><td>{status}</td><td>{action}</td></tr>";
        }));
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Auction - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:1000px;margin:7vh auto}a{color:#8cdcff}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="auction"><h1>Auction</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · Balance: {{state.Credits:N0}} local credits</p><p>Each lot is reserved for your local account. Bids hold credits, can be raised by at least 10%, and can be claimed five minutes after the first bid.</p><table><thead><tr><th>Item</th><th>Your bid</th><th>Minimum bid</th><th>Status</th><th>Action</th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalDock">Hangar</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
