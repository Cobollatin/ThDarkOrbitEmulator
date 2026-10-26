using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalForumHelpValidationTests
{
    // Local indexInternal.es menu actions that a topic may link to.
    private static readonly string[] LocalMenus =
    [
        "internalCompanyChoose", "internalDock", "internalShop", "internalAssembly", "internalSkylab",
        "internalTechFactory", "internalGalaxyGates", "internalEvents", "internalQuests", "internalAuction",
        "internalClan", "internalAccount"
    ];

    private static GameAuthSession Session(string? company = "mmo") => new()
    {
        AccountId = Guid.NewGuid(),
        Username = "pilot",
        GameUserId = 42,
        Company = company,
        Dosid = "dosid",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));

    [Test]
    public void TopicsHaveUniqueIdsValidFormatAndKnownMenus()
    {
        Assert.That(LocalForumHelp.Topics.Select(topic => topic.Id), Is.Unique);
        foreach (var topic in LocalForumHelp.Topics)
        {
            Assert.That(topic.Id, Does.Match("^[a-z0-9-]{1,32}$"));
            Assert.That(LocalMenus, Does.Contain(topic.Menu), topic.Id);
            Assert.That(topic.Title, Is.Not.Empty);
            Assert.That(topic.Answer, Is.Not.Empty);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IndexShowsTopicCountAndNoTopicNavigation(bool forum)
    {
        var page = LocalForumHelp.Page(forum);
        Assert.That(page, Does.Contain($"<p id=\"topicCount\">{LocalForumHelp.Topics.Count} topics</p>"));
        Assert.That(page, Does.Not.Contain("id=\"topicNav\""));
        Assert.That(page, Does.Not.Contain("id=\"openMenu\""));
    }

    [Test]
    public void EveryDetailLinksItsMenuAndAdjacentTopics()
    {
        var topics = LocalForumHelp.Topics;
        for (var i = 0; i < topics.Count; i++)
        {
            var page = LocalForumHelp.Page(false, topics[i].Id);
            Assert.That(page, Does.Contain($"id=\"openMenu\" href=\"/indexInternal.es?action={topics[i].Menu}\""));
            Assert.That(page, Does.Not.Contain("id=\"topicCount\""));
            if (i > 0)
                Assert.That(page, Does.Contain($"rel=\"prev\" href=\"/indexInternal.es?action=internalHelp&amp;topic={topics[i - 1].Id}\""));
            else
                Assert.That(page, Does.Not.Contain("rel=\"prev\""));
            if (i < topics.Count - 1)
                Assert.That(page, Does.Contain($"rel=\"next\" href=\"/indexInternal.es?action=internalHelp&amp;topic={topics[i + 1].Id}\""));
            else
                Assert.That(page, Does.Not.Contain("rel=\"next\""));
        }
    }

    [Test]
    public void ForumDetailNavigationStaysOnForum()
    {
        var page = LocalForumHelp.Page(true, "skylab");
        Assert.That(page, Does.Contain("action=internalForum&amp;topic=assembly"));
        Assert.That(page, Does.Contain("action=internalForum&amp;topic=tech-factory"));
        Assert.That(page, Does.Not.Contain("action=internalHelp&amp;topic="));
        Assert.That(page, Does.Contain("Open Skylab"));
    }

    [TestCase(Page.Forum, "internalForum", "topic")]
    [TestCase(Page.Help, "internalHelp", "topic")]
    [TestCase(Page.Support, "internalSupport", "section")]
    public void ValueAtLimitIsAcceptedAndOverLimitIsRejected(Page page, string action, string idKey)
    {
        var atLimit = LocalLinksRequest.Resolve(
            Query(("action", action), ("from", new string('a', LocalLinksRequest.MaxValueLength))), Session(), page);
        Assert.That(atLimit.Status, Is.EqualTo(StatusCodes.Status200OK));

        foreach (var key in new[] { "from", "prc", "lang" })
        {
            var over = LocalLinksRequest.Resolve(
                Query(("action", action), (key, new string('a', LocalLinksRequest.MaxValueLength + 1))), Session(), page);
            Assert.That(over.Status, Is.EqualTo(StatusCodes.Status400BadRequest), key);
            Assert.That(over.Html, Does.Contain("at most 64 characters"));
        }

        var longId = LocalLinksRequest.Resolve(Query(("action", action), (idKey, new string('a', 33))), Session(), page);
        Assert.That(longId.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [TestCase("evilParam")]
    [TestCase("<img src=x>")]
    [TestCase("topic2")]
    public void UnknownParameterNamesAreNotEchoed(string key)
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalHelp"), (key, "1")), Session(), Page.Help);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Unknown parameter"));
        Assert.That(result.Html, Does.Not.Contain(key));
        Assert.That(result.Html, Does.Not.Contain("&lt;img"));
    }

    [TestCase(Page.Forum, "topic", "secretname")]
    [TestCase(Page.Help, "topic", "zzz-unknown")]
    [TestCase(Page.Support, "section", "nope-section")]
    public void UnknownIdentifiersReturn404WithoutEchoingThem(Page page, string idKey, string id)
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "x"), (idKey, id)), Session(), page);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Not.Contain(id));
        Assert.That(result.Html, Does.Contain("Choose one from the list"));
    }

    [Test]
    public void NewTopicsResolveForSessionWithoutCompanyAndGuardSessionAndCompany()
    {
        foreach (var topic in new[] { "skylab", "tech-factory", "quests", "auction", "clan", "account", "assembly" })
        {
            var result = LocalLinksRequest.Resolve(Query(("action", "internalHelp"), ("topic", topic)), Session(null), Page.Help);
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK), topic);
        }
        Assert.That(LocalLinksRequest.Resolve(Query(("action", "internalHelp"), ("topic", "clan")), Session("abc"), Page.Help).Status,
            Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(LocalLinksRequest.Resolve(Query(("action", "internalHelp"), ("topic", "clan")), null, Page.Help).Status,
            Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task UnknownGameApiActionIsHtmlNotFoundAndNoStore()
    {
        var result = LocalLinksRequest.UnknownGameApiAction();
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Contain("id=\"localLinksError\""));
        Assert.That(result.Html, Does.Contain("action=internalForum"));

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await LocalLinksRequest.WriteAsync(context.Response, result, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(body.Length, Is.GreaterThan(0));
    }
}
