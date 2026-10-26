using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalForumHelpSearchTests
{
    private static GameAuthSession Session() => new()
    {
        AccountId = Guid.NewGuid(),
        Username = "pilot",
        GameUserId = 7,
        Company = "eic",
        Dosid = "dosid",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));

    private static LocalLinksRequest.Result Resolve(Page page, params (string Key, string Value)[] pairs) =>
        LocalLinksRequest.Resolve(
            Query([("action", page == Page.Forum ? "internalForum" : "internalHelp"), .. pairs]), Session(), page);

    private static string[] ListedTopics(string html) =>
        LocalForumHelp.Topics.Where(item => html.Contains($"data-topic=\"{item.Id}\"")).Select(item => item.Id).ToArray();

    [TestCase(Page.Forum, "internalForum")]
    [TestCase(Page.Help, "internalHelp")]
    public void SearchListsMatchingTopicsCaseInsensitively(Page page, string action)
    {
        var result = Resolve(page, ("q", "CREDITS"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        var expected = LocalForumHelp.Topics.Where(item => LocalForumHelp.Matches(item, "credits")).Select(item => item.Id).ToArray();
        Assert.That(expected, Has.Length.GreaterThan(1));
        Assert.That(ListedTopics(result.Html), Is.EqualTo(expected));
        Assert.That(result.Html, Does.Contain($"<p id=\"topicCount\">{expected.Length} topics match \"CREDITS\"</p>"));
        Assert.That(result.Html, Does.Contain($"<a id=\"clearSearch\" href=\"/indexInternal.es?action={action}\">Clear search</a>"));
        Assert.That(result.Html, Does.Contain("name=\"q\" maxlength=\"40\" value=\"CREDITS\""));
    }

    [Test]
    public void SearchMatchesTitleCategoryAnswerAndMenuName()
    {
        var gates = LocalForumHelp.Topics.Single(item => item.Id == "gates");
        Assert.That(LocalForumHelp.Matches(gates, "galaxy"), Is.True);
        Assert.That(LocalForumHelp.Matches(gates, "activities"), Is.True);
        Assert.That(LocalForumHelp.Matches(gates, "gate parts"), Is.True);
        Assert.That(LocalForumHelp.Matches(gates, "auction"), Is.False);
        var tech = LocalForumHelp.Topics.Single(item => item.Id == "tech-factory");
        Assert.That(LocalForumHelp.Matches(tech, "tech factory"), Is.True);
    }

    [Test]
    public void SingleMatchUsesSingularLabelAndTrimsWhitespace()
    {
        var result = Resolve(Page.Help, ("q", "  auction  "));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ListedTopics(result.Html), Is.EqualTo(new[] { "auction" }));
        Assert.That(result.Html, Does.Contain("<p id=\"topicCount\">1 topic matches \"auction\"</p>"));
    }

    [Test]
    public void SearchInsideCategoryKeepsCategoryAndOnlyListsItsTopics()
    {
        var result = Resolve(Page.Forum, ("category", "production"), ("q", "resources"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ListedTopics(result.Html), Is.EqualTo(new[] { "assembly", "skylab" }));
        Assert.That(result.Html, Does.Contain("2 topics match \"resources\" in Production</p>"));
        Assert.That(result.Html, Does.Contain("<input type=\"hidden\" name=\"category\" value=\"production\">"));
        Assert.That(result.Html, Does.Contain("href=\"/indexInternal.es?action=internalForum&amp;category=production\">Clear search</a>"));
    }

    [TestCase(Page.Forum, "internalForum")]
    [TestCase(Page.Help, "internalHelp")]
    public void CategoryTabsKeepSearchAndShowMatchingCounts(Page page, string action)
    {
        const string search = "resources";
        var result = Resolve(page, ("category", "production"), ("q", search));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        var matches = LocalForumHelp.Topics.Where(item => LocalForumHelp.Matches(item, search)).ToArray();
        Assert.That(result.Html, Does.Contain($"href=\"/indexInternal.es?action={action}&amp;q={search}\">All ({matches.Length})</a>"));
        foreach (var name in LocalForumHelp.Categories)
        {
            var id = LocalForumHelp.CategoryId(name);
            var count = matches.Count(item => item.Category == name);
            Assert.That(result.Html, Does.Contain($"href=\"/indexInternal.es?action={action}&amp;category={id}&amp;q={search}\" data-category=\"{id}\""));
            Assert.That(result.Html, Does.Contain($"{name} ({count})</a>"));
        }
        Assert.That(result.Html, Does.Contain("data-category=\"production\" aria-current=\"page\""));
    }

    [Test]
    public void SearchIsUrlEncodedInCategoryLinksAndHtmlEncodedInForm()
    {
        var result = Resolve(Page.Help, ("q", "credits & items"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("action=internalHelp&amp;category=shop&amp;q=credits%20%26%20items\""));
        Assert.That(result.Html, Does.Contain("name=\"q\" maxlength=\"40\" value=\"credits &amp; items\""));
        Assert.That(result.Html, Does.Not.Contain("&amp;q=credits & items"));
    }

    [Test]
    public void NoMatchShowsEmptyStateAndHidesBoardNotices()
    {
        var result = Resolve(Page.Forum, ("q", "zzzz"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ListedTopics(result.Html), Is.Empty);
        Assert.That(result.Html, Does.Contain("0 topics match \"zzzz\""));
        Assert.That(result.Html, Does.Contain("id=\"noTopics\""));
        Assert.That(result.Html, Does.Not.Contain("forumNotices"));
    }

    [Test]
    public void SearchTextIsHtmlEncodedInPageAndForm()
    {
        var result = Resolve(Page.Help, ("q", "<b>\"x"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Not.Contain("<b>\"x"));
        Assert.That(result.Html, Does.Contain("&lt;b&gt;&quot;x"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void BlankSearchShowsTheFullIndex(string blank)
    {
        var result = Resolve(Page.Forum, ("q", blank));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ListedTopics(result.Html), Has.Length.EqualTo(LocalForumHelp.Topics.Count));
        Assert.That(result.Html, Does.Contain("forumNotices"));
        Assert.That(result.Html, Does.Contain("<p id=\"topicCount\">12 topics</p>"));
    }

    [Test]
    public void IndexAndCategoryPagesShowSearchFormButTopicDetailDoesNot()
    {
        Assert.That(LocalForumHelp.Page(true), Does.Contain("<form id=\"topicSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalForum\">"));
        Assert.That(LocalForumHelp.Page(false, category: "shop"), Does.Contain("id=\"topicSearch\""));
        Assert.That(LocalForumHelp.Page(false, "shop"), Does.Not.Contain("id=\"topicSearch\""));
    }

    [TestCase("a")]
    [TestCase("12345678901234567890123456789012345678901")]
    public void SearchOutsideLengthLimitsReturns400WithoutEcho(string text)
    {
        var result = Resolve(Page.Help, ("q", text));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Search text must be 2-40 characters."));
        Assert.That(result.Html, Does.Not.Contain(text.Length > 1 ? text : "value=\"a\""));
    }

    [Test]
    public void SearchWithTopicReturns400()
    {
        var result = Resolve(Page.Forum, ("topic", "shop"), ("q", "credits"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Search cannot be combined with a topic."));
    }

    [Test]
    public void SearchKeyIsExactAndSingle()
    {
        Assert.That(Resolve(Page.Forum, ("Q", "credits")).Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        var repeated = LocalLinksRequest.Resolve(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["action"] = "internalHelp",
            ["q"] = new StringValues(["shop", "clan"])
        }), Session(), Page.Help);
        Assert.That(repeated.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(Resolve(Page.Forum, ("q", "cre\u0001dits")).Status, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public void SupportAcceptsSectionSearch()
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("q", "account")), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("supportSearchSummary"));
        Assert.That(result.Html, Does.Contain("section=account"));
    }

    [Test]
    public void SearchStillNeedsASession()
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalForum"), ("q", "credits")), null, Page.Forum);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }
}
