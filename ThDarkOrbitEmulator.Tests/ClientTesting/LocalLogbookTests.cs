using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogbookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static GameAuthSession NewSession(string? company = "mmo", long gameUserId = 7, DateTimeOffset? expiresAt = null) => new()
    {
        Username = "<pilot>", GameUserId = gameUserId, Company = company, Dosid = "d",
        CreatedAt = Now.AddMinutes(-5), ExpiresAt = expiresAt ?? Now.AddHours(1)
    };

    [Test]
    public void DefaultPage_ListsNewestEntriesEncodedWithMenuLinks()
    {
        var result = Resolve("action=internalLogbook");
        var pages = (LocalLogbook.GeneratedEntries + 2 + LocalLogbook.PageSize - 1) / LocalLogbook.PageSize;
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(200));
            Assert.That(result.Html, Does.Contain($"All · page 1 of {pages}"));
            Assert.That(result.Html, Does.Contain("History of &lt;pilot&gt;"));
            Assert.That(result.Html, Does.Not.Contain("<pilot>"));
            Assert.That(result.Html, Does.Contain("Logged in to the local emulator"));
            Assert.That(result.Html, Does.Contain("Joined company "));
            Assert.That(result.Html, Does.Contain("action=internalWebGL&amp;from=internalStart"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalLogbook"));
        });
    }

    [Test]
    public void Entries_AreDeterministicOrderedAndNonNegative()
    {
        var session = NewSession();
        var entries = LocalLogbook.Entries(session);
        Assert.That(entries, Has.Count.EqualTo(LocalLogbook.GeneratedEntries + 2));
        Assert.That(entries.Select(entry => entry.Number), Is.Ordered.Descending);
        Assert.That(entries.All(entry => entry.Amount >= 0), Is.True);
        Assert.That(entries.Select(entry => entry.Id).Distinct().Count(), Is.EqualTo(entries.Count));
        Assert.That(entries.Select(entry => entry.Category).Distinct(),
            Is.SubsetOf(LocalLogbook.Categories.Select(category => category.Code)));
        Assert.That(LocalLogbook.Entries(NewSession()).Select(e => e.Text), Is.EqualTo(entries.Select(e => e.Text)));
        Assert.That(entries.All(entry => entry.Id.StartsWith("7-", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Entries_DifferPerAccount()
    {
        Assert.That(LocalLogbook.Entries(NewSession(gameUserId: 8)).Select(e => e.Text),
            Is.Not.EqualTo(LocalLogbook.Entries(NewSession(gameUserId: 7)).Select(e => e.Text)));
    }

    [Test]
    public void CategoryFilter_ShowsOnlyThatCategory()
    {
        foreach (var category in LocalLogbook.Categories)
        {
            var result = Resolve($"category={category.Code}");
            Assert.That(result.Status, Is.EqualTo(200), category.Code);
            foreach (var other in LocalLogbook.Categories.Where(c => c != category))
                Assert.That(result.Html, Does.Not.Contain($"<td>{other.Code}</td>"), category.Code);
        }
    }

    [TestCase("category=all", 200)]
    [TestCase("category=", 400)]
    [TestCase("page=", 400)]
    [TestCase("page=4", 200)]
    [TestCase("page=5", 404)]
    [TestCase("category=account&page=2", 404)]
    [TestCase("category=unknown", 404)]
    [TestCase("category=Combat", 404)]
    [TestCase("category=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", 400)]
    [TestCase("page=0", 400)]
    [TestCase("page=-1", 400)]
    [TestCase("page=99999", 400)]
    [TestCase("page=1.5", 400)]
    [TestCase("page=%EF%BC%91", 400)]
    [TestCase("page=1&page=1", 400)]
    [TestCase("category=combat&category=economy", 400)]
    [TestCase("quantity=1", 400)]
    [TestCase("entry=7-1", 200)]
    [TestCase("entry=7-32", 200)]
    [TestCase("entry=7-33", 404)]
    [TestCase("entry=7-9999", 404)]
    [TestCase("entry=8-1", 403)]
    [TestCase("entry=99999999999-1", 403)]
    [TestCase("entry=", 400)]
    [TestCase("entry=7", 400)]
    [TestCase("entry=7-", 400)]
    [TestCase("entry=-1", 400)]
    [TestCase("entry=7--1", 400)]
    [TestCase("entry=7-0", 400)]
    [TestCase("entry=7-1x", 400)]
    [TestCase("entry=7-99999", 404)]
    [TestCase("entry=99999999999999999999-1", 400)]
    [TestCase("entry=7-9223372036854775808", 400)]
    [TestCase("entry=07-1", 400)]
    [TestCase("entry=7-01", 400)]
    [TestCase("ACTION=internalLogbook", 400)]
    [TestCase("Action=internalLogbook", 400)]
    [TestCase("action=INTERNALLOGBOOK", 400)]
    [TestCase("category=combat%0A", 400)]
    [TestCase("from=%3Cscript%3E", 200)]
    [TestCase("entry=7-1&entry=7-2", 400)]
    public void Query_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve("action=internalLogbook&" + query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.That(result.Html, Does.Contain($"Logbook request failed ({status})"));
    }

    [Test]
    public void EntryDetail_ShowsOwnEntry()
    {
        var result = Resolve("entry=7-32");
        Assert.That(result.Status, Is.EqualTo(200));
        Assert.That(result.Html, Does.Contain("Logbook entry 7-32"));
        Assert.That(result.Html, Does.Contain("Logged in to the local emulator"));
    }

    [Test]
    public void MissingOrExpiredSession_Returns401()
    {
        Assert.That(Resolve("", session: null).Status, Is.EqualTo(401));
        Assert.That(Resolve("", NewSession(expiresAt: Now)).Status, Is.EqualTo(401));
        Assert.That(Resolve("", NewSession(expiresAt: Now.AddSeconds(-1))).Status, Is.EqualTo(401));
        Assert.That(Resolve("", NewSession(expiresAt: Now.AddSeconds(1))).Status, Is.EqualTo(200));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("xyz")]
    [TestCase("MMO")]
    public void MissingOrWrongCompany_Returns403(string? company)
    {
        var result = Resolve("", NewSession(company));
        Assert.That(result.Status, Is.EqualTo(403));
        Assert.That(result.Html, Does.Contain("Choose a company"));
    }

    [Test]
    public void SessionChecks_RunBeforeQueryValidation()
    {
        Assert.That(Resolve("bogus=1", session: null).Status, Is.EqualTo(401));
        Assert.That(Resolve("bogus=1", NewSession("xyz")).Status, Is.EqualTo(403));
    }

    [Test]
    public async Task ConcurrentRequests_ReturnIdenticalPagesAndLeaveSessionUntouched()
    {
        var session = NewSession();
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => Resolve("category=economy", session))));
        Assert.That(results.Select(r => r.Html).Distinct().Count(), Is.EqualTo(1));
        Assert.That(results.All(r => r.Status == 200), Is.True);
        Assert.That(session.Company, Is.EqualTo("mmo"));
    }

    [Test]
    public void Pagination_LinksStayWithinBounds()
    {
        var first = Resolve("page=1");
        var last = Resolve("page=4");
        Assert.That(first.Html, Does.Not.Contain(">Previous<"));
        Assert.That(first.Html, Does.Contain("category=all&amp;page=2"));
        Assert.That(last.Html, Does.Not.Contain(">Next<"));
        Assert.That(last.Html, Does.Contain("category=all&amp;page=3"));
    }

    private static LocalLogbook.Result Resolve(string query) => Resolve(query, NewSession());

    private static LocalLogbook.Result Resolve(string query, GameAuthSession? session)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" +
            (query.StartsWith("action=", StringComparison.Ordinal) ? query
                : "action=internalLogbook" + (query.Length == 0 ? "" : "&" + query)));
        return LocalLogbook.Resolve(context.Request.Query, session, Now);
    }
}
