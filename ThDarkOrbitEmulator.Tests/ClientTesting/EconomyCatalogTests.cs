using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class EconomyCatalogTests
{
    [Test]
    public void Shop_RendersDatabaseListingsWalletAndStockStates()
    {
        var listings = new List<EconomyListing>
        {
            new("new_laser", "new_laser", "New laser", "laser", "rare",
                JsonSerializer.Deserialize<JsonElement>("{}"), "laser_icon",
                JsonSerializer.Deserialize<JsonElement>("{\"uridium\":25}"), 0, 2, true),
            new("disabled_ship", "disabled_ship", "Disabled ship", "ship", "common",
                JsonSerializer.Deserialize<JsonElement>("{}"), null,
                JsonSerializer.Deserialize<JsonElement>("{\"credits\":100}"), null, null, false)
        };
        var html = LocalShop.PageFromDatabase(new GameAuthSession { Username = "pilot" }, listings,
            [new EconomyBalance("uridium", "currency", 50)],
            new Dictionary<string, long> { ["new_laser"] = 3 }, null, null);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("New laser"));
            Assert.That(html, Does.Contain("25 uridium"));
            Assert.That(html, Does.Contain("Owned: 3"));
            Assert.That(html, Does.Contain("Sold out"));
            Assert.That(html, Does.Contain("Disabled"));
            Assert.That(html, Does.Contain("idempotencyKey"));
        });
        var equipment = LocalShop.PageFromDatabase(new GameAuthSession { Username = "pilot" }, listings,
            [], new Dictionary<string, long>(), "Equipment", null);
        Assert.That(equipment, Does.Contain("New laser"));
        Assert.That(equipment, Does.Not.Contain("Disabled ship"));
        var limited = LocalShop.PageFromDatabase(new GameAuthSession { Username = "pilot" }, listings,
            [new EconomyBalance("uridium", "currency", 50)], new Dictionary<string, long>(),
            null, "new_laser", new Dictionary<string, long> { ["new_laser"] = 2 });
        Assert.That(limited, Does.Contain("Limit 2 per account; purchased 2"));
        Assert.That(limited, Does.Contain("name=\"currency\""));
        Assert.That(limited, Does.Contain("value=\"uridium\""));
    }

    [Test]
    public void EconomyHeader_ShowsAllCurrencyAndResourceBalances()
    {
        var html = LocalEconomyHeader.Add("<html><body><main>Menu</main></body></html>",
            [new EconomyBalance("credits", "currency", 100), new EconomyBalance("uridium", "currency", 5),
                new EconomyBalance("prometium", "resource", 250)]);
        Assert.That(html, Does.Contain("credits: 100"));
        Assert.That(html, Does.Contain("uridium: 5"));
        Assert.That(html, Does.Contain("prometium: 250"));
    }

    [Test]
    public void AuctionPage_UsesPersistentLotAndBalance()
    {
        var html = LocalAuction.PageFromDatabase(new GameAuthSession { Username = "pilot" },
            [new EconomyAuctionLot("custom", "laser_lf3", "Catalog laser", 900, null, null, false, true)],
            [new EconomyBalance("credits", "currency", 1200)], DateTimeOffset.UtcNow);
        Assert.That(html, Does.Contain("Catalog laser"));
        Assert.That(html, Does.Contain("Credits:"));
        Assert.That(html, Does.Contain("value=\"custom\""));
    }
}
