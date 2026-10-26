using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCommandCenterSortTests
{
    [TestCase("sort=priority", 200)]
    [TestCase("sort=ending", 200)]
    [TestCase("sort=name", 200)]
    [TestCase("sort=", 200)]
    [TestCase("category=events&sort=ending", 200)]
    [TestCase("item=Voyagers2607&sort=name", 200)]
    [TestCase("SORT=ending", 400)]
    [TestCase("sort=Ending", 400)]
    [TestCase("sort=countdown", 400)]
    [TestCase("sort=ending&sort=name", 400)]
    [TestCase("sort=ending%20", 400)]
    [TestCase("sort=%3Cscript%3E", 400)]
    [TestCase("category=payments&sort=ending", 404)]
    public void Sort_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve(query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status == 400)
            Assert.Multiple(() =>
            {
                Assert.That(result.Html, Does.Contain("Command center request failed (400)"));
                Assert.That(result.Html, Does.Not.Contain("<script>"));
                Assert.That(result.Html, Does.Not.Contain("countdown<"));
            });
    }

    [TestCase("priority", new[] { "TrinityTrials2601", "Voyagers2607", "bonus_gg_reward", "DSE202505", "SeasonPass202608" })]
    [TestCase("ending", new[] { "bonus_gg_reward", "SeasonPass202608", "TrinityTrials2601", "DSE202505", "Voyagers2607" })]
    [TestCase("name", new[] { "bonus_gg_reward", "DSE202505", "SeasonPass202608", "TrinityTrials2601", "Voyagers2607" })]
    public void Sort_OrdersCapturedEventNotices(string sort, string[] expected)
    {
        var events = CapturedNewsCatalog.MenuEntries.Where(entry => entry.Category == "events");
        Assert.That(LocalCommandCenter.Sorted(events, sort).Select(entry => entry.Name), Is.EqualTo(expected));

        var html = Resolve("category=events&sort=" + sort).Html;
        var positions = expected.Select(name => html.IndexOf($">{name}</a></h2>", StringComparison.Ordinal)).ToArray();
        Assert.That(positions, Is.All.GreaterThan(0));
        Assert.That(positions, Is.Ordered);
    }

    [Test]
    public void Sort_IsKeptInTabsCardsAndMarkedInSortNavigation()
    {
        var html = Resolve("category=events&sort=ending").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("action=internalCommandCenter&amp;sort=ending\">All (19)</a>"));
            Assert.That(html, Does.Contain("category=deals&amp;sort=ending\">deals ("));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending\" aria-current=\"page\">events (5)</a>"));
            Assert.That(html, Does.Contain("item=Voyagers2607&amp;sort=ending\">Voyagers2607</a>"));
            Assert.That(html, Does.Contain("id=\"commandCenterSort\""));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending\" aria-current=\"true\">Ending soonest</a>"));
            Assert.That(html, Does.Contain("category=events\">Priority</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=name\">Name</a>"));
        });

        var defaultPage = Resolve("").Html;
        Assert.Multiple(() =>
        {
            Assert.That(defaultPage, Does.Not.Contain("&amp;sort=priority"));
            Assert.That(defaultPage, Does.Contain("action=internalCommandCenter\" aria-current=\"true\">Priority</a>"));
            Assert.That(defaultPage, Does.Contain("action=internalCommandCenter&amp;sort=ending\">Ending soonest</a>"));
        });
    }

    [Test]
    public void Detail_PositionAndNeighboursFollowSelectedSort()
    {
        var html = Resolve("item=SeasonPass202608&sort=ending").Html;
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"commandCenterPosition\">Notice 2 of 5 in events</span>"));
            Assert.That(html, Does.Contain("item=bonus_gg_reward&amp;sort=ending\">Previous: bonus_gg_reward</a>"));
            Assert.That(html, Does.Contain("item=TrinityTrials2601&amp;sort=ending\">Next: TrinityTrials2601</a>"));
            Assert.That(html, Does.Contain("category=events&amp;sort=ending\">Back to events</a>"));
            Assert.That(html, Does.Not.Contain("id=\"commandCenterSort\""));
            Assert.That(html, Does.Not.Contain("id=\"commandCenterSummary\""));
        });

        var byPriority = Resolve("item=SeasonPass202608").Html;
        Assert.Multiple(() =>
        {
            Assert.That(byPriority, Does.Contain("Notice 5 of 5 in events"));
            Assert.That(byPriority, Does.Contain("item=DSE202505\">Previous: DSE202505</a>"));
            Assert.That(byPriority, Does.Not.Contain("rel=\"next\""));
        });
    }

    [TestCase(0, "ending-soon")]
    [TestCase(65568, "ending-soon")]
    [TestCase(86399, "ending-soon")]
    [TestCase(86400, "this-week")]
    [TestCase(414768, "this-week")]
    [TestCase(604800, "running")]
    [TestCase(1278768, "running")]
    [TestCase(31535999, "running")]
    [TestCase(31536000, "permanent")]
    [TestCase(755550769, "permanent")]
    public void Urgency_GroupsCountdownSnapshots(int seconds, string expected) =>
        Assert.That(LocalCommandCenter.Urgency(seconds), Is.EqualTo(expected));

    [Test]
    public void Cards_ShowUrgencyAndListShowsSummary()
    {
        var all = Resolve("").Html;
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("id=\"commandCenterSummary\">Ending soonest: bk_and_lf_sale (18h 12m) · Ending within a day: 3 · Permanent: 4</p>"));
            Assert.That(CountOf(all, "data-urgency=\""), Is.EqualTo(19));
            Assert.That(CountOf(all, "data-urgency=\"ending-soon\""), Is.EqualTo(3));
            Assert.That(CountOf(all, "data-urgency=\"permanent\""), Is.EqualTo(4));
            Assert.That(all, Does.Contain("<p class=\"urgency\">Ends within a day</p>"));
            Assert.That(all, Does.Contain("<p class=\"urgency\">Ends this week</p>"));
            Assert.That(all, Does.Contain("<p class=\"urgency\">Running</p>"));
            Assert.That(all, Does.Contain("<p class=\"urgency\">Permanent</p>"));
        });

        var events = Resolve("category=events").Html;
        Assert.That(events, Does.Contain("Ending soonest: bonus_gg_reward (18h 12m) · Ending within a day: 1 · Permanent: 0</p>"));
        var empty = Resolve("category=upselling&sort=name").Html;
        Assert.Multiple(() =>
        {
            Assert.That(empty, Does.Not.Contain("id=\"commandCenterSummary\""));
            Assert.That(empty, Does.Contain("id=\"commandCenterEmpty\""));
        });
    }

    [Test]
    public void Page_FallsBackToPriorityForUnknownSort() =>
        Assert.That(LocalCommandCenter.Page("events", null, "bogus"), Is.EqualTo(LocalCommandCenter.Page("events", null)));

    private static int CountOf(string html, string value)
    {
        var count = 0;
        for (var i = html.IndexOf(value, StringComparison.Ordinal); i >= 0; i = html.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static LocalCommandCenter.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalCommandCenter&" + query);
        return LocalCommandCenter.Resolve(context.Request.Query);
    }
}
