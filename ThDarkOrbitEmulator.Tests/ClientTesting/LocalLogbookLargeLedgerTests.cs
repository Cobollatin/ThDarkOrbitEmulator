using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookLargeLedgerTests
{
    [Test]
    public void ExtremeLedgerChanges_RenderWithoutOverflowAndKeepExactNet()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var session = new GameAuthSession
        {
            Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d",
            CreatedAt = now.AddMinutes(-5), ExpiresAt = now.AddHours(1)
        };
        var ledger = new[]
        {
            new EconomyLedgerRow(1, 1, "credits", long.MinValue, 0, "shop_purchase", "shop", now),
            new EconomyLedgerRow(2, 2, "credits", long.MinValue, 0, "shop_purchase", "shop", now)
        };
        var entries = LocalLogbook.Entries(session, ledger);
        var summary = LocalLogbook.Summary(entries);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalLogbook&source=local");
        var page = LocalLogbook.Resolve(context.Request.Query, session, now, ledger);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Where(entry => entry.Ledger is not null).Select(entry => entry.Amount),
                Is.All.EqualTo(9_223_372_036_854_775_808m));
            Assert.That(summary.Single().Net, Is.EqualTo(-18_446_744_073_709_551_616m));
            Assert.That(page.Status, Is.EqualTo(200));
            Assert.That(page.Html, Does.Contain("-18,446,744,073,709,551,616"));
            Assert.That(page.Html, Does.Contain("-9,223,372,036,854,775,808 credits"));
        });
    }
}
