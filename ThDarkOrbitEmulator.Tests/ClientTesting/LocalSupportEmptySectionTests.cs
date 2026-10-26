using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-181: an explicitly empty section/topic is malformed; omitting it still opens the overview.
public class LocalSupportEmptySectionTests
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

    private static string Action(Page page) => page switch
    {
        Page.Forum => "internalForum",
        Page.Help => "internalHelp",
        _ => "internalSupport"
    };

    private static string IdKey(Page page) => page == Page.Support ? "section" : "topic";

    [Test]
    public void EmptySectionReturnsBadRequestInsteadOfOverview()
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("section", "")), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("The section must not be empty"));
        Assert.That(result.Html, Does.Contain("href=\"/indexInternal.es?action=internalSupport\""));
        Assert.That(result.Html, Does.Not.Contain("id=\"localSupport\""));
        Assert.That(result.Html, Does.Not.Contain("id=\"supportConnectionChecks\""));
    }

    // Forum and Help keep their existing contract: an empty topic shows the index (LocalLinksRequestTests).
    [TestCase(Page.Forum)]
    [TestCase(Page.Help)]
    public void EmptyTopicOnForumAndHelpStillShowsTheIndex(Page page)
    {
        var result = LocalLinksRequest.Resolve(Query(("action", Action(page)), (IdKey(page), "")), Session(), page);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [TestCase(Page.Support)]
    [TestCase(Page.Forum)]
    [TestCase(Page.Help)]
    public void OmittedIdStillOpensTheOverview(Page page)
    {
        var result = LocalLinksRequest.Resolve(Query(("action", Action(page))), Session(), page);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public void OmittedSectionListsEverySupportTopic()
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("from", "internalStart")), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain($"{LocalSupport.Sections.Count} support topics"));
        Assert.That(result.Html, Does.Not.Contain("data-section="));
    }

    [TestCase(" ")]
    [TestCase(" ")]
    public void BlankSectionIsStillRejected(string section)
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("section", section)), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public void SessionChecksStillRunBeforeTheEmptySectionCheck()
    {
        var missing = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("section", "")), null, Page.Support);
        Assert.That(missing.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        var unknownCompany = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("section", "")), Session("xyz"), Page.Support);
        Assert.That(unknownCompany.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public void UnknownKeyIsReportedBeforeTheEmptySection()
    {
        // Unknown keys are reported first, as for every other malformed request.
        var result = LocalLinksRequest.Resolve(
            Query(("action", "internalSupport"), ("section", ""), ("extra", "1")), Session(), Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("Unknown parameter"));
    }
}
