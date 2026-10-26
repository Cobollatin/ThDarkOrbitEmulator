using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSupportRelatedTopicsTests
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

    private static LocalLinksRequest.Result Resolve(string? section, GameAuthSession? session)
    {
        var pairs = new Dictionary<string, StringValues> { ["action"] = "internalSupport" };
        if (section is not null)
            pairs["section"] = section;
        return LocalLinksRequest.Resolve(new QueryCollection(pairs), session, Page.Support);
    }

    [Test]
    public void EveryRelatedTopicIsAKnownHelpTopicOfAKnownSection()
    {
        Assert.That(LocalSupport.RelatedHelpTopics.Keys, Is.SubsetOf(LocalSupport.Sections.Select(item => item.Id)));
        foreach (var (_, topics) in LocalSupport.RelatedHelpTopics)
        {
            Assert.That(topics, Is.Not.Empty.And.Unique);
            Assert.That(topics, Is.SubsetOf(LocalForumHelp.Topics.Select(item => item.Id)));
        }
    }

    [TestCase("getting-started", new[] { "company", "hangar" })]
    [TestCase("account", new[] { "account" })]
    [TestCase("community", new[] { "clan" })]
    [TestCase("payment", new[] { "shop" })]
    [TestCase("security", new[] { "account" })]
    public void SectionListsItsHelpTopicsInCatalogOrder(string section, string[] expected)
    {
        var result = Resolve(section, Session());
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("<ul id=\"supportRelatedTopics\">"));
        var listed = LocalForumHelp.Topics.Where(item => result.Html.Contains($"<li data-topic=\"{item.Id}\">")).Select(item => item.Id);
        Assert.That(listed, Is.EqualTo(expected));
        var positions = expected.Select(id => result.Html.IndexOf($"data-topic=\"{id}\"", StringComparison.Ordinal)).ToArray();
        Assert.That(positions, Is.Ordered);
        foreach (var id in expected)
            Assert.That(result.Html, Does.Contain($"href=\"/indexInternal.es?action=internalHelp&amp;topic={id}\">Help: "));
    }

    [TestCase("connection")]
    [TestCase("legal")]
    [TestCase("client-links")]
    public void SectionWithoutMatchingHelpTopicHasNoList(string section)
    {
        var result = Resolve(section, Session());
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Not.Contain("supportRelatedTopics"));
    }

    [Test]
    public void OverviewHasNoRelatedTopics()
    {
        var result = Resolve(null, Session());
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Not.Contain("supportRelatedTopics"));
    }

    [Test]
    public void RelatedTopicsNeedASession()
    {
        var result = Resolve("payment", null);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(result.Html, Does.Not.Contain("supportRelatedTopics"));
    }

    [Test]
    public void RelatedTopicsAreShownWithoutACompanyToo()
    {
        var session = Session();
        session.Company = null;
        var result = Resolve("getting-started", session);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("<li data-topic=\"company\">"));
    }

    [Test]
    public void EveryLinkedHelpTopicOpens()
    {
        foreach (var id in LocalSupport.RelatedHelpTopics.Values.SelectMany(topics => topics).Distinct())
        {
            var help = LocalLinksRequest.Resolve(new QueryCollection(new Dictionary<string, StringValues>
            {
                ["action"] = "internalHelp",
                ["topic"] = id
            }), Session(), Page.Help);
            Assert.That(help.Status, Is.EqualTo(StatusCodes.Status200OK), id);
        }
    }
}
