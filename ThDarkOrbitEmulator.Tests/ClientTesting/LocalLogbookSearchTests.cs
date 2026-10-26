using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly EconomyLedgerRow[] Ledger =
    [
        new(101, 1, "credits", 500, 500, "quest_claim", "quests", Now.AddMinutes(-3)),
        new(102, 2, "uridium", -200, 300, "shop_purchase", "shop", Now.AddMinutes(-2))
    ];

    private static LocalLogbook.Result Get(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalLogbook" + query);
        return LocalLogbook.Resolve(context.Request.Query, new GameAuthSession
        {
            Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d",
            CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1)
        }, Now, Ledger);
    }

    [Test]
    public void SearchMatchesLedgerDetailsAndPreservesFiltersAndNavigation()
    {
        var result = Get("&source=local&q=uridium");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Shop purchase"));
            Assert.That(result.Html, Does.Not.Contain("Quest claim"));
            Assert.That(result.Html, Does.Contain("id=\"logbookSearchCount\">1 entries match"));
            Assert.That(result.Html, Does.Contain("category=economy&amp;page=1&amp;source=local&amp;q=uridium"));
            Assert.That(result.Html, Does.Contain("q=uridium&amp;entry=7-34"));
            Assert.That(result.Html, Does.Contain("data-source=\"local\" data-count=\"1\""));
        });
        var detail = Get("&source=local&q=uridium&entry=7-34");
        Assert.That(detail.Status, Is.EqualTo(200));
        Assert.That(detail.Html, Does.Contain("q=uridium\">Back to logbook"));
        Assert.That(Get("&source=local&q=uridium&entry=7-33").Status, Is.EqualTo(404));
    }

    [Test]
    public void SearchCombinesWithCategoryAndDemoSource()
    {
        var mission = Get("&category=missions&source=local&q=QUEST");
        var empty = Get("&source=demo&q=uridium");
        Assert.Multiple(() =>
        {
            Assert.That(mission.Status, Is.EqualTo(200));
            Assert.That(mission.Html, Does.Contain("Quest claim"));
            Assert.That(mission.Html, Does.Not.Contain("Shop purchase"));
            Assert.That(empty.Status, Is.EqualTo(200));
            Assert.That(empty.Html, Does.Contain("0 entries match"));
            Assert.That(empty.Html, Does.Not.Contain("id=\"logbookSummary\""));
            Assert.That(Get("&q=shop_purchase").Html, Does.Contain("Shop purchase"));
            Assert.That(Get("&q=no_matches_here").Status, Is.EqualTo(200));
        });
    }

    [TestCase("&q=", 400)]
    [TestCase("&q=a", 400)]
    [TestCase("&q=%20shop", 400)]
    [TestCase("&q=shop%20", 400)]
    [TestCase("&q=shop%3Cscript", 400)]
    [TestCase("&q=shop&q=quest", 400)]
    [TestCase("&Q=shop", 400)]
    public void InvalidSearchIsRejected(string query, int status)
    {
        Assert.That(Get(query).Status, Is.EqualTo(status));
    }
}
