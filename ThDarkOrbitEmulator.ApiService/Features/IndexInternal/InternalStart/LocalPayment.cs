using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local practice grants. No price, checkout, payment provider, or real currency.
public sealed record LocalPaymentPackage(string Id, string Name, int Credits);

public static class LocalPayment
{
    public const int MaxIdLength = 32;
    // Emulator-defined upper bound for a single practice grant.
    public const long MaxGrant = 1_000_000;

    // Names and amounts are emulator-defined; no Live payment page was captured.
    public static readonly IReadOnlyList<LocalPaymentPackage> Catalog =
    [
        new("starter", "Starter supply", 1000),
        new("hangar", "Hangar supply", 3000),
        new("fleet", "Fleet supply", 5000),
        new("squadron", "Squadron supply", 10000),
        new("armada", "Armada supply", 25000)
    ];

    public static bool IsWellFormed(string? id) =>
        id is { Length: >= 1 and <= MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // One row of the rendered catalog: the built-in list or the Postgres `payment` reward offers.
    public sealed record PackageView(string Id, string Name, long Amount, string AssetId, bool Enabled);

    public static IReadOnlyList<PackageView> Packages(IReadOnlyList<EconomyRewardOffer>? offers) =>
        (offers is null
            ? Catalog.Select(package => new PackageView(package.Id, package.Name, package.Credits, "credits", true))
            : offers.Select(offer => new PackageView(offer.Id, offer.Name, offer.Amount, offer.AssetId, offer.Enabled))).ToList();

    // Ids are matched exactly, like the claim action.
    public static PackageView? Find(IReadOnlyList<PackageView> catalog, string? id) =>
        id is null ? null : catalog.FirstOrDefault(package => string.Equals(package.Id, id, StringComparison.Ordinal));

    public static string AssetLabel(string assetId) => assetId switch
    {
        "credits" => "Credits",
        "uridium" => "Uridium",
        _ => assetId
    };

    // available, claimed or unavailable; a claimed package stays claimed even if the offer is later disabled.
    public static string Status(LocalEquipmentState state, PackageView package) =>
        state.ClaimedPaymentPackages.Contains(package.Id) ? "claimed" : package.Enabled ? "available" : "unavailable";

    // Menu-146: emulator-defined status filters; no official payment list or filter was captured.
    public static readonly IReadOnlyList<string> Filters = ["all", "available", "claimed", "unavailable"];
    public const int MaxFilterLength = 16;

    // Shape check only (1-16 lowercase ASCII letters); whether the filter exists is IsKnownFilter.
    public static bool IsWellFormedFilter(string? filter) =>
        filter is { Length: >= 1 and <= MaxFilterLength } && filter.All(char.IsAsciiLetterLower);

    public static bool IsKnownFilter(string? filter) => filter is not null && Filters.Contains(filter, StringComparer.Ordinal);

    public static bool IsWellFormedSearch(string? search) =>
        search is { Length: >= 2 and <= 40 } && search[0] != ' ' && search[^1] != ' '
        && search.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_');

    public static IReadOnlyList<PackageView> Visible(LocalEquipmentState state, IReadOnlyList<PackageView> catalog, string filter,
        string? search = null) => catalog.Where(package =>
            (filter == "all" || Status(state, package) == filter)
            && (search is null || package.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || package.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || package.AssetId.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();

    // True when the package exists and is listed under the filter, so detail links never cross filters.
    public static bool HasDetail(LocalEquipmentState state, IReadOnlyList<PackageView> catalog, string filter, string id,
        string? search = null) => IsKnownFilter(filter) && Find(Visible(state, catalog, filter, search), id) is not null;

    public static string Page(LocalEquipmentState state, bool proxy = false,
        IReadOnlyList<EconomyRewardOffer>? offers = null, string? detail = null, string? claimed = null, string filter = "all",
        string? search = null)
    {
        if (!IsKnownFilter(filter)) filter = "all";
        if (!IsWellFormedSearch(search)) search = null;
        var catalog = Packages(offers);
        var shown = Visible(state, catalog, filter, search);
        var balance = Math.Max(0, state.Credits);
        var cards = string.Join("", shown.Select(package =>
        {
            var status = Status(state, package);
            return $"<article id=\"package-{Encode(package.Id)}\" data-status=\"{status}\" data-asset=\"{Encode(package.AssetId)}\">" +
                $"<h2><a href=\"{DetailHref(package.Id, filter, search)}\">{Encode(package.Name)}</a></h2>" +
                $"<p>{package.Amount:N0} {Encode(package.AssetId)}</p>" +
                (status == "unavailable" ? "<p>Unavailable</p>" : status == "claimed" ? "<p>Claimed</p>" : ClaimForm(package.Id)) + "</article>";
        }));
        var claimedCount = catalog.Count(package => state.ClaimedPaymentPackages.Contains(package.Id));
        var claimedCredits = catalog.Where(package => package.AssetId == "credits" && state.ClaimedPaymentPackages.Contains(package.Id))
            .Sum(package => package.Amount);
        var available = catalog.Where(package => Status(state, package) == "available").ToList();
        var remaining = available.Count == 0
            ? "<p id=\"paymentRemaining\">No packages left to claim.</p>"
            : "<p id=\"paymentRemaining\">Still available: " + string.Join(", ", available.GroupBy(package => package.AssetId)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Sum(package => package.Amount):N0} {Encode(AssetLabel(group.Key))}")) +
                $" in {available.Count} package{(available.Count == 1 ? "" : "s")}.</p>";
        var panel = Find(shown, detail) is { } selected ? DetailPanel(state, shown, selected, balance, filter, search) : "";
        var tabs = string.Join(" · ", Filters.Select(name =>
        {
            var label = $"{char.ToUpperInvariant(name[0])}{name[1..]} ({Visible(state, catalog, name, search).Count})";
            return name == filter
                ? $"<a href=\"{ListHref(name, search)}\" data-filter=\"{name}\" aria-current=\"page\"><strong>{label}</strong></a>"
                : $"<a href=\"{ListHref(name, search)}\" data-filter=\"{name}\">{label}</a>";
        }));
        var empty = shown.Count == 0 ? search is null
            ? $"<p id=\"paymentEmpty\">No {filter} packages.</p>"
            : "<p id=\"paymentEmpty\">No packages match this search.</p>" : "";
        var searchUi = $"<form method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalPayment\"><input type=\"hidden\" name=\"filter\" value=\"{filter}\"><label>Search packages <input name=\"q\" required minlength=\"2\" maxlength=\"40\" value=\"{Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>" +
            (search is null ? "" : $"<p id=\"paymentResults\">{shown.Count} package{(shown.Count == 1 ? "" : "s")} match this search. <a id=\"paymentClearSearch\" href=\"{ListHref(filter)}\">Clear search</a></p>");
        var title = filter == "all" ? "Payment - DarkOrbit" : $"Payment ({filter}) - DarkOrbit";
        var notice = ClaimedNotice(state, catalog, claimed, balance);
        var pageId = proxy ? "paymentProxy" : "payment";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>{{title}}</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}article,section{border:1px solid #4c7896;padding:16px;margin:16px 0}section{border-color:#8cdcff}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="{{pageId}}" data-filter="{{filter}}"><h1>Payment</h1><p>Local emulator practice credits. No purchase or payment is made.</p><p>Balance: {{balance:N0}} local credits</p><p id="paymentSummary">Claimed {{claimedCount}} of {{catalog.Count}} packages ({{claimedCredits:N0}} credits). Each package can be claimed once per account.</p>{{notice}}{{remaining}}{{searchUi}}<nav id="paymentFilters">{{tabs}}</nav>{{panel}}{{cards}}{{empty}}<p><a href="/indexInternal.es?action=internalShop">Shop</a> · <a href="/indexInternal.es?action=internalVouchers">Vouchers</a> · <a href="/indexInternal.es?action=internalLogbook&amp;category=economy">Logbook</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }

    // Shown after the claim redirect. The grant text comes from the catalog, never the link, and only for a package
    // this account has actually claimed, so an edited link cannot fake a grant.
    public static string ClaimedNotice(LocalEquipmentState state, IReadOnlyList<PackageView> catalog, string? claimed, long balance)
    {
        if (Find(catalog, claimed) is not { } package || !state.ClaimedPaymentPackages.Contains(package.Id)) return "";
        var balanceText = package.AssetId == "credits" ? $" Balance: {balance:N0} local credits." : "";
        return $"<p id=\"paymentClaimed\" role=\"status\" data-package=\"{Encode(package.Id)}\">Package claimed: {Encode(package.Name)} · " +
            $"{package.Amount:N0} {Encode(AssetLabel(package.AssetId))} added to your account.{balanceText}</p>";
    }

    // catalog is the filtered list, so the position and Previous/Next stay inside the selected filter.
    private static string DetailPanel(LocalEquipmentState state, IReadOnlyList<PackageView> catalog, PackageView package, long balance,
        string filter, string? search)
    {
        var status = Status(state, package);
        var index = catalog.ToList().IndexOf(package);
        var html = new System.Text.StringBuilder();
        html.Append($"<section id=\"packageDetail\" data-package=\"{Encode(package.Id)}\" data-status=\"{status}\">");
        html.Append($"<h2>{Encode(package.Name)}</h2><dl>");
        html.Append($"<dt>Package</dt><dd>{index + 1} of {catalog.Count}</dd>");
        html.Append($"<dt>Grant</dt><dd>{package.Amount:N0} {Encode(AssetLabel(package.AssetId))}</dd>");
        html.Append($"<dt>Status</dt><dd>{status switch { "claimed" => "Claimed", "unavailable" => "Unavailable", _ => "Available" }}</dd>");
        if (package.AssetId == "credits")
        {
            html.Append($"<dt>Credits now</dt><dd>{balance:N0}</dd>");
            if (status == "available")
                html.Append($"<dt>Credits after claim</dt><dd>{balance + package.Amount:N0}</dd>");
        }
        html.Append("</dl><p>Practice grant only. No price, checkout, or real currency.</p>");
        if (status == "available") html.Append(ClaimForm(package.Id));
        html.Append("<p>");
        if (index > 0) html.Append($"<a rel=\"prev\" href=\"{DetailHref(catalog[index - 1].Id, filter, search)}\">Previous</a> · ");
        if (index < catalog.Count - 1) html.Append($"<a rel=\"next\" href=\"{DetailHref(catalog[index + 1].Id, filter, search)}\">Next</a> · ");
        html.Append($"<a href=\"{ListHref(filter, search)}\">Close</a></p></section>");
        return html.ToString();
    }

    private static string ClaimForm(string id) =>
        "<form method=\"post\" action=\"/indexInternal.es?action=internalPaymentClaim\">" +
        $"<input type=\"hidden\" name=\"package\" value=\"{Encode(id)}\">" +
        "<button type=\"submit\">Claim local credits</button></form>";

    private static string ListHref(string filter, string? search = null) =>
        (filter == "all" ? "/indexInternal.es?action=internalPayment" : $"/indexInternal.es?action=internalPayment&amp;filter={filter}")
        + (search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}");

    private static string DetailHref(string id, string filter = "all", string? search = null) =>
        $"{ListHref(filter, search)}&amp;detail={Uri.EscapeDataString(id)}";

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}
