using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only codes. No official voucher URL or response was captured.
public sealed record LocalVoucher(string Code, int Credits);

public static class LocalVouchers
{
    public static readonly IReadOnlyList<LocalVoucher> Catalog =
    [
        new("TRAINING-500", 500),
        new("HANGAR-750", 750)
    ];

    // Emulator-defined limits; no official voucher format or grant cap was captured.
    public const int MaxCodeLength = 32;
    public const long MaxGrant = 1_000_000;

    public static bool IsWellFormed(string? code) =>
        code is { Length: >= 1 and <= MaxCodeLength } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Emulator-defined list filters for the Vouchers page; no official voucher list was captured.
    public static readonly IReadOnlyList<string> Filters = ["all", "available", "redeemed", "unavailable"];

    public static bool IsKnownFilter(string? filter) => filter is not null && Filters.Contains(filter, StringComparer.Ordinal);

    private static bool MatchesFilter(string filter, string status) =>
        filter == "all" || filter == status || filter == "unavailable" && status == "balance-limit";

    public static bool IsWellFormedSearch(string? search) =>
        search is { Length: >= 1 and <= 32 }
        && search == search.Trim()
        && search.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-');

    private static bool MatchesSearch(string id, string name, string? search) =>
        search is null || id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || name.Contains(search, StringComparison.OrdinalIgnoreCase);

    public static string Status(LocalEquipmentState state, string id, long amount, bool enabled, string assetId = "credits") =>
        state.RedeemedVoucherCodes.Contains(id) ? "redeemed"
            : enabled && amount is > 0 and <= MaxGrant
                ? assetId == "credits" && amount > int.MaxValue - (long)state.Credits ? "balance-limit" : "available"
                : "unavailable";

    private static List<(string Id, string Name, long Amount, string AssetId, string Status)> Rows(
        LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers)
    {
        var catalog = offers is null
            ? Catalog.Select(voucher => (Id: voucher.Code, Name: voucher.Code, Amount: (long)voucher.Credits, AssetId: "credits", Enabled: true))
            : offers.Select(offer => (offer.Id, offer.Name, offer.Amount, offer.AssetId, offer.Enabled));
        return catalog.Select(voucher => (voucher.Id, voucher.Name, voucher.Amount, voucher.AssetId,
            Status: Status(state, voucher.Id, voucher.Amount, voucher.Enabled, voucher.AssetId))).ToList();
    }

    private static string ListHref(string filter, string? search = null) =>
        (filter == "all" ? "/indexInternal.es?action=internalVouchers" : $"/indexInternal.es?action=internalVouchers&amp;filter={filter}")
        + (search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}");

    private static string DetailHref(string id, string filter, string? search = null) => $"{ListHref(filter, search)}&amp;voucher={Uri.EscapeDataString(id)}";

    // Emulator-defined detail view (`voucher={id}`, exact case); null when the code is unknown or outside the filter.
    public static string? DetailPage(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers, string filter, string voucherId, string? search = null)
    {
        if (!IsKnownFilter(filter)) filter = "all";
        var shown = Rows(state, offers).Where(voucher => MatchesFilter(filter, voucher.Status)
            && MatchesSearch(voucher.Id, voucher.Name, search)).ToList();
        var index = shown.FindIndex(voucher => string.Equals(voucher.Id, voucherId, StringComparison.Ordinal));
        if (index < 0) return null;
        var item = shown[index];
        var encode = HtmlEncoder.Default;
        var id = encode.Encode(item.Id);
        var statusText = item.Status switch
        {
            "redeemed" => "Redeemed on this account. Each code works once per account.",
            "balance-limit" => "Your local credit balance is too high for this reward. Spend credits and try again.",
            "unavailable" => "Unavailable. This offer is disabled or its reward is outside the local grant limit.",
            _ => "Available. Redeeming adds the reward to this account once."
        };
        var name = item.Name != item.Id ? $"<p id=\"voucherName\">Offer: {encode.Encode(item.Name)}</p>" : "";
        var balance = item.AssetId == "credits" ? $"<p id=\"voucherBalance\">Balance: {state.Credits:N0} local credits</p>" : "";
        var redeem = item.Status == "available"
            ? $"<form id=\"voucherDetailRedeem\" method=\"post\" action=\"/indexInternal.es?action=internalVoucherRedeem\"><input type=\"hidden\" name=\"code\" value=\"{id}\"><button type=\"submit\">Redeem {id}</button></form>"
            : "";
        var previous = index > 0 ? $"<a id=\"voucherPrevious\" href=\"{DetailHref(shown[index - 1].Id, filter, search)}\">Previous</a> · " : "";
        var next = index < shown.Count - 1 ? $"<a id=\"voucherNext\" href=\"{DetailHref(shown[index + 1].Id, filter, search)}\">Next</a> · " : "";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Voucher {{id}} - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}button{padding:8px;background:#1789b5;color:white;border:0}</style></head><body><main id="voucherDetail" data-voucher="{{id}}" data-status="{{item.Status}}" data-filter="{{filter}}"><h1>Voucher {{id}}</h1>{{name}}<p id="voucherReward">Reward: {{item.Amount:N0}} {{encode.Encode(item.AssetId)}}</p><p id="voucherStatus">{{statusText}}</p>{{balance}}<p id="voucherPosition">Code {{index + 1}} of {{shown.Count}} in {{filter}}.</p>{{redeem}}<p>{{previous}}{{next}}<a id="voucherClose" href="{{ListHref(filter, search)}}">Close</a> · <a href="/indexInternal.es?action=internalLogbook&amp;category=account">Logbook</a></p></main></body></html>
            """;
    }

    // Menu-114: notice for the `redeemed={id}` redirect target (exact case). Null when the code is unknown.
    // A code that is not redeemed on this account (for example a link from another account) gets a neutral notice.
    public static string? RedeemedNotice(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers, string redeemedId)
    {
        var row = Rows(state, offers).FirstOrDefault(voucher => string.Equals(voucher.Id, redeemedId, StringComparison.Ordinal));
        if (row.Id is null) return null;
        var encode = HtmlEncoder.Default;
        var id = encode.Encode(row.Id);
        if (row.Status != "redeemed")
            return $"<p id=\"voucherRedeemedNotice\" role=\"status\" data-voucher=\"{id}\" data-redeemed=\"false\">Voucher {id} is not redeemed on this account.</p>";
        var balance = row.AssetId == "credits" ? $" Balance: {state.Credits:N0} local credits." : "";
        return $"<p id=\"voucherRedeemedNotice\" role=\"status\" data-voucher=\"{id}\" data-redeemed=\"true\">Voucher {id} redeemed: +{row.Amount:N0} {encode.Encode(row.AssetId)}.{balance}</p>";
    }

    public static string Page(LocalEquipmentState state, IReadOnlyList<EconomyRewardOffer>? offers = null, string filter = "all",
        string notice = "", string? search = null)
    {
        if (!IsKnownFilter(filter)) filter = "all";
        var list = Rows(state, offers);
        var redeemed = list.Count(voucher => voucher.Status == "redeemed");
        var encode = HtmlEncoder.Default;
        static string Totals(IEnumerable<(long Amount, string AssetId)> rows) =>
            rows.GroupBy(row => row.AssetId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Sum(row => row.Amount):N0} {HtmlEncoder.Default.Encode(group.Key)}")
                .DefaultIfEmpty("nothing").Aggregate((left, right) => $"{left}, {right}");
        var granted = Totals(list.Where(voucher => voucher.Status == "redeemed").Select(voucher => (voucher.Amount, voucher.AssetId)));
        var open = Totals(list.Where(voucher => voucher.Status == "available").Select(voucher => (voucher.Amount, voucher.AssetId)));
        var latestId = state.RedeemedVoucherCodes.LastOrDefault(id => list.Any(voucher => voucher.Id == id));
        var latest = "";
        if (latestId is not null)
        {
            var last = list.First(voucher => voucher.Id == latestId);
            latest = $"<p id=\"voucherLatest\">Last redeemed: {encode.Encode(last.Id)} (+{last.Amount:N0} {encode.Encode(last.AssetId)}).</p>";
        }
        var tabs = string.Join(" · ", Filters.Select(name =>
        {
            var count = list.Count(voucher => MatchesFilter(name, voucher.Status));
            var label = $"{char.ToUpperInvariant(name[0])}{name[1..]} ({count})";
            var href = ListHref(name, search);
            return name == filter
                ? $"<a href=\"{href}\" data-filter=\"{name}\" aria-current=\"page\"><strong>{label}</strong></a>"
                : $"<a href=\"{href}\" data-filter=\"{name}\">{label}</a>";
        }));
        var shown = list.Where(voucher => MatchesFilter(filter, voucher.Status)
            && MatchesSearch(voucher.Id, voucher.Name, search)).ToList();
        var codes = string.Join("", shown.Select(voucher =>
            $"<li data-voucher=\"{encode.Encode(voucher.Id)}\" data-status=\"{voucher.Status}\">{encode.Encode(voucher.Id)}: {voucher.Amount:N0} {encode.Encode(voucher.AssetId)}" +
            (voucher.Name != voucher.Id ? $" ({encode.Encode(voucher.Name)})" : "") +
            (voucher.Status switch
            {
                "redeemed" => " (redeemed)",
                "balance-limit" => " (credit balance limit; spend credits to redeem)",
                "unavailable" => " (unavailable)",
                _ => $" <form method=\"post\" action=\"/indexInternal.es?action=internalVoucherRedeem\" style=\"display:inline\"><input type=\"hidden\" name=\"code\" value=\"{encode.Encode(voucher.Id)}\"><button type=\"submit\">Redeem {encode.Encode(voucher.Id)}</button></form>"
            }) + $" <a href=\"{DetailHref(voucher.Id, filter, search)}\" data-detail=\"{encode.Encode(voucher.Id)}\">Details</a></li>"));
        var empty = shown.Count == 0 ? $"<p id=\"voucherEmpty\">No {(filter == "all" ? "" : filter + " ")}voucher codes{(search is null ? "" : " match this search")}.</p>" : "";
        var searchForm = $"<form id=\"voucherSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalVouchers\"><input type=\"hidden\" name=\"filter\" value=\"{filter}\"><label for=\"voucherQuery\">Search codes and offer names</label> <input id=\"voucherQuery\" name=\"q\" maxlength=\"32\" pattern=\"[A-Za-z0-9 -]+\" required value=\"{encode.Encode(search ?? "")}\"><button type=\"submit\">Search</button></form>" + (search is null ? "" : $"<a id=\"voucherClearSearch\" href=\"{ListHref(filter)}\">Clear search</a>");
        var title = filter == "all" ? "Vouchers - DarkOrbit" : $"Vouchers ({filter}) - DarkOrbit";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>{{title}}</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}input,button{padding:8px}button{background:#1789b5;color:white;border:0}</style></head><body><main id="vouchers" data-filter="{{filter}}"><h1>Vouchers</h1>{{notice}}<p>Local emulator practice codes. No real voucher or payment is used.</p><p>Balance: {{state.Credits:N0}} local credits</p><p id="voucherProgress">Redeemed {{redeemed}} of {{list.Count}} codes. Each code works once per account.</p><p id="voucherTotals">Granted: {{granted}}. Still available: {{open}}.</p>{{latest}}<nav id="voucherFilters">{{tabs}}</nav>{{searchForm}}<ul>{{codes}}</ul>{{empty}}<form method="post" action="/indexInternal.es?action=internalVoucherRedeem"><label for="code">Code</label> <input id="code" name="code" maxlength="{{MaxCodeLength}}" pattern="[A-Za-z0-9-]+" required autocomplete="off"> <button type="submit">Redeem</button></form><p><a href="/indexInternal.es?action=internalShop">Shop</a> · <a href="/indexInternal.es?action=internalLogbook&amp;category=account">Logbook</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
