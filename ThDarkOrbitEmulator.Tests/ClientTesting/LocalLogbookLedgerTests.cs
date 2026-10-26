using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private const int Offset = LocalLogbook.GeneratedEntries + 2;

    private static GameAuthSession NewSession(long gameUserId = 7) => new()
    {
        Username = "pilot", GameUserId = gameUserId, Company = "mmo", Dosid = "d",
        CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1)
    };

    private static EconomyLedgerRow Row(long id, long delta, string reason = "shop_purchase", string source = "shop",
        string asset = "credits", long after = 1000) =>
        new(id * 10, id, asset, delta, after, reason, source, Now.AddMinutes(-(id % 10_000)));

    private static readonly EconomyLedgerRow[] Ledger =
    [
        Row(3, 50_000, "legacy_import", "hangar", after: 50_000),
        Row(9, -2_500, after: 47_500),
        Row(12, 300, "quest_claim", "quests", "uridium", 300),
        Row(15, 1_000, "<b>gift</b>", "admin", after: 48_500)
    ];

    [Test]
    public void LedgerRows_AppearAsNewestEntriesWithStableIds()
    {
        var entries = LocalLogbook.Entries(NewSession(), Ledger);
        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(Offset + Ledger.Length));
            Assert.That(entries.Take(4).Select(e => e.Id), Is.EqualTo(new[] { $"7-{Offset + 15}", $"7-{Offset + 12}", $"7-{Offset + 9}", $"7-{Offset + 3}" }));
            Assert.That(entries.Select(e => e.Number), Is.Ordered.Descending.And.Unique);
            Assert.That(entries.All(e => e.Amount >= 0), Is.True);
            Assert.That(entries.Count(e => e.Ledger is not null), Is.EqualTo(Ledger.Length));
            // Demo entries keep their numbers when ledger rows are added.
            Assert.That(entries.Where(e => e.Ledger is null).Select(e => e.Id),
                Is.EquivalentTo(LocalLogbook.Entries(NewSession()).Select(e => e.Id)));
        });
    }

    [Test]
    public void LedgerRows_MapToCategoriesAndReadableText()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalLogbook.LedgerCategory(Ledger[0]), Is.EqualTo("account"));
            Assert.That(LocalLogbook.LedgerCategory(Ledger[1]), Is.EqualTo("economy"));
            Assert.That(LocalLogbook.LedgerCategory(Ledger[2]), Is.EqualTo("missions"));
            Assert.That(LocalLogbook.LedgerCategory(Ledger[3]), Is.EqualTo("account"));
            Assert.That(LocalLogbook.LedgerText(Ledger[1]), Is.EqualTo("Shop purchase (shop): -2,500 credits, balance 47,500"));
            Assert.That(LocalLogbook.LedgerText(Ledger[2]), Is.EqualTo("Quest claim (quests): +300 uridium, balance 300"));
            Assert.That(LocalLogbook.LedgerText(Row(1, 1, new string('x', 200))), Has.Length.LessThan(120));
        });
    }

    [Test]
    public void NpcRewards_AppearInCombatLogbook()
    {
        var kill = Row(16, 400, "npc_destroyed", "streuner");
        Assert.That(LocalLogbook.LedgerCategory(kill), Is.EqualTo("combat"));
        Assert.That(LocalLogbook.LedgerText(kill), Does.Contain("Destroyed streuner"));
        var result = Resolve("action=internalLogbook&category=combat", NewSession(), [kill]);
        Assert.That(result.Html, Does.Contain("Destroyed streuner"));
    }

    [Test]
    public void ListPage_ShowsPurchaseAndEncodesReason()
    {
        var result = Resolve("action=internalLogbook", NewSession(), Ledger);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Shop purchase (shop): -2,500 credits, balance 47,500"));
            Assert.That(result.Html, Does.Contain($"entry=7-{Offset + 9}"));
            Assert.That(result.Html, Does.Not.Contain("<b>gift</b>"));
            Assert.That(result.Html, Does.Contain("local economy ledger rows"));
        });
    }

    [Test]
    public void CategoryFilter_IncludesLedgerRowsOfThatCategoryOnly()
    {
        var html = Resolve("action=internalLogbook&category=missions", NewSession(), Ledger).Html;
        Assert.That(html, Does.Contain("Quest claim (quests)"));
        Assert.That(html, Does.Not.Contain("Shop purchase (shop)"));
    }

    [Test]
    public void DetailPage_ShowsLedgerFields()
    {
        var result = Resolve($"action=internalLogbook&entry=7-{Offset + 9}", NewSession(), Ledger);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain("Local ledger row 90"));
            Assert.That(result.Html, Does.Contain("<dt>Change</dt><dd>-2500</dd>"));
            Assert.That(result.Html, Does.Contain("<dt>Balance after</dt><dd>47500</dd>"));
            Assert.That(result.Html, Does.Contain("<dt>Menu</dt><dd>shop</dd>"));
        });
        Assert.That(Resolve("action=internalLogbook&entry=7-1", NewSession(), Ledger).Html, Does.Contain("Demo entry"));
    }

    [Test]
    public void DetailPage_UnknownLedgerNumberIs404AndOverflowIs400()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Resolve($"action=internalLogbook&entry=7-{Offset + 10}", NewSession(), Ledger).Status, Is.EqualTo(404));
            Assert.That(Resolve("action=internalLogbook&entry=7-9999", NewSession(), Ledger).Status, Is.EqualTo(404));
            Assert.That(Resolve("action=internalLogbook&entry=7-10000", NewSession(), Ledger).Status, Is.EqualTo(404));
            Assert.That(Resolve("action=internalLogbook&entry=7-9223372036854775808", NewSession(), Ledger).Status, Is.EqualTo(400));
            Assert.That(Resolve($"action=internalLogbook&entry=8-{Offset + 9}", NewSession(), Ledger).Status, Is.EqualTo(403));
        });
    }

    [Test]
    public void InvalidLedgerRows_AreSkippedAndDuplicatesCollapse()
    {
        EconomyLedgerRow[] rows = [Row(0, 5), Row(-4, 5), Row(5, 0), Row(long.MaxValue, 5), Row(6, 5), Row(6, 5)];
        var entries = LocalLogbook.Entries(NewSession(), rows);
        Assert.That(entries.Count(e => e.Ledger is not null), Is.EqualTo(1));
        Assert.That(entries.Select(e => e.Number), Is.Unique);
    }

    [Test]
    public void MatureAccount_KeepsNewestLedgerEntriesBeyondFourDigitIds()
    {
        EconomyLedgerRow[] rows = [Row(9_968, -25), Row(100_000, 500, "quest_claim", "quests"), Row(long.MaxValue - Offset, -1)];
        var entries = LocalLogbook.Entries(NewSession(), rows);
        Assert.That(entries.Count(e => e.Ledger is not null), Is.EqualTo(rows.Length));
        foreach (var row in rows)
        {
            var id = $"7-{Offset + row.Sequence}";
            var result = Resolve($"action=internalLogbook&entry={id}", NewSession(), rows);
            Assert.That(result.Status, Is.EqualTo(200), id);
            Assert.That(result.Html, Does.Contain($"Logbook entry {id}"));
        }
        Assert.That(Resolve("action=internalLogbook", NewSession(), rows).Html,
            Does.Contain($"entry=7-{Offset + rows[2].Sequence}"));
    }

    [Test]
    public void ExpiredSession_StillReturns401BeforeLedger()
    {
        var session = new GameAuthSession { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d", ExpiresAt = Now.AddMinutes(-1) };
        Assert.That(Resolve("action=internalLogbook", session, Ledger).Status, Is.EqualTo(401));
    }

    [Test]
    public void ManyLedgerRows_PaginateWithinBounds()
    {
        var rows = Enumerable.Range(1, LocalLogbook.LedgerLimit).Select(i => Row(i, -i)).ToArray();
        var total = Offset + rows.Length;
        var pages = (total + LocalLogbook.PageSize - 1) / LocalLogbook.PageSize;
        Assert.Multiple(() =>
        {
            Assert.That(Resolve($"action=internalLogbook&page={pages}", NewSession(), rows).Status, Is.EqualTo(200));
            Assert.That(Resolve($"action=internalLogbook&page={pages + 1}", NewSession(), rows).Status, Is.EqualTo(404));
        });
    }

    [Test]
    public void LedgerDetail_PreservesFilteredPageAndNavigatesAcrossPageBoundary()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(i, -i)).ToArray();
        var session = NewSession();
        var filtered = LocalLogbook.Entries(session, rows).Where(e => e.Category == "economy").ToArray();
        var boundary = filtered[LocalLogbook.PageSize - 1];
        var following = filtered[LocalLogbook.PageSize];
        var list = Resolve("action=internalLogbook&category=economy&page=1", session, rows);
        var detail = Resolve($"action=internalLogbook&category=economy&page=1&entry={boundary.Id}", session, rows);
        Assert.Multiple(() =>
        {
            Assert.That(list.Html, Does.Contain($"category=economy&amp;page=1&amp;entry={boundary.Id}"));
            Assert.That(detail.Status, Is.EqualTo(200));
            Assert.That(detail.Html, Does.Contain("category=economy&amp;page=1\">Back to logbook"));
            Assert.That(detail.Html, Does.Contain($"category=economy&amp;page=2&amp;entry={following.Id}\">Next entry"));
            Assert.That(detail.Html, Does.Not.Contain("category=all&amp;page="));
        });
    }

    [Test]
    public void Detail_RejectsEntryOutsideSelectedCategoryOrPage()
    {
        var session = NewSession();
        var rows = Enumerable.Range(1, 20).Select(i => Row(i, -i)).ToArray();
        var filtered = LocalLogbook.Entries(session, rows).Where(e => e.Category == "economy").ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Resolve($"action=internalLogbook&category=missions&entry={filtered[0].Id}", session, rows).Status, Is.EqualTo(404));
            Assert.That(Resolve($"action=internalLogbook&category=economy&page=2&entry={filtered[0].Id}", session, rows).Status, Is.EqualTo(404));
            Assert.That(Resolve($"action=internalLogbook&entry={filtered[8].Id}", session, rows).Html,
                Does.Contain("category=all&amp;page=2\">Back to logbook"));
        });
    }

    private static LocalLogbook.Result Resolve(string query, GameAuthSession session, IReadOnlyList<EconomyLedgerRow> ledger)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalLogbook.Resolve(context.Request.Query, session, Now, ledger);
    }
}
