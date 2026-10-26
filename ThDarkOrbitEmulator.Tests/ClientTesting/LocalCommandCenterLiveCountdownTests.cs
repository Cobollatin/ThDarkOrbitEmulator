using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-100: the command center page shows the same ticking countdowns as /unityApi/news.php.
public class LocalCommandCenterLiveCountdownTests
{
    private static readonly DateTimeOffset Snapshot = CapturedNewsCatalog.SnapshotAt;

    [Test]
    public void Page_AtSnapshotTime_MatchesCapturedSnapshotValues()
    {
        var live = LocalCommandCenter.Page(null, null, "priority", Snapshot);
        Assert.Multiple(() =>
        {
            Assert.That(Seconds(live), Is.EquivalentTo(CapturedNewsCatalog.MenuEntries.Select(e => e.Seconds)));
            Assert.That(live, Does.Contain("Countdown: 14d 19h 12m (1278768 seconds)"));
            Assert.That(live, Does.Not.Contain("Countdown snapshot"));
        });
    }

    [Test]
    public void Page_WithoutClock_KeepsSnapshotLabel()
    {
        var page = LocalCommandCenter.Page(null, null);
        Assert.That(page, Does.Contain("Countdown snapshot: 14d 19h 12m (1278768 seconds)"));
    }

    [TestCase(0)]
    [TestCase(3_600)]
    [TestCase(65_567)]
    [TestCase(65_568)]
    [TestCase(86_400 * 30)]
    [TestCase(86_400 * 365 * 30)]
    public void Page_MatchesNewsPhpCountdownsAtSameInstant(long elapsedSeconds)
    {
        var now = Snapshot.AddSeconds(elapsedSeconds);
        var news = CapturedNewsCatalog.News(now).Data!.ToDictionary(i => i.NewsId!, i => i.Seconds);
        var page = LocalCommandCenter.Page(null, null, "name", now);
        var names = CapturedNewsCatalog.MenuEntries.Select(e => e.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.That(Seconds(page), Is.EqualTo(names.Select(n => news["BE_" + n]).ToArray()));
    }

    [Test]
    public void Page_OneHourLater_CountsDown()
    {
        var page = LocalCommandCenter.Page(null, "Voyagers2607", "priority", Snapshot.AddHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-seconds=\"1275168\""));
            Assert.That(page, Does.Contain("Countdown: 14d 18h 12m (1275168 seconds)"));
        });
    }

    [Test]
    public void Page_BeforeSnapshot_ShowsCapturedValues()
    {
        var page = LocalCommandCenter.Page(null, null, "priority", Snapshot.AddDays(-3));
        Assert.That(Seconds(page), Is.EquivalentTo(CapturedNewsCatalog.MenuEntries.Select(e => e.Seconds)));
    }

    [Test]
    public void UrgencyAndSummary_FollowLiveCountdowns()
    {
        // 65,000 s later the three 65,568 s notices have 568 s left; the 328,368 s ones drop under a week.
        var page = LocalCommandCenter.Page(null, null, "priority", Snapshot.AddSeconds(65_000));
        Assert.Multiple(() =>
        {
            Assert.That(Count(page, "data-urgency=\"ending-soon\""), Is.EqualTo(3));
            Assert.That(page, Does.Contain("data-seconds=\"568\""));
            Assert.That(page, Does.Match("Ending soonest: [A-Za-z_]+ \\(0h 9m\\)"));
            Assert.That(Count(page, "data-urgency=\"permanent\""), Is.EqualTo(4));
        });

        // At exactly 65,568 s the short notices restart with their captured duration.
        var restarted = LocalCommandCenter.Page(null, null, "priority", Snapshot.AddSeconds(65_568));
        Assert.That(Count(restarted, "data-seconds=\"65568\""), Is.EqualTo(3));
    }

    [Test]
    public void EndingSort_UsesLiveCountdowns()
    {
        // 300,000 s later the 65,568 s notices have restarted four times and have 27,840 s left,
        // ahead of the 328,368 s notices (28,368 s left).
        var now = Snapshot.AddSeconds(300_000);
        var expected = LocalCommandCenter.EntriesAt(now)
            .OrderBy(e => e.Seconds).ThenBy(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Seconds).ToArray();
        var page = LocalCommandCenter.Page(null, null, "ending", now);
        var seconds = Seconds(page);
        Assert.Multiple(() =>
        {
            Assert.That(seconds, Is.EqualTo(expected));
            Assert.That(seconds, Is.Ordered);
            Assert.That(seconds[0], Is.EqualTo(27_840));
        });
    }

    [Test]
    public void DetailNavigation_EndingSort_UsesLiveOrder()
    {
        // Events at +300,000 s: bonus_gg_reward (27,840 s) now ends before SeasonPass202608 (28,368 s).
        var now = Snapshot.AddSeconds(300_000);
        var events = LocalCommandCenter.EntriesAt(now).Where(e => e.Category == "events")
            .OrderBy(e => e.Seconds).ThenBy(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var page = LocalCommandCenter.Page(null, events[1].Name, "ending", now);
        Assert.That(events[0].Name, Is.EqualTo("bonus_gg_reward"));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"Notice 2 of {events.Length} in events"));
            Assert.That(page, Does.Contain($"Previous: {events[0].Name}"));
            Assert.That(page, Does.Contain($"Next: {events[2].Name}"));
        });
    }

    [Test]
    public void Resolve_PassesClockToPage()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalCommandCenter&item=discord");
        var result = LocalCommandCenter.Resolve(context.Request.Query, Snapshot.AddSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(result.Html, Does.Contain("data-seconds=\"755550759\""));
        });
    }

    [Test]
    public void EntriesAt_KeepsCatalogIdentityFields()
    {
        var live = LocalCommandCenter.EntriesAt(Snapshot.AddDays(2));
        Assert.That(live.Select(e => (e.Name, e.Priority, e.Category, e.Background)),
            Is.EqualTo(CapturedNewsCatalog.MenuEntries.Select(e => (e.Name, e.Priority, e.Category, e.Background))));
        Assert.That(LocalCommandCenter.EntriesAt(null), Is.SameAs(CapturedNewsCatalog.MenuEntries));
    }

    private static int[] Seconds(string html) =>
        Regex.Matches(html, "data-seconds=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToArray();

    private static int Count(string html, string token) => Regex.Matches(html, Regex.Escape(token)).Count;
}
