using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCommandCenterSearchTests
{
    [TestCase("q=Voyagers", 200)]
    [TestCase("q=voyagers", 200)]
    [TestCase("q=events", 200)]
    [TestCase("q=12th_Birthday", 200)]
    [TestCase("q=zz-no-match", 200)]
    [TestCase("q=", 400)]
    [TestCase("q=a", 400)]
    [TestCase("q=%20events", 400)]
    [TestCase("q=events%20", 400)]
    [TestCase("q=%3Cscript%3E", 400)]
    [TestCase("q=events%0A", 400)]
    [TestCase("q=events&q=deals", 400)]
    public void Search_ValidatesWithoutEchoingInput(string query, int status)
    {
        var result = Resolve(query);
        Assert.That(result.Status, Is.EqualTo(status));
        if (status == 400)
            Assert.That(result.Html, Does.Not.Contain("<script>"));
    }

    [Test]
    public void Search_FiltersCatalogAndKeepsContextInNavigation()
    {
        const string search = "voyagers";
        var matches = CapturedNewsCatalog.MenuEntries.Where(e =>
            e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            e.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            e.Background.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = Resolve("category=events&sort=ending&urgency=running&q=voyagers");
        Assert.That(page.Status, Is.EqualTo(200));
        Assert.Multiple(() =>
        {
            Assert.That(page.Html, Does.Contain($">All ({matches.Length})</a>"));
            Assert.That(page.Html, Does.Contain("category=events&amp;sort=ending&amp;urgency=running&amp;q=voyagers"));
            Assert.That(page.Html, Does.Contain("item=Voyagers2607&amp;sort=ending&amp;q=voyagers"));
            Assert.That(page.Html, Does.Contain("id=\"commandCenterSearchStatus\""));
            Assert.That(page.Html, Does.Contain("name=\"q\" required minlength=\"2\" maxlength=\"40\""));
            Assert.That(page.Html, Does.Contain("name=\"urgency\" value=\"running\""));
            Assert.That(page.Html, Does.Not.Contain("item=Voyagers2607&amp;sort=ending&amp;urgency"));
        });
    }

    [Test]
    public void Search_DetailStaysWithinMatchedSiblings()
    {
        var detail = Resolve("item=Voyagers2607&q=Voyagers");
        Assert.Multiple(() =>
        {
            Assert.That(detail.Status, Is.EqualTo(200));
            Assert.That(detail.Html, Does.Contain("Notice 1 of 1 in events"));
            Assert.That(detail.Html, Does.Contain("category=events&amp;q=Voyagers\">Back to events"));
            Assert.That(detail.Html, Does.Not.Contain("rel=\"prev\""));
            Assert.That(detail.Html, Does.Not.Contain("rel=\"next\""));
        });
        Assert.That(Resolve("item=discord&q=Voyagers").Status, Is.EqualTo(404));
    }

    [Test]
    public void Search_EmptyResultsKeepFiltersAndClearLink()
    {
        var page = Resolve("category=events&q=zz-no-match");
        Assert.Multiple(() =>
        {
            Assert.That(page.Status, Is.EqualTo(200));
            Assert.That(page.Html, Does.Contain("<p>0 notices</p>"));
            Assert.That(page.Html, Does.Contain("Clear search"));
            Assert.That(page.Html, Does.Contain("category=events\">Clear search"));
            Assert.That(page.Html, Does.Not.Contain("data-seconds="));
        });
    }

    private static LocalCommandCenter.Result Resolve(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?action=internalCommandCenter&" + query);
        return LocalCommandCenter.Resolve(context.Request.Query);
    }
}
