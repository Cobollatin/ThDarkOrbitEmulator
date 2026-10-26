using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSupportSearchTests
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

    private static IQueryCollection Query(params (string Key, StringValues Value)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static LocalLinksRequest.Result Search(string text) =>
        LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("q", text)), Session(), Page.Support);

    [Test]
    public void SearchMatchesTitleDescriptionAndIdWithoutShowingUnrelatedSections()
    {
        foreach (var text in new[] { "Payments", "vouchers", "payment" })
        {
            var result = Search(text);
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(result.Html, Does.Contain("id=\"supportSearch\""));
            Assert.That(result.Html, Does.Contain("id=\"supportSearchSummary\""));
            Assert.That(result.Html, Does.Contain("section=payment"));
            Assert.That(result.Html, Does.Not.Contain("section=connection"));
        }
    }

    [Test]
    public void SearchIsTrimmedAndEscaped()
    {
        var result = Search("  local <script>  ");
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain("local &lt;script&gt;"));
        Assert.That(result.Html, Does.Not.Contain("<script>"));
        Assert.That(result.Html, Does.Contain("id=\"supportEmpty\""));
        Assert.That(result.Html, Does.Contain("Clear search"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void EmptySearchOpensOverview(string text)
    {
        var result = Search(text);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain($"{LocalSupport.Sections.Count} support topics"));
        Assert.That(result.Html, Does.Not.Contain("supportSearchSummary"));
    }

    [TestCase("x")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("line\nbreak")]
    public void MalformedSearchIsRejectedWithoutEchoingIt(string text)
    {
        var result = Search(text);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Not.Contain("id=\"supportSearch\""));
        Assert.That(result.Html, Does.Not.Contain("name=\"q\""));
    }

    [Test]
    public void SearchCannotBeCombinedWithSectionOrRepeatedOrWrongCase()
    {
        var withSection = LocalLinksRequest.Resolve(
            Query(("action", "internalSupport"), ("section", "legal"), ("q", "legal")), Session(), Page.Support);
        Assert.That(withSection.Status, Is.EqualTo(StatusCodes.Status400BadRequest));

        var repeated = LocalLinksRequest.Resolve(
            Query(("action", "internalSupport"), ("q", new StringValues(["legal", "account"]))), Session(), Page.Support);
        Assert.That(repeated.Status, Is.EqualTo(StatusCodes.Status400BadRequest));

        var wrongCase = LocalLinksRequest.Resolve(
            Query(("action", "internalSupport"), ("Q", "legal")), Session(), Page.Support);
        Assert.That(wrongCase.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public void MissingSessionStillRejectsSearch()
    {
        var result = LocalLinksRequest.Resolve(Query(("action", "internalSupport"), ("q", "legal")), null, Page.Support);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }
}
