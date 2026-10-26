using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookAssetFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly EconomyLedgerRow[] Ledger =
    [
        new(101, 1, "credits", 500, 500, "legacy_import", "hangar", Now.AddMinutes(-3)),
        new(102, 2, "uridium", 200, 200, "quest_claim", "quests", Now.AddMinutes(-2)),
        new(103, 3, "credits", -50, 450, "shop_purchase", "shop", Now.AddMinutes(-1))
    ];

    private static LocalLogbook.Result Get(string query, IReadOnlyList<EconomyLedgerRow>? ledger = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalLogbook" + query);
        var session = new GameAuthSession
        {
            Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d",
            CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1)
        };
        return LocalLogbook.Resolve(context.Request.Query, session, Now, ledger ?? Ledger);
    }

    [Test]
    public void AssetFiltersRealRowsAndCarriesSelectionIntoDetailAndSearch()
    {
        var page = Get("&asset=credits");
        Assert.Multiple(() =>
        {
            Assert.That(page.Status, Is.EqualTo(200));
            Assert.That(page.Html, Does.Contain("Shop purchase"));
            Assert.That(page.Html, Does.Contain("Imported starting balance"));
            Assert.That(page.Html, Does.Not.Contain("Quest claim"));
            Assert.That(page.Html, Does.Contain("asset=credits&amp;entry=7-35"));
            Assert.That(page.Html, Does.Contain("name=\"asset\" value=\"credits\""));
            Assert.That(page.Html, Does.Contain("<tr data-asset=\"credits\""));
            Assert.That(page.Html, Does.Not.Contain("<tr data-asset=\"uridium\""));
        });
        var detail = Get("&asset=credits&entry=7-35");
        Assert.That(detail.Status, Is.EqualTo(200));
        Assert.That(detail.Html, Does.Contain("asset=credits\">Back to logbook"));
        Assert.That(Get("&asset=credits&entry=7-34").Status, Is.EqualTo(404));
        Assert.That(Get("&asset=credits&q=shop&source=local").Html, Does.Contain("Shop purchase"));
    }

    [Test]
    public void AssetTabsCanSwitchFromDemoAndSourceTabCanClearAsset()
    {
        var demo = Get("&source=demo").Html;
        var filtered = Get("&asset=credits").Html;
        Assert.Multiple(() =>
        {
            Assert.That(demo, Does.Contain("source=local&amp;asset=credits"));
            Assert.That(filtered, Does.Contain("source=demo\""));
            Assert.That(filtered, Does.Not.Contain("source=demo&amp;asset=credits"));
            Assert.That(Get("&source=demo").Status, Is.EqualTo(200));
        });
    }

    [TestCase("&asset=", 400)]
    [TestCase("&asset=credits&asset=uridium", 400)]
    [TestCase("&Asset=credits", 400)]
    [TestCase("&asset=credit%0Ascript", 400)]
    [TestCase("&asset=Credits", 404)]
    [TestCase("&asset=prometium", 404)]
    [TestCase("&asset=credits&source=demo", 400)]
    public void InvalidOrUnknownAssetIsRejected(string query, int status)
    {
        Assert.That(Get(query).Status, Is.EqualTo(status));
    }

    [Test]
    public void AssetFromAnotherAccountIsNotDiscoverable()
    {
        Assert.That(Get("&asset=uridium", [Ledger[0], Ledger[2]]).Status, Is.EqualTo(404));
    }

    [Test]
    public void FilteredPaginationKeepsAssetOnPageAndDetailLinks()
    {
        var rows = Enumerable.Range(1, 10).Select(n => new EconomyLedgerRow(
            100 + n, n, "credits", 1, n, "admin_grant", "admin", Now.AddMinutes(n))).ToArray();
        var page = Get("&asset=credits&page=2", rows);
        Assert.Multiple(() =>
        {
            Assert.That(page.Status, Is.EqualTo(200));
            Assert.That(page.Html, Does.Contain("page 2 of 2"));
            Assert.That(page.Html, Does.Contain("page=1&amp;asset=credits\">Previous"));
            Assert.That(page.Html, Does.Contain("page=2&amp;asset=credits&amp;entry=7-34"));
            Assert.That(Get("&asset=credits&page=1&entry=7-34", rows).Status, Is.EqualTo(404));
            Assert.That(Get("&asset=credits&page=2&entry=7-34", rows).Status, Is.EqualTo(200));
        });
    }

    [Test]
    public void AdminDefinedAssetNamesAreEncodedInTabsAndLinks()
    {
        var row = new EconomyLedgerRow(104, 4, "ore & parts", 5, 5, "admin_grant", "admin", Now);
        var html = Get("&asset=ore%20%26%20parts", [row]).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("ore &amp; parts</a>"));
            Assert.That(html, Does.Contain("asset=ore%20%26%20parts"));
            Assert.That(html, Does.Not.Contain("ore & parts</a>"));
        });
    }
}
