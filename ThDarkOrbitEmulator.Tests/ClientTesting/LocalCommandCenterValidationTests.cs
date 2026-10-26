using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCommandCenterValidationTests
{
    [TestCase("", 200)]
    [TestCase("category=events", 200)]
    [TestCase("category=upselling", 200)]
    [TestCase("item=Voyagers2607", 200)]
    [TestCase("category=events&item=Voyagers2607", 200)]
    [TestCase("category=&item=", 200)]
    [TestCase("from=internalStart&prc=100&lang=en", 200)]
    [TestCase("category=payments", 404)]
    [TestCase("item=unknown", 404)]
    [TestCase("category=deals&item=Voyagers2607", 404)]
    [TestCase("category=Events", 404)]
    [TestCase("category=events&category=deals", 400)]
    [TestCase("item=discord&item=discord", 400)]
    [TestCase("page=2", 400)]
    [TestCase("quantity=1", 400)]
    [TestCase("item=%3Cscript%3E", 400)]
    [TestCase("item=Voyagers2607%00", 400)]
    [TestCase("category=..%2Fevents", 400)]
    [TestCase("item=%EF%BC%A4iscord", 400)]
    [TestCase("item=discord%20", 400)]
    public void Query_IsStrictlyValidated(string query, int status)
    {
        var result = Resolve("action=internalCommandCenter&" + query);
        Assert.That(result.Status, Is.EqualTo(status), result.Html);
        if (status != 200)
            Assert.Multiple(() =>
            {
                Assert.That(result.Html, Does.Contain($"Command center request failed ({status})"));
                Assert.That(result.Html, Does.Not.Contain("<script>"));
                Assert.That(result.Html, Does.Contain("action=internalCommandCenter"));
            });
    }

    [Test]
    public void OverlongValues_AreRejectedWithoutEcho()
    {
        var value = new string('a', 65);
        var result = Resolve("action=internalCommandCenter&item=" + value);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(400));
            Assert.That(result.Html, Does.Not.Contain(value));
        });
        Assert.That(Resolve("action=internalCommandCenter&secret=" + value).Html, Does.Not.Contain("secret"));
    }

    [Test]
    public void Unauthorized_LinksToSignIn()
    {
        var result = LocalCommandCenter.Unauthorized();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(401));
            Assert.That(result.Html, Does.Contain("Command center request failed (401)"));
            Assert.That(result.Html, Does.Contain("<a href=\"/\">Sign in</a>"));
        });
    }

    [Test]
    public void Navigation_ShowsCategoryCountsAndCurrentFilter()
    {
        var all = Resolve("action=internalCommandCenter").Html;
        var counts = CapturedNewsCatalog.MenuEntries.GroupBy(entry => entry.Category)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("aria-current=\"page\">All (19)</a>"));
            foreach (var (category, count) in counts)
                Assert.That(all, Does.Contain($"category={category}\">{category} ({count})</a>"), category);
            Assert.That(all, Does.Contain("upselling (0)</a>"));
        });

        var deals = Resolve("action=internalCommandCenter&category=deals").Html;
        Assert.Multiple(() =>
        {
            Assert.That(deals, Does.Contain($"category=deals\" aria-current=\"page\">deals ({counts["deals"]})</a>"));
            Assert.That(deals, Does.Not.Contain("aria-current=\"page\">All"));
        });

        var empty = Resolve("action=internalCommandCenter&category=upselling").Html;
        Assert.Multiple(() =>
        {
            Assert.That(empty, Does.Contain("0 notices"));
            Assert.That(empty, Does.Contain("id=\"commandCenterEmpty\""));
        });
    }

    [Test]
    public void Detail_ShowsCountdownUnityIdAndNeighbourLinks()
    {
        var events = CapturedNewsCatalog.MenuEntries.Where(entry => entry.Category == "events")
            .OrderBy(entry => entry.Priority).ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name).ToArray();
        var first = Resolve("action=internalCommandCenter&item=" + events[0]).Html;
        var middle = Resolve("action=internalCommandCenter&item=" + events[1]).Html;
        var last = Resolve("action=internalCommandCenter&item=" + events[^1]).Html;
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Contain("id=\"commandCenterDetail\""));
            Assert.That(first, Does.Contain("category=events\">Back to events</a>"));
            Assert.That(first, Does.Not.Contain("rel=\"prev\""));
            Assert.That(first, Does.Contain($"rel=\"next\" href=\"/indexInternal.es?action=internalCommandCenter&amp;item={events[1]}\""));
            Assert.That(middle, Does.Contain($"item={events[0]}\">Previous: {events[0]}</a>"));
            Assert.That(middle, Does.Contain($"item={events[2]}\">Next: {events[2]}</a>"));
            Assert.That(last, Does.Contain("rel=\"prev\""));
            Assert.That(last, Does.Not.Contain("rel=\"next\""));
            Assert.That(first, Does.Contain("Unity news id: BE_" + events[0]));
        });

        var voyagers = Resolve("action=internalCommandCenter&item=Voyagers2607").Html;
        Assert.That(voyagers, Does.Contain("Countdown snapshot: 14d 19h 12m (1278768 seconds)"));
        Assert.That(Resolve("action=internalCommandCenter").Html, Does.Not.Contain("id=\"commandCenterDetail\""));
    }

    [TestCase(0, "0h 0m")]
    [TestCase(-5, "0h 0m")]
    [TestCase(65568, "18h 12m")]
    [TestCase(86400, "1d 0h 0m")]
    [TestCase(755550769, "8744d 19h 12m")]
    public void Countdown_IsFormattedFromCapturedSnapshot(int seconds, string expected) =>
        Assert.That(LocalCommandCenter.FormatCountdown(seconds), Is.EqualTo(expected));

    [Test]
    public void EveryCapturedNotice_HasAReachableDetailPage()
    {
        foreach (var entry in CapturedNewsCatalog.MenuEntries)
        {
            var detail = Resolve($"action=internalCommandCenter&category={entry.Category}&item={entry.Name}");
            Assert.That(detail.Status, Is.EqualTo(200), entry.Name);
            Assert.That(detail.Html, Does.Contain("1 notices"), entry.Name);
        }
    }

    private static LocalCommandCenter.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return LocalCommandCenter.Resolve(context.Request.Query);
    }
}
