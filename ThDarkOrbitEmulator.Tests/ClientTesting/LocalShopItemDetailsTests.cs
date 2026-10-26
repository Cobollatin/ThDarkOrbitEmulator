using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopItemDetailsTests
{
    [Test]
    public void DetailShowsSavedStatsStockAndAllowanceWithEncodedAdminValues()
    {
        var listing = Listing("laser_lf3", "<script>laser</script>", "{\"damage<bonus>\":\"<img onerror=alert(1)>\",\"power\":150,\"enabled\":true,\"nested\":{\"danger\":\"<script>\"},\"slots\":[1,2],\"optional\":null}",
            3, 5, true, 3600, 2);
        var page = Render([listing], "laser_lf3", "Equipment", "laser");

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"shopItemDetails\""));
            Assert.That(page, Does.Contain("id=\"shopItemFacts\""));
            Assert.That(page, Does.Contain("id=\"shopItemStats\""));
            Assert.That(page, Does.Contain("Stock</th><td>3"));
            Assert.That(page, Does.Contain("Your remaining allowance</th><td>3"));
            Assert.That(page, Does.Contain("Restock interval (seconds)</th><td>3600"));
            Assert.That(page, Does.Contain("Restock amount</th><td>2"));
            Assert.That(page, Does.Contain("damage&lt;bonus&gt;"));
            Assert.That(page, Does.Contain("&lt;img onerror=alert(1)&gt;"));
            Assert.That(page, Does.Contain("power</th><td>150"));
            Assert.That(page, Does.Not.Contain("<script>"));
            Assert.That(page, Does.Not.Contain("<img"));
            Assert.That(page, Does.Contain("nested</th><td>{&quot;danger&quot;:&quot;&lt;script&gt;&quot;}"));
            Assert.That(page, Does.Contain("slots</th><td>[1,2]"));
            Assert.That(page, Does.Contain("optional</th><td>null"));
            Assert.That(page, Does.Contain("action=internalShopBuy"));
            Assert.That(page, Does.Contain("id=\"shopBackToList\" href=\"/indexInternal.es?action=internalShop&amp;category=Equipment&amp;q=laser\""));
        });
    }

    [Test]
    public void DetailCapsStatsAtThirtyTwoFields()
    {
        var stats = "{" + string.Join(",", Enumerable.Range(1, 33).Select(n => $"\"stat{n}\":{n}")) + "}";
        var page = Render([Listing("laser_lf3", "Laser", stats, null, null, true)], "laser_lf3", null, null);

        Assert.That(page, Does.Contain("stat32</th><td>32"));
        Assert.That(page, Does.Not.Contain("stat33</th>"));
        Assert.That(page, Does.Contain("Showing 32 of 33 item stats."));
    }

    [Test]
    public void DetailsDistinguishUnlimitedAndEmptyStats_AndStayOffListCards()
    {
        var listing = Listing("drone_flax", "Flax", "{}", null, null, false);
        var detail = Render([listing], "drone_flax", null, null);
        var list = Render([listing], null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Stock</th><td>Unlimited"));
            Assert.That(detail, Does.Contain("Your remaining allowance</th><td>Unlimited"));
            Assert.That(detail, Does.Contain("Availability</th><td>Disabled"));
            Assert.That(detail, Does.Contain("No item stats recorded."));
            Assert.That(detail, Does.Not.Contain("Restock interval"));
            Assert.That(list, Does.Not.Contain("id=\"shopItemDetails\""));
            Assert.That(list, Does.Not.Contain("id=\"shopBackToList\""));
            Assert.That(list, Does.Contain("action=internalShop&amp;item=drone_flax"));
        });
    }

    private static EconomyListing Listing(string id, string name, string stats, long? stock, long? limit,
        bool enabled, int? restockInterval = null, long? restockAmount = null) =>
        new(id, id, name, "laser", "rare", JsonSerializer.Deserialize<JsonElement>(stats), null,
            JsonSerializer.Deserialize<JsonElement>("{\"credits\":100}"), stock, limit, enabled, restockInterval, restockAmount);

    private static string Render(IReadOnlyList<EconomyListing> listings, string? item, string? category, string? search) =>
        LocalShop.PageFromDatabase(new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" },
            listings, [new EconomyBalance("credits", "currency", 5000)], new Dictionary<string, long>(),
            category, item, new Dictionary<string, long> { ["laser_lf3"] = 2 }, search);
}
