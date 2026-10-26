using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-115: optional urgency=ending-soon|this-week|running|permanent list filter.
public class LocalCommandCenterUrgencyFilterTests
{
    [TestCase("urgency=ending-soon", 200)]
    [TestCase("urgency=this-week", 200)]
    [TestCase("urgency=running", 200)]
    [TestCase("urgency=permanent", 200)]
    [TestCase("urgency=", 200)]
    [TestCase("category=events&urgency=permanent", 200)]
    [TestCase("category=sales&urgency=running", 200)]
    [TestCase("urgency=running&sort=ending", 200)]
    [TestCase("URGENCY=running", 400)]
    [TestCase("urgency=Running", 400)]
    [TestCase("urgency=soon", 400)]
    [TestCase("urgency=ending_soon", 400)]
    [TestCase("urgency=running%20", 400)]
    [TestCase("urgency=running&urgency=permanent", 400)]
    [TestCase("urgency=%3Cscript%3E", 400)]
    [TestCase("urgency=running%0A", 400)]
    [TestCase("item=Voyagers2607&urgency=running", 400)]
    [TestCase("item=Voyagers2607&urgency=", 200)]
    [TestCase("category=payments&urgency=running", 404)]
    public void Urgency_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve(query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status == 400)
            Assert.Multiple(() =>
            {
                Assert.That(result.Html, Does.Contain("Command center request failed (400)"));
                Assert.That(result.Html, Does.Not.Contain("<script>"));
                Assert.That(result.Html, Does.Not.Contain("soon<"));
            });
    }

    [Test]
    public void Validate_RejectsUrgencyBeforeSession()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalCommandCenter.Validate(Query("urgency=later"))?.Status, Is.EqualTo(400));
            Assert.That(LocalCommandCenter.Validate(Query("item=Voyagers2607&urgency=running"))?.Html,
                Does.Contain("applies to the notice list only"));
            Assert.That(LocalCommandCenter.Validate(Query("urgency=permanent")), Is.Null);
        });
    }

    [TestCase(null)]
    [TestCase("events")]
    [TestCase("deals")]
    [TestCase("news")]
    public void Filter_ListsOnlyMatchingNoticesInEveryGroup(string? category)
    {
        var inCategory = CapturedNewsCatalog.MenuEntries.Where(e => category is null || e.Category == category).ToArray();
        var total = 0;
        foreach (var group in LocalCommandCenter.UrgencyGroups)
        {
            var expected = inCategory.Where(e => LocalCommandCenter.Urgency(e.Seconds) == group).Select(e => e.Name).ToArray();
            total += expected.Length;
            var html = Resolve((category is null ? "" : "category=" + category + "&") + "urgency=" + group).Html;
            Assert.Multiple(() =>
            {
                Assert.That(CountOf(html, "data-urgency=\""), Is.EqualTo(expected.Length), group);
                Assert.That(CountOf(html, $"data-urgency=\"{group}\""), Is.EqualTo(expected.Length), group);
                Assert.That(html, Does.Contain($"<p>{expected.Length} notices</p>"));
                foreach (var name in expected)
                    Assert.That(html, Does.Contain($">{name}</a></h2>"));
                if (expected.Length == 0)
                    Assert.That(html, Does.Contain("No notices match this urgency filter."));
            });
        }
        Assert.That(total, Is.EqualTo(inCategory.Length));
    }

    [Test]
    public void Snapshot_GroupCountsMatchCapturedCatalog()
    {
        var html = Resolve("").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"commandCenterUrgency\""));
            Assert.That(html, Does.Contain("action=internalCommandCenter\" aria-current=\"true\">Any (19)</a>"));
            Assert.That(html, Does.Contain("urgency=ending-soon\">Ends within a day (3)</a>"));
            Assert.That(html, Does.Contain("urgency=permanent\">Permanent (4)</a>"));
            Assert.That(html, Does.Not.Contain("No notices match"));
        });
    }

    [Test]
    public void Filter_IsKeptInTabsAndSortLinksButNotInCards()
    {
        var html = Resolve("category=events&urgency=permanent&sort=ending").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("action=internalCommandCenter&amp;sort=ending&amp;urgency=permanent\">All (19)</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending&amp;urgency=permanent\" aria-current=\"page\">events (5)</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending&amp;urgency=permanent\" aria-current=\"true\">Ending soonest</a>"));
            Assert.That(html, Does.Contain("category=events&amp;urgency=permanent\">Priority</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending\">Any (5)</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending&amp;urgency=permanent\" aria-current=\"true\">Permanent ("));
            Assert.That(html, Does.Not.Contain("item=Voyagers2607&amp;sort=ending&amp;urgency"));
        });
    }

    [Test]
    public void EmptyCategory_KeepsCategoryMessage()
    {
        var html = Resolve("category=sales").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("No notices in this category."));
            Assert.That(html, Does.Contain("category=sales\" aria-current=\"true\">Any (0)</a>"));
        });
    }

    [Test]
    public void DetailView_HasNoUrgencyFilter() =>
        Assert.That(Resolve("item=Voyagers2607").Html, Does.Not.Contain("commandCenterUrgency"));

    [Test]
    public void LiveClock_GroupsFollowTickingCountdowns()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var expected = LocalCommandCenter.EntriesAt(now).Count(e => LocalCommandCenter.Urgency(e.Seconds) == "ending-soon");
        var html = LocalCommandCenter.Resolve(Query("urgency=ending-soon"), now).Html;
        Assert.Multiple(() =>
        {
            Assert.That(CountOf(html, "data-urgency=\"ending-soon\""), Is.EqualTo(expected));
            Assert.That(CountOf(html, "data-urgency=\""), Is.EqualTo(expected));
            Assert.That(html, Does.Contain($"aria-current=\"true\">Ends within a day ({expected})</a>"));
        });
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalCommandCenter&" + query);
        return context.Request.Query;
    }

    private static LocalCommandCenter.Result Resolve(string query) => LocalCommandCenter.Resolve(Query(query));
}
