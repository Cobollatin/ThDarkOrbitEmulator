using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopPurchaseOptionsTests
{
    [Test]
    public void EachCurrencyGetsItsOwnAffordableQuantityAndValidOrderForm()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" };
        var listing = Listing("{\"credits\":1500,\"uridium\":10}", 9, 8);
        var page = LocalShop.PageFromDatabase(session, [listing],
            [new EconomyBalance("credits", "currency", 3500), new EconomyBalance("uridium", "currency", 50)],
            new Dictionary<string, long>(), null, null,
            new Dictionary<string, long> { [listing.Id] = 1 });

        Assert.That(page, Does.Contain("1,500 credits each · Balance: 3,500 · Up to 2 per order"));
        Assert.That(page, Does.Contain("10 uridium each · Balance: 50 · Up to 5 per order"));
        Assert.That(page, Does.Contain("name=\"currency\" value=\"credits\""));
        Assert.That(page, Does.Contain("name=\"currency\" value=\"uridium\""));
        Assert.That(page, Does.Contain("max=\"2\""));
        Assert.That(page, Does.Contain("max=\"5\""));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(page, "name=\"idempotencyKey\"").Count, Is.EqualTo(2));
    }

    [TestCase(0, 8, true)]
    [TestCase(9, 0, true)]
    [TestCase(9, 2, false)]
    public void StockAndAccountLimitControlPurchaseQuantity(long stock, long limit, bool disabled)
    {
        var page = Render(Listing("{\"credits\":100}", stock, limit), 10000, 1);
        Assert.That(page, Does.Contain(disabled ? "Unavailable with this balance, stock or account limit" : "Up to 1 per order"));
        Assert.That(page, disabled ? Does.Contain("<button type=\"submit\" disabled>") : Does.Not.Contain("<button type=\"submit\" disabled>"));
    }

    [Test]
    public void FreeAndUnaffordableCurrenciesKeepPurchaseOptionsSeparate()
    {
        var page = Render(Listing("{\"credits\":0,\"uridium\":10}", null, null), 0, 0);
        Assert.That(page, Does.Contain("0 credits each · Balance: 0 · Up to 100 per order"));
        Assert.That(page, Does.Contain("10 uridium each · Balance: 0 · Unavailable with this balance"));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(page, "<button type=\"submit\" disabled>").Count, Is.EqualTo(1));
    }

    [Test]
    public void FullInventoryOrPurchaseCounterDisablesBuyForm()
    {
        var listing = Listing("{\"credits\":100}", null, null);
        var session = new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" };
        foreach (var (owned, purchased) in new[] { (long.MaxValue, 0L), (0L, long.MaxValue) })
        {
            var page = LocalShop.PageFromDatabase(session, [listing],
                [new EconomyBalance("credits", "currency", 10000)],
                new Dictionary<string, long> { [listing.ItemId] = owned }, null, null,
                new Dictionary<string, long> { [listing.Id] = purchased });
            Assert.That(page, Does.Contain("<button type=\"submit\" disabled>"));
            Assert.That(page, Does.Contain("Unavailable with this balance, stock or account limit"));
        }
    }

    private static EconomyListing Listing(string prices, long? stock, long? limit) =>
        new("laser_lf3", "laser_lf3", "LF-3 laser", "laser", "rare", JsonSerializer.Deserialize<JsonElement>("{}"),
            null, JsonSerializer.Deserialize<JsonElement>(prices), stock, limit, true);

    private static string Render(EconomyListing listing, long credits, long purchased) =>
        LocalShop.PageFromDatabase(new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" },
            [listing], [new EconomyBalance("credits", "currency", credits)], new Dictionary<string, long>(), null, null,
            new Dictionary<string, long> { [listing.Id] = purchased });
}
