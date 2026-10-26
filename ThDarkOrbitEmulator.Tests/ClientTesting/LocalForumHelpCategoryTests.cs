using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalForumHelpCategoryTests
{
    private static GameAuthSession Session(string? company = "eic") => new()
    {
        AccountId = Guid.NewGuid(),
        Username = "pilot",
        GameUserId = 7,
        Company = company,
        Dosid = "dosid",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));

    private static LocalLinksRequest.Result Help(params (string Key, string Value)[] pairs) =>
        LocalLinksRequest.Resolve(Query([("action", "internalHelp"), .. pairs]), Session(), Page.Help);

    [Test]
    public void CategoriesFollowTopicOrderAndHaveValidIds()
    {
        Assert.That(LocalForumHelp.Categories, Is.EqualTo(new[]
        {
            "Getting started", "Hangar", "Shop", "Production", "Activities", "Economy", "Community", "Account"
        }));
        Assert.That(LocalForumHelp.Categories.Select(LocalForumHelp.CategoryId), Is.Unique);
        foreach (var name in LocalForumHelp.Categories)
            Assert.That(LocalForumHelp.CategoryId(name), Does.Match("^[a-z0-9-]{1,32}$"), name);
        Assert.That(LocalForumHelp.CategoryId("Getting started"), Is.EqualTo("getting-started"));
    }

    [TestCase(true, "internalForum")]
    [TestCase(false, "internalHelp")]
    public void IndexShowsCategoryTabsWithCountsAndMarksAll(bool forum, string action)
    {
        var page = LocalForumHelp.Page(forum);
        Assert.That(page, Does.Contain($"<a href=\"/indexInternal.es?action={action}\" aria-current=\"page\">All (12)</a>"));
        Assert.That(page, Does.Contain($"action={action}&amp;category=production\" data-category=\"production\">Production (3)</a>"));
        Assert.That(page, Does.Contain($"action={action}&amp;category=activities\" data-category=\"activities\">Activities (3)</a>"));
        Assert.That(page, Does.Contain("data-category=\"getting-started\">Getting started (1)</a>"));
        var tabTotal = LocalForumHelp.Categories.Sum(name => LocalForumHelp.Topics.Count(item => item.Category == name));
        Assert.That(tabTotal, Is.EqualTo(LocalForumHelp.Topics.Count));
    }

    [Test]
    public void CategoryFilterListsOnlyItsTopicsAndHidesBoardNotices()
    {
        var result = LocalLinksRequest.Resolve(
            Query(("action", "internalForum"), ("category", "production")), Session(), Page.Forum);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        foreach (var topic in LocalForumHelp.Topics)
            Assert.That(result.Html.Contains($"data-topic=\"{topic.Id}\""), Is.EqualTo(topic.Category == "Production"), topic.Id);
        Assert.That(result.Html, Does.Contain("<p id=\"topicCount\">3 topics in Production</p>"));
        Assert.That(result.Html, Does.Contain("data-category=\"production\" aria-current=\"page\""));
        Assert.That(result.Html, Does.Not.Contain("<a href=\"/indexInternal.es?action=internalForum\" aria-current"));
        Assert.That(result.Html, Does.Not.Contain("id=\"forumNotices\""));
        Assert.That(result.Html, Does.Contain("action=internalForum\">All topics</a>"));
    }

    [Test]
    public void SingleTopicCategoryUsesSingularCount()
    {
        var result = Help(("category", "getting-started"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("<p id=\"topicCount\">1 topic in Getting started</p>"));
    }

    [Test]
    public void DetailLinksBackToItsCategoryWithoutTabs()
    {
        foreach (var topic in LocalForumHelp.Topics)
        {
            var page = LocalForumHelp.Page(false, topic.Id);
            Assert.That(page, Does.Contain(
                $"id=\"topicCategory\" href=\"/indexInternal.es?action=internalHelp&amp;category={LocalForumHelp.CategoryId(topic.Category)}\">More in "),
                topic.Id);
            Assert.That(page, Does.Not.Contain("id=\"topicCategories\""));
        }
    }

    [Test]
    public void TopicInsideMatchingCategoryIsServed()
    {
        var result = Help(("category", "production"), ("topic", "skylab"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("Skylab collectors produce resources"));
    }

    [Test]
    public void TopicOutsideSelectedCategoryIsNotFound()
    {
        var result = Help(("category", "economy"), ("topic", "skylab"));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Contain("not in the selected category"));
    }

    [TestCase("missions")]
    [TestCase("getting")]
    public void UnknownCategoryIsNotFoundAndNotEchoed(string category)
    {
        var result = Help(("category", category));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Not.Contain(category));
    }

    [TestCase("")]
    [TestCase("Production")]
    [TestCase("getting started")]
    [TestCase("<b>")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MalformedCategoryIsBadRequestAndNotEchoed(string category)
    {
        var result = Help(("category", category));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("The category must be"));
        if (category.Length > 0)
            Assert.That(result.Html, Does.Not.Contain(category));
    }

    [Test]
    public void RepeatedCategoryIsBadRequest()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["action"] = "internalHelp",
            ["category"] = new(["shop", "hangar"])
        });
        Assert.That(LocalLinksRequest.Resolve(query, Session(), Page.Help).Status, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public void SupportDoesNotAcceptCategory()
    {
        var result = LocalLinksRequest.Resolve(
            Query(("action", "internalSupport"), ("category", "production")), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Unknown parameter"));
    }

    [TestCase(Page.Forum, "ACTION")]
    [TestCase(Page.Help, "Action")]
    [TestCase(Page.Help, "FROM")]
    [TestCase(Page.Help, "Lang")]
    [TestCase(Page.Forum, "Category")]
    [TestCase(Page.Forum, "TOPIC")]
    [TestCase(Page.Support, "Prc")]
    public void NonCanonicalKeyCasingIsRejected(Page page, string key)
    {
        var result = LocalLinksRequest.Resolve(Query((key, "x")), Session(), page);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Unknown parameter"));
        Assert.That(result.Html, Does.Not.Contain(key));
    }

    [TestCase("from", "a\0b")]
    [TestCase("lang", "en\t")]
    [TestCase("prc", "1\n2")]
    [TestCase("from", "x\u007f")]
    public void ControlCharactersInValuesAreRejectedAndNotEchoed(string key, string value)
    {
        foreach (var page in new[] { Page.Forum, Page.Help, Page.Support })
        {
            var result = LocalLinksRequest.Resolve(Query(("action", "x"), (key, value)), Session(), page);
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest), page.ToString());
            Assert.That(result.Html, Does.Contain("control characters"));
            // Ordinal check: culture-aware Contains ignores DEL and would match the bare prefix.
            Assert.That(result.Html.Contains(value, StringComparison.Ordinal), Is.False, page.ToString());
        }
    }

    [Test]
    public void SessionAndCompanyGuardsRunBeforeCategoryChecks()
    {
        var query = Query(("action", "internalHelp"), ("category", "nope"));
        Assert.That(LocalLinksRequest.Resolve(query, null, Page.Help).Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(LocalLinksRequest.Resolve(query, Session("xyz"), Page.Help).Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(LocalLinksRequest.Resolve(Query(("action", "internalHelp"), ("category", "shop")), Session(null), Page.Help).Status,
            Is.EqualTo(StatusCodes.Status200OK));
    }
}
