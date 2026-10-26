using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopSearchTests
{
    [TestCase("LF", true)]
    [TestCase("lf-3", true)]
    [TestCase(" LF", false)]
    [TestCase("L", false)]
    [TestCase("", false)]
    [TestCase("a\n", false)]
    public void SearchQueryIsValidated(string search, bool valid)
    {
        Assert.That(LocalShopPurchase.ValidPageQuery(Query(("action", "internalShop"), ("q", search))), Is.EqualTo(valid));
    }

    [Test]
    public void SearchKeyAndValueMustBeValid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalShopPurchase.ValidPageQuery(Query(("action", "internalShop"), ("Q", "LF"))), Is.False);
            Assert.That(LocalShopPurchase.ValidPageQuery(new QueryCollection(new Dictionary<string, StringValues>
            {
                ["action"] = "internalShop", ["q"] = new StringValues(["LF", "PET"])
            })), Is.False);
        });
    }

    [Test]
    public void SearchFiltersCatalogAndPreservesContextInLinks()
    {
        var page = Render("LF", "Equipment");
        Assert.That(page, Does.Contain("LF-3 laser"));
        Assert.That(page, Does.Not.Contain("P.E.T. 10"));
        Assert.That(page, Does.Contain("category=Equipment&amp;q=LF"));
        Assert.That(page, Does.Contain("item=laser_lf3&amp;category=Equipment&amp;q=LF"));
        Assert.That(page, Does.Contain("value=\"LF\""));
    }

    [Test]
    public void EmptySearchResultIsExplicitAndDoesNotOfferPurchases()
    {
        var page = Render("not-found", null);
        Assert.That(page, Does.Contain("0 items match"));
        Assert.That(page, Does.Contain("id=\"shopEmpty\""));
        Assert.That(page, Does.Not.Contain("internalShopBuy"));
    }

    [Test]
    public void SearchTextIsEncodedAndNeverInjectedIntoMarkup()
    {
        var page = Render("<script>", null);
        Assert.That(page, Does.Not.Contain("<script>"));
        Assert.That(page, Does.Contain("&lt;script&gt;"));
    }

    [Test]
    public void CategoryCountsFollowSearchAndMarkSelectedTab()
    {
        var page = Render("LF", "Equipment");
        Assert.That(page, Does.Contain("data-count=\"1\" aria-current=\"page\">Equipment (1)</a>"));
        Assert.That(page, Does.Contain("data-count=\"0\">PET (0)</a>"));
        Assert.That(page, Does.Contain("data-count=\"1\">All items (1)</a>"));
        Assert.That(page, Does.Contain("category=Equipment&amp;q=LF"));
    }

    [Test]
    public void ClearSearchRetainsCategoryAndRestoresItsCounts()
    {
        var searched = Render("no-match", "Equipment");
        Assert.That(searched, Does.Contain("id=\"shopClearSearch\" href=\"/indexInternal.es?action=internalShop&amp;category=Equipment\""));
        Assert.That(searched, Does.Contain("data-count=\"0\" aria-current=\"page\">Equipment (0)</a>"));

        var cleared = Render(null, "Equipment");
        Assert.That(cleared, Does.Not.Contain("shopClearSearch"));
        Assert.That(cleared, Does.Contain("data-count=\"1\" aria-current=\"page\">Equipment (1)</a>"));
        Assert.That(cleared, Does.Contain("data-count=\"2\">All items (2)</a>"));
    }

    private static QueryCollection Query(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));

    private static string Render(string? search, string? category) => LocalShop.PageFromDatabase(
        new GameAuthSession { Username = "pilot", Dosid = "session-one", Company = "mmo" },
        [Listing("laser_lf3", "LF-3 laser", "laser"), Listing("pet_10", "P.E.T. 10", "pet")],
        [new EconomyBalance("credits", "currency", 5000)], new Dictionary<string, long>(),
        category, null, search: search);

    private static EconomyListing Listing(string id, string name, string type) =>
        new(id, id, name, type, "common", JsonSerializer.Deserialize<JsonElement>("{}"), null,
            JsonSerializer.Deserialize<JsonElement>("{\"credits\":100}"), null, null, true);
}
