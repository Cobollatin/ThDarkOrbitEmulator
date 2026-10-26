using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookSourceFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly EconomyLedgerRow[] Ledger =
    [
        new(101, 1, "credits", 500, 500, "quest_claim", "quests", Now.AddMinutes(-3)),
        new(102, 2, "credits", -200, 300, "shop_purchase", "shop", Now.AddMinutes(-2))
    ];

    private static GameAuthSession Session() => new()
    {
        Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d",
        CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1)
    };

    private static LocalLogbook.Result Get(string query, IReadOnlyList<EconomyLedgerRow>? ledger = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalLogbook" + query);
        return LocalLogbook.Resolve(context.Request.Query, Session(), Now, ledger ?? Ledger);
    }

    [Test]
    public void LocalSource_ShowsOnlyRealLedgerRowsAndNavigationPreservesFilter()
    {
        var result = Get("&source=local");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Shop purchase"));
            Assert.That(result.Html, Does.Contain("Quest claim"));
            Assert.That(result.Html, Does.Not.Contain("Logged in to the local emulator</a>"));
            Assert.That(result.Html, Does.Contain("data-source=\"local\" data-count=\"2\" aria-current=\"page\""));
            Assert.That(result.Html, Does.Contain("source=local&amp;entry=7-34"));
            Assert.That(result.Html, Does.Contain("category=missions&amp;page=1&amp;source=local"));
        });
    }

    [Test]
    public void DemoSource_HidesLedgerAndItsSummary()
    {
        var result = Get("&source=demo");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Not.Contain("Shop purchase"));
            Assert.That(result.Html, Does.Contain("Logged in to the local emulator"));
            Assert.That(result.Html, Does.Not.Contain("id=\"logbookSummary\""));
            Assert.That(result.Html, Does.Contain("data-source=\"demo\" data-count=\"32\" aria-current=\"page\""));
        });
    }

    [Test]
    public void SourceAndCategoryCombineAndEmptyLocalViewIsValid()
    {
        var missions = Get("&source=local&category=missions");
        var empty = Get("&source=local", []);
        Assert.Multiple(() =>
        {
            Assert.That(missions.Status, Is.EqualTo(200));
            Assert.That(missions.Html, Does.Contain("Quest claim"));
            Assert.That(missions.Html, Does.Not.Contain("Shop purchase"));
            Assert.That(missions.Html, Does.Contain("data-source=\"local\" data-count=\"1\" aria-current=\"page\""));
            Assert.That(missions.Html, Does.Contain("data-count=\"1\" aria-current=\"page\">Missions (1)"));
            Assert.That(empty.Status, Is.EqualTo(200));
            Assert.That(empty.Html, Does.Contain("No entries in this category yet."));
        });
    }

    [Test]
    public void DetailAndPreviousNextStayWithinSource()
    {
        var result = Get("&source=local&entry=7-34");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("source=local&amp;entry=7-33\">Next entry"));
            Assert.That(result.Html, Does.Not.Contain("Previous entry"));
            Assert.That(result.Html, Does.Contain("source=local\">Back to logbook"));
            Assert.That(Get("&source=demo&entry=7-34").Status, Is.EqualTo(404));
            Assert.That(Get("&source=local&entry=7-1").Status, Is.EqualTo(404));
        });
    }

    [TestCase("&source=", 400)]
    [TestCase("&source=local&source=demo", 400)]
    [TestCase("&Source=local", 400)]
    [TestCase("&source=LOCAL", 404)]
    [TestCase("&source=other", 404)]
    public void InvalidSource_IsRejected(string query, int expected)
    {
        var result = Get(query);
        Assert.That(result.Status, Is.EqualTo(expected));
    }
}
