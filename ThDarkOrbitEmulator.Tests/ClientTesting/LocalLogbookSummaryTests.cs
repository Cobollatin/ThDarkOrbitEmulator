using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-108: category tab counts and the per-asset local ledger summary on the Logbook list page.
public class LocalLogbookSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static GameAuthSession NewSession(long gameUserId = 7) => new()
    {
        Username = "pilot", GameUserId = gameUserId, Company = "mmo", Dosid = "d",
        CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1)
    };

    private static GameAuthSession NoCompany()
    {
        var session = NewSession();
        session.Company = null;
        return session;
    }

    private static EconomyLedgerRow Row(long seq, long delta, long after, string asset = "credits",
        string reason = "shop_purchase", string source = "shop") =>
        new(seq * 10, seq, asset, delta, after, reason, source, Now.AddMinutes(-100 + seq));

    private static readonly EconomyLedgerRow[] Ledger =
    [
        Row(1, 50_000, 50_000, reason: "legacy_import", source: "hangar"),
        Row(2, -2_500, 47_500),
        Row(3, 300, 300, "uridium", "quest_claim", "quests"),
        Row(4, -1_000, 46_500),
        Row(5, -100, 200, "uridium")
    ];

    private static LocalLogbook.Result Resolve(string query, GameAuthSession session, IReadOnlyList<EconomyLedgerRow> ledger)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalLogbook.Resolve(context.Request.Query, session, Now, ledger);
    }

    [Test]
    public void Summary_TotalsNetChangeAndLatestBalancePerAsset()
    {
        var summary = LocalLogbook.Summary(LocalLogbook.Entries(NewSession(), Ledger));
        Assert.That(summary, Is.EqualTo(new[]
        {
            ("credits", 3, 46_500m, 46_500L),
            ("uridium", 2, 200m, 200L)
        }));
    }

    [Test]
    public void Summary_IgnoresDemoEntriesAndZeroDeltaRows()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalLogbook.Summary(LocalLogbook.Entries(NewSession())), Is.Empty);
            Assert.That(LocalLogbook.Summary(LocalLogbook.Entries(NewSession(), [Row(1, 0, 10)])), Is.Empty);
        });
    }

    [Test]
    public void Summary_LatestBalanceFollowsNewestSequenceNotInputOrder()
    {
        var shuffled = new[] { Ledger[3], Ledger[0], Ledger[1] };
        var summary = LocalLogbook.Summary(LocalLogbook.Entries(NewSession(), shuffled));
        Assert.That(summary.Single(), Is.EqualTo(("credits", 3, 46_500m, 46_500L)));
    }

    [Test]
    public void Search_DoesNotPresentAnOlderMatchingBalanceAsLatest()
    {
        var html = Resolve("action=internalLogbook&q=Shop", NewSession(), Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("3 entries match &quot;Shop&quot;"));
            Assert.That(html, Does.Contain("<tr data-asset=\"credits\"><td>credits</td><td>3</td><td>+46,500</td><td>46,500</td></tr>"));
            Assert.That(html, Does.Contain("<tr data-asset=\"uridium\"><td>uridium</td><td>2</td><td>+200</td><td>200</td></tr>"));
        });
    }

    [Test]
    public void SearchWithoutMatches_StillShowsCurrentLedgerBalances()
    {
        var html = Resolve("action=internalLogbook&q=Unmatched", NewSession(), Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("0 entries match &quot;Unmatched&quot;"));
            Assert.That(html, Does.Contain("<tr data-asset=\"credits\"><td>credits</td><td>3</td><td>+46,500</td><td>46,500</td></tr>"));
            Assert.That(html, Does.Contain("<tr data-asset=\"uridium\"><td>uridium</td><td>2</td><td>+200</td><td>200</td></tr>"));
        });
    }

    [Test]
    public void AssetFilter_StillScopesSummaryWhenSearchMatchesAnotherAsset()
    {
        var html = Resolve("action=internalLogbook&asset=credits&q=uridium", NewSession(), Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("0 entries match &quot;uridium&quot;"));
            Assert.That(html, Does.Contain("<tr data-asset=\"credits\"><td>credits</td><td>3</td><td>+46,500</td><td>46,500</td></tr>"));
            Assert.That(html, Does.Not.Contain("<tr data-asset=\"uridium\""));
        });
    }

    [Test]
    public void ListPage_ShowsSummaryTable()
    {
        var html = Resolve("action=internalLogbook", NewSession(), Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"logbookSummary\""));
            Assert.That(html, Does.Contain("<tr data-asset=\"credits\"><td>credits</td><td>3</td><td>+46,500</td><td>46,500</td></tr>"));
            Assert.That(html, Does.Contain("<tr data-asset=\"uridium\"><td>uridium</td><td>2</td><td>+200</td><td>200</td></tr>"));
            Assert.That(html.IndexOf("data-asset=\"credits\"", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("data-asset=\"uridium\"", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ListPage_NegativeNetIsSigned()
    {
        var html = Resolve("action=internalLogbook", NewSession(), [Row(1, -2_500, 7_500)]).Html;
        Assert.That(html, Does.Contain("<td>1</td><td>-2,500</td><td>7,500</td>"));
    }

    [Test]
    public void ListPage_WithoutLedgerSaysNoActivity()
    {
        var html = Resolve("action=internalLogbook", NewSession(), []).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("No local economy activity recorded yet."));
            Assert.That(html, Does.Not.Contain("data-asset="));
        });
    }

    [Test]
    public void ListPage_SummaryEncodesAssetIds()
    {
        var html = Resolve("action=internalLogbook", NewSession(), [Row(1, 5, 5, "<b>x</b>")]).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<b>x</b>"));
            Assert.That(html, Does.Contain("&lt;b&gt;x&lt;/b&gt;"));
        });
    }

    [Test]
    public void CategoryTabs_ShowCountsThatMatchFilteredEntries()
    {
        var session = NewSession();
        var all = LocalLogbook.Entries(session, Ledger);
        var html = Resolve("action=internalLogbook", session, Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-count=\"{all.Count}\" aria-current=\"page\">All ({all.Count})</a>"));
            foreach (var category in LocalLogbook.Categories)
            {
                var count = all.Count(entry => entry.Category == category.Code);
                Assert.That(html, Does.Contain($"data-count=\"{count}\">{category.Title} ({count})</a>"), category.Code);
            }
            Assert.That(LocalLogbook.Categories.Sum(c => all.Count(entry => entry.Category == c.Code)), Is.EqualTo(all.Count));
        });
    }

    [Test]
    public void CategoryTabs_MarkSelectedCategoryAndKeepCountsOfAllEntries()
    {
        var session = NewSession();
        var all = LocalLogbook.Entries(session, Ledger);
        var missions = all.Count(entry => entry.Category == "missions");
        var html = Resolve("action=internalLogbook&category=missions", session, Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-count=\"{missions}\" aria-current=\"page\">Missions ({missions})</a>"));
            Assert.That(html, Does.Contain($"data-count=\"{all.Count}\">All ({all.Count})</a>"));
            // The summary always covers every category so it does not change with the filter.
            Assert.That(html, Does.Contain("data-asset=\"credits\""));
        });
    }

    [Test]
    public void DetailPage_HasNoSummaryOrTabs()
    {
        var html = Resolve("action=internalLogbook&entry=7-1", NewSession(), Ledger).Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("logbookSummary"));
            Assert.That(html, Does.Not.Contain("data-count="));
        });
    }

    [Test]
    public void ErrorPages_HaveNoSummary()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Resolve("action=internalLogbook&page=99", NewSession(), Ledger).Html, Does.Not.Contain("logbookSummary"));
            Assert.That(Resolve("action=internalLogbook", NoCompany(), Ledger).Html,
                Does.Not.Contain("logbookSummary"));
        });
    }
}
