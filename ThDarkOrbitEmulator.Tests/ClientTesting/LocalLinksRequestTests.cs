using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLinksRequestTests
{
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

    private static IQueryCollection Query(params (string Key, string[] Values)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Values)));

    private static IQueryCollection Query(string action, string? idKey = null, string? id = null) =>
        idKey is null ? Query(("action", [action])) : Query(("action", [action]), (idKey, [id!]));

    [TestCase(Page.Forum, "internalForum")]
    [TestCase(Page.Help, "internalHelp")]
    [TestCase(Page.Support, "internalSupport")]
    public void MissingOrExpiredSessionReturns401WithSignInLink(Page page, string action)
    {
        var result = LocalLinksRequest.Resolve(Query(action), null, page);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(result.Html, Does.Contain("id=\"localLinksError\""));
        Assert.That(result.Html, Does.Contain("href=\"/\""));
    }

    [Test]
    public void UnknownCompanyIsForbiddenButNoCompanyYetCanReadHelp()
    {
        Assert.That(LocalLinksRequest.Resolve(Query("internalHelp"), Session("xyz"), Page.Help).Status,
            Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(LocalLinksRequest.Resolve(Query("internalHelp"), Session(null), Page.Help).Status,
            Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public void RepeatedOrUnknownParametersAreRejected()
    {
        var repeated = LocalLinksRequest.Resolve(
            Query(("action", ["internalForum"]), ("topic", ["shop", "hangar"])), Session(), Page.Forum);
        Assert.That(repeated.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(repeated.Html, Does.Contain("must appear once"));

        var unknown = LocalLinksRequest.Resolve(
            Query(("action", ["internalSupport"]), ("topic", ["legal"])), Session(), Page.Support);
        Assert.That(unknown.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(unknown.Html, Does.Contain("Unknown parameter"));

        var tolerated = LocalLinksRequest.Resolve(
            Query(("action", ["internalHelp"]), ("from", ["internalStart"]), ("lang", ["es"])), Session(), Page.Help);
        Assert.That(tolerated.Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [TestCase("SHOP")]
    [TestCase("../shop")]
    [TestCase("<script>")]
    [TestCase("shop%00")]
    [TestCase("-1")]
    public void MalformedIdentifiersAreBadRequestAndNeverEchoedUnencoded(string topic)
    {
        var result = LocalLinksRequest.Resolve(Query("internalHelp", "topic", topic), Session(), Page.Help);
        Assert.That(result.Status, topic == "-1" ? Is.EqualTo(StatusCodes.Status404NotFound) : Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Not.Contain("<script>"));
    }

    [Test]
    public void HugeIdentifierIsBadRequestAndUnknownIdIsNotFound()
    {
        var huge = LocalLinksRequest.Resolve(Query("internalSupport", "section", new string('a', 10_000)), Session(), Page.Support);
        Assert.That(huge.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(huge.Html.Length, Is.LessThan(1_000));

        var unknown = LocalLinksRequest.Resolve(Query("internalForum", "topic", "missing"), Session(), Page.Forum);
        Assert.That(unknown.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(unknown.Html, Does.Contain("action=internalForum"));
    }

    [Test]
    public void EmptyIdentifierShowsIndexAndValidIdShowsDetail()
    {
        var index = LocalLinksRequest.Resolve(Query("internalForum", "topic", ""), Session(), Page.Forum);
        Assert.That(index.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(index.Html, Does.Contain("id=\"forumNotices\""));
        foreach (var notice in LocalForumHelp.Notices)
            Assert.That(index.Html, Does.Contain($"id=\"notice-{notice.Id}\""));

        var detail = LocalLinksRequest.Resolve(Query("internalSupport", "section", "payment"), Session(), Page.Support);
        Assert.That(detail.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(detail.Html, Does.Contain("action=internalPayment"));
        Assert.That(detail.Html, Does.Contain("No real payment is taken"));
    }

    [Test]
    public void HelpAndTopicDetailsDoNotShowForumNotices()
    {
        Assert.That(LocalForumHelp.Page(false), Does.Not.Contain("forumNotices"));
        Assert.That(LocalForumHelp.Page(true, "shop"), Does.Not.Contain("forumNotices"));
        Assert.That(LocalSupport.Page("security"), Does.Contain("action=externalLogout"));
        Assert.That(LocalSupport.Sections.Select(section => section.Id), Is.Unique);
        Assert.That(LocalForumHelp.Notices.Select(notice => notice.Id), Is.Unique);
    }

    [Test]
    public void ConcurrentReadsAreDeterministic()
    {
        var expected = LocalLinksRequest.Resolve(Query("internalHelp", "topic", "gates"), Session(), Page.Help).Html;
        var results = Enumerable.Range(0, 32).AsParallel()
            .Select(_ => LocalLinksRequest.Resolve(Query("internalHelp", "topic", "gates"), Session(), Page.Help))
            .ToArray();
        Assert.That(results.All(result => result.Status == StatusCodes.Status200OK && result.Html == expected));
    }

    [Test]
    public async Task WriteSetsStatusHtmlAndNoStore()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await LocalLinksRequest.WriteAsync(context.Response,
            LocalLinksRequest.Resolve(Query("internalForum", "topic", "nope"), Session(), Page.Forum), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(body.Length, Is.GreaterThan(0));
    }
}
