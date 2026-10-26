using System.Text.Encodings.Web;
using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only catalogue. No post-company Shop request or price was captured.
public sealed record LocalShopItem(string Id, string Name, string Category, string Description, int Price);

public static class LocalShop
{
    public static string CategoryFor(EconomyListing item) => item.Type switch
    {
        "laser" or "shield" or "generator" => "Equipment",
        "drone" => "Drones",
        "pet" => "PET",
        "extra" => "Extras",
        _ => item.Type
    };

    public static string PageFromDatabase(GameAuthSession session, IReadOnlyList<EconomyListing> catalog,
        IReadOnlyList<EconomyBalance> balances, IReadOnlyDictionary<string, long> inventory,
        string? category, string? itemId, IReadOnlyDictionary<string, long>? purchases = null, string? search = null)
    {
        purchases ??= new Dictionary<string, long>();
        var visible = catalog.Where(item => (category is null || CategoryFor(item).Equals(category, StringComparison.OrdinalIgnoreCase))
            && (itemId is null || item.Id == itemId) && MatchesSearch(item, search)).ToList();
        var searchQuery = search is null ? "" : $"&amp;q={Uri.EscapeDataString(search)}";
        var matching = catalog.Where(item => MatchesSearch(item, search)).ToList();
        var allSelected = category is null ? " aria-current=\"page\"" : "";
        var nav = string.Join("", catalog.Select(CategoryFor).Distinct().Select(type =>
        {
            var count = matching.Count(item => CategoryFor(item).Equals(type, StringComparison.OrdinalIgnoreCase));
            var selected = type.Equals(category, StringComparison.OrdinalIgnoreCase) ? " aria-current=\"page\"" : "";
            return $"<a href=\"/indexInternal.es?action=internalShop&amp;category={Uri.EscapeDataString(type)}{searchQuery}\" data-count=\"{count}\"{selected}>{HtmlEncoder.Default.Encode(type)} ({count})</a>";
        }));
        var cards = string.Join("", visible.Select(item =>
        {
            var stock = item.Enabled ? item.StockAvailable is null ? "Available" : item.StockAvailable == 0 ? "Sold out" : $"{item.StockAvailable} available" : "Disabled";
            var owned = inventory.GetValueOrDefault(item.ItemId);
            var purchased = purchases.GetValueOrDefault(item.Id);
            var limit = item.PerAccountLimit is null ? "" : $" · Limit {item.PerAccountLimit} per account; purchased {purchased}";
            var detail = $"/indexInternal.es?action=internalShop&amp;item={Uri.EscapeDataString(item.Id)}" +
                (category is null ? "" : $"&amp;category={Uri.EscapeDataString(category)}") + searchQuery;
            var notice = item.StockAvailable is <= 5 and > 0 ? " · Low stock" : "";
            long? remaining = item.PerAccountLimit is null ? null : Math.Max(0, item.PerAccountLimit.Value - purchased);
            var details = itemId is null ? "" : ItemDetails(item, remaining);
            var forms = string.Join("", item.Prices.EnumerateObject().Select(price =>
            {
                if (price.Value.ValueKind != System.Text.Json.JsonValueKind.Number
                    || !price.Value.TryGetInt64(out var unitPrice) || unitPrice < 0) return "";
                var currency = HtmlEncoder.Default.Encode(price.Name);
                var balance = Math.Max(0, balances.FirstOrDefault(b => b.AssetId == price.Name)?.Amount ?? 0);
                var byWallet = unitPrice == 0 ? 100L : balance / unitPrice;
                var byStock = item.StockAvailable ?? 100L;
                var byLimit = item.PerAccountLimit is null ? 100L : Math.Max(0, item.PerAccountLimit.Value - purchased);
                var byInventoryCapacity = long.MaxValue - Math.Max(0, owned);
                var byPurchaseCapacity = long.MaxValue - Math.Max(0, purchased);
                var maximum = item.Enabled ? Math.Max(0, Math.Min(100L, Math.Min(byWallet,
                    Math.Min(byStock, Math.Min(byLimit, Math.Min(byInventoryCapacity, byPurchaseCapacity)))))) : 0;
                var availability = maximum == 0 ? "Unavailable with this balance, stock or account limit" : $"Up to {maximum} per order";
                return $"<form method=\"post\" action=\"/indexInternal.es?action=internalShopBuy\">" +
                    $"<input type=\"hidden\" name=\"item\" value=\"{HtmlEncoder.Default.Encode(item.Id)}\">" +
                    $"<input type=\"hidden\" name=\"currency\" value=\"{currency}\">" +
                    $"<input type=\"hidden\" name=\"idempotencyKey\" value=\"{LocalShopPurchase.CreateFormKey(session.Dosid, item.Id, currency: price.Name)}\">" +
                    $"<label>{unitPrice:N0} {currency} each · Balance: {balance:N0} · {availability} <input type=\"number\" name=\"quantity\" min=\"1\" max=\"{Math.Max(1, maximum)}\" value=\"1\"{(maximum == 0 ? " disabled" : "")}></label>" +
                    $"<button type=\"submit\"{(maximum == 0 ? " disabled" : "")}>Buy</button></form>";
            }));
            return $"<article><h2>{HtmlEncoder.Default.Encode(item.Name)}</h2><p>{HtmlEncoder.Default.Encode(item.Type)} · {HtmlEncoder.Default.Encode(item.Rarity)} · Owned: {owned} · {stock}{notice}{limit}</p>" +
                (itemId is null ? $"<a href=\"{detail}\">Details</a>" : details) + $"{forms}</article>";
        }));
        var wallet = string.Join(" · ", balances.Select(b => $"{b.Amount:N0} {HtmlEncoder.Default.Encode(b.AssetId)}"));
        var searchForm = $"<form method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalShop\">" +
            (category is null ? "" : $"<input type=\"hidden\" name=\"category\" value=\"{HtmlEncoder.Default.Encode(category)}\">") +
            $"<label>Search catalog <input name=\"q\" minlength=\"2\" maxlength=\"40\" value=\"{HtmlEncoder.Default.Encode(search ?? "")}\"></label><button type=\"submit\">Search</button></form>";
        var summary = search is null ? $"{visible.Count} items" : $"{visible.Count} items match &quot;{HtmlEncoder.Default.Encode(search)}&quot;";
        var clearSearch = search is null ? "" : $"<a id=\"shopClearSearch\" href=\"/indexInternal.es?action=internalShop" +
            (category is null ? "" : $"&amp;category={Uri.EscapeDataString(category)}") + "\">Clear search</a>";
        var empty = visible.Count == 0 ? "<p id=\"shopEmpty\">No matching items.</p>" : "";
        var back = itemId is null ? "" : $"<p><a id=\"shopBackToList\" href=\"/indexInternal.es?action=internalShop" +
            (category is null ? "" : $"&amp;category={Uri.EscapeDataString(category)}") + searchQuery + "\">Back to items</a></p>";
        return $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Shop - DarkOrbit</title></head><body><main id=\"shop\"><h1>Shop</h1><p>Pilot {HtmlEncoder.Default.Encode(session.Username)} · {wallet}</p><nav><a href=\"/indexInternal.es?action=internalShop{searchQuery}\" data-count=\"{matching.Count}\"{allSelected}>All items ({matching.Count})</a>{nav}</nav>{searchForm}{clearSearch}<p id=\"shopResultCount\">{summary}</p>{empty}{cards}{back}<p><a href=\"/indexInternal.es?action=internalDock\">Hangar</a></p></main></body></html>";
    }

    private static string ItemDetails(EconomyListing item, long? remaining)
    {
        var e = HtmlEncoder.Default;
        var facts = new List<(string Label, string Value)>
        {
            ("Listing ID", item.Id), ("Item ID", item.ItemId), ("Type", item.Type),
            ("Rarity", item.Rarity), ("Availability", item.Enabled ? "Enabled" : "Disabled"),
            ("Stock", item.StockAvailable?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unlimited"),
            ("Your remaining allowance", remaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unlimited")
        };
        if (item.RestockIntervalSeconds is > 0 && item.RestockAmount is > 0)
        {
            facts.Add(("Restock interval (seconds)", item.RestockIntervalSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            facts.Add(("Restock amount", item.RestockAmount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var rows = string.Join("", facts.Select(fact => $"<tr><th>{e.Encode(fact.Label)}</th><td>{e.Encode(fact.Value)}</td></tr>"));
        var statCount = item.Stats.ValueKind == JsonValueKind.Object ? item.Stats.EnumerateObject().Count() : 0;
        var stats = item.Stats.ValueKind == JsonValueKind.Object
            ? item.Stats.EnumerateObject().Take(32)
                .Select(property => (property.Name, property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? "" : property.Value.GetRawText())).ToArray()
            : [];
        var statRows = stats.Length == 0 ? "<tr><td colspan=\"2\">No item stats recorded.</td></tr>"
            : string.Join("", stats.Select(stat => $"<tr><th>{e.Encode(stat.Name)}</th><td>{e.Encode(stat.Item2)}</td></tr>"));
        var statLimitNotice = statCount > stats.Length ? $"<p>Showing {stats.Length} of {statCount} item stats.</p>" : "";
        return $"<section id=\"shopItemDetails\"><h3>Item details</h3><table id=\"shopItemFacts\"><tbody>{rows}</tbody></table>" +
            $"<h3>Item stats</h3><table id=\"shopItemStats\"><tbody>{statRows}</tbody></table>{statLimitNotice}</section>";
    }

    public static bool MatchesSearch(EconomyListing item, string? search) => search is null ||
        new[] { item.Id, item.Name, item.Type, item.Rarity, CategoryFor(item) }
            .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    public static readonly IReadOnlyList<LocalShopItem> Catalog =
    [
        new("laser_lf3", "LF-3 laser", "Equipment", "Laser equipment for a hangar configuration.", 1500),
        new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", "Shield equipment for a hangar configuration.", 1000),
        new("drone_flax", "Flax drone", "Drones", "A drone for a hangar configuration.", 500),
        new("pet_10", "P.E.T. 10", "PET", "A companion for a hangar configuration.", 3000),
        new("extra_cpu", "Slot CPU", "Extras", "A locally defined ship extra for a hangar configuration.", 750)
    ];

    public static LocalShopItem? Find(string? id) => Catalog.SingleOrDefault(item => item.Id == id);

    public static bool IsCategory(string? category) =>
        category is null || Catalog.Any(item => item.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    public static string Page(GameAuthSession session, LocalEquipmentState state, string? category, string? itemId)
    {
        var selected = Find(itemId);
        var nav = string.Join("", Catalog.Select(item => item.Category).Distinct().Select(name =>
            $"<a href=\"/indexInternal.es?action=internalShop&amp;category={Uri.EscapeDataString(name)}\">{HtmlEncoder.Default.Encode(name)}</a>"));
        var items = selected is null
            ? Catalog.Where(item => category is null || item.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            : [selected];
        var cards = string.Join("", items.Select(item =>
        {
            var owned = state.Inventory.Single(inventory => inventory.Id == item.Id).Quantity;
            return $"<article><h2>{HtmlEncoder.Default.Encode(item.Name)}</h2><p>{HtmlEncoder.Default.Encode(item.Description)}</p>" +
                $"<p>{HtmlEncoder.Default.Encode(item.Category)} · {item.Price:N0} local credits · Owned: {owned}</p>" +
                $"<a href=\"/indexInternal.es?action=internalShop&amp;item={item.Id}\">Details</a>" +
                $"<form method=\"post\" action=\"/indexInternal.es?action=internalShopBuy\">" +
                $"<input type=\"hidden\" name=\"item\" value=\"{item.Id}\">" +
                "<label>Quantity <input type=\"number\" name=\"quantity\" min=\"1\" max=\"100\" value=\"1\"></label>" +
                "<button type=\"submit\">Buy with local credits</button></form></article>";
        }));
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Shop - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff;margin-right:20px}article{border:1px solid #4c7896;padding:16px;margin:16px 0}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="shop"><h1>Shop</h1><p>Pilot {{HtmlEncoder.Default.Encode(session.Username)}} · Balance: {{state.Credits:N0}} local credits</p><nav><a href="/indexInternal.es?action=internalShop">All items</a>{{nav}}</nav>{{cards}}<p><a href="/indexInternal.es?action=internalDock">Hangar</a><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
