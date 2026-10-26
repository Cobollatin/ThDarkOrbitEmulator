using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using Page = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.LocalLinksRequest.Page;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSupportHardeningTests
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

    private static IQueryCollection Query(params (string Key, string[] Values)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Values)));

    private static LocalLinksRequest.Result Resolve(string? section, GameAuthSession? session = null) =>
        LocalLinksRequest.Resolve(section is null
            ? Query(("action", ["internalSupport"]))
            : Query(("action", ["internalSupport"]), ("section", [section])), session ?? Session(), Page.Support);

    private static IEnumerable<string> SectionIds() => LocalSupport.Sections.Select(section => section.Id);

    [TestCaseSource(nameof(SectionIds))]
    public void EverySectionResolvesToItsOwnDetail(string id)
    {
        var result = Resolve(id);
        var section = LocalSupport.Sections.Single(item => item.Id == id);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Does.Contain($"data-section=\"{id}\""));
        Assert.That(result.Html, Does.Contain($"<title>{section.Title} - Support"));
        Assert.That(result.Html, Does.Contain("action=internalSupport\">All support topics"));
        Assert.That(result.Html, Does.Contain("action=internalWebGL&amp;from=internalStart"));
        foreach (var other in LocalSupport.Sections.Where(item => item.Id != id))
            Assert.That(result.Html, Does.Not.Contain($"<h2><a href=\"/indexInternal.es?action=internalSupport&amp;section={other.Id}\">"));
    }

    [Test]
    public void IndexHasNoSectionMarkerOrNeighbourNavigation()
    {
        var html = LocalSupport.Page();
        Assert.That(html, Does.Contain("<title>Support - DarkOrbit local emulator</title>"));
        Assert.That(html, Does.Contain($"{LocalSupport.Sections.Count} support topics"));
        Assert.That(html, Does.Not.Contain("data-section"));
        Assert.That(html, Does.Not.Contain("supportNeighbours"));
        Assert.That(html, Does.Not.Contain("supportClientLinks"));
        Assert.That(html, Does.Not.Contain("All support topics"));
        foreach (var section in LocalSupport.Sections)
            Assert.That(html, Does.Not.Contain(section.Description));
    }

    [Test]
    public void NeighbourLinksWalkSectionsInOrderWithoutWrapping()
    {
        var sections = LocalSupport.Sections;
        for (var i = 0; i < sections.Count; i++)
        {
            var html = LocalSupport.Page(sections[i].Id);
            var previous = i > 0 ? $"rel=\"previous\" href=\"/indexInternal.es?action=internalSupport&amp;section={sections[i - 1].Id}\"" : "rel=\"previous\"";
            var next = i < sections.Count - 1 ? $"rel=\"next\" href=\"/indexInternal.es?action=internalSupport&amp;section={sections[i + 1].Id}\"" : "rel=\"next\"";
            Assert.That(html, i > 0 ? Does.Contain(previous) : Does.Not.Contain(previous));
            Assert.That(html, i < sections.Count - 1 ? Does.Contain(next) : Does.Not.Contain(next));
        }
    }

    [Test]
    public void ClientLinksMatchTheServedLinksPayloadInCapturedOrder()
    {
        var links = ClientStubResponses.Links(string.Empty);
        Assert.That(LocalSupport.ClientLinks().Select(link => link.Key),
            Is.EqualTo(new[] { "imprint", "credits", "forum", "support", "help", "accountCenter" }));
        Assert.That(LocalSupport.ClientLinks().Select(link => link.Destination), Is.EqualTo(new[]
        {
            links.Imprint, links.Credits, links.Forum, links.Support, links.Help, links.AccountCenter
        }));
    }

    [Test]
    public void ClientLinksSectionRendersEveryDestinationAndMarksMissingCredits()
    {
        var html = Resolve("client-links").Html;
        Assert.That(html, Does.Contain("id=\"supportClientLinks\""));
        foreach (var link in LocalSupport.ClientLinks())
            Assert.That(html, Does.Contain($"id=\"link-{link.Key}\""));
        Assert.That(html, Does.Contain("<a href=\"https://legal.bigpoint.com/PE/imprint/es\" rel=\"noopener noreferrer\">"));
        Assert.That(html, Does.Contain("<a href=\"https://board-es.darkorbit.com/\" rel=\"noopener noreferrer\">"));
        Assert.That(html, Does.Contain("<a href=\"/indexInternal.es?action=internalSupport\">/indexInternal.es?action=internalSupport</a>"));
        Assert.That(html, Does.Contain("<a href=\"/indexInternal.es?action=internalAccount\">"));
        Assert.That(html, Does.Contain("<a href=\"/GameAPI.php?action=portal.redirectToBoard\">"));
        Assert.That(html, Does.Contain("Not provided by the captured client links"));
        Assert.That(html, Does.Not.Contain("href=\"\""));
    }

    [Test]
    public void OnlyLegalAndClientLinksPointOffTheEmulator()
    {
        foreach (var section in LocalSupport.Sections)
        {
            var html = LocalSupport.Page(section.Id);
            var external = html.Contains("href=\"https://", StringComparison.Ordinal);
            Assert.That(external, Is.EqualTo(section.Id is "legal" or "client-links"), section.Id);
        }
    }

    [TestCase("Legal")]
    [TestCase("client_links")]
    [TestCase("legal ")]
    [TestCase("legal/../account")]
    [TestCase("\"><script>alert(1)</script>")]
    public void MalformedSectionsAreBadRequestWithoutEchoingInput(string section)
    {
        var result = Resolve(section);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("action=internalSupport\">Back to Support"));
        Assert.That(result.Html, Does.Not.Contain(section));
    }

    [TestCase("billing")]
    [TestCase("links")]
    [TestCase("credits")]
    public void UnknownWellFormedSectionsAreNotFound(string section) =>
        Assert.That(Resolve(section).Status, Is.EqualTo(StatusCodes.Status404NotFound));

    [Test]
    public void SupportUsesSectionNotTopicAndToleratesMenuParameters()
    {
        var topic = LocalLinksRequest.Resolve(
            Query(("action", ["internalSupport"]), ("topic", ["legal"])), Session(), Page.Support);
        Assert.That(topic.Status, Is.EqualTo(StatusCodes.Status400BadRequest));

        var repeated = LocalLinksRequest.Resolve(
            Query(("action", ["internalSupport"]), ("section", ["legal", "account"])), Session(), Page.Support);
        Assert.That(repeated.Status, Is.EqualTo(StatusCodes.Status400BadRequest));

        var tolerated = LocalLinksRequest.Resolve(
            Query(("action", ["internalSupport"]), ("section", ["security"]), ("from", ["internalStart"]),
                ("prc", ["100"]), ("lang", ["es"])), Session(), Page.Support);
        Assert.That(tolerated.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(tolerated.Html, Does.Contain("data-section=\"security\""));
    }

    [Test]
    public void SessionAndCompanyAreCheckedBeforeTheSection()
    {
        var expired = LocalLinksRequest.Resolve(
            Query(("action", ["internalSupport"]), ("section", ["<bad>"])), null, Page.Support);
        Assert.That(expired.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(Resolve("<bad>", Session("xyz")).Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(Resolve("getting-started", Session(null)).Status, Is.EqualTo(StatusCodes.Status200OK));
        foreach (var company in new[] { "mmo", "eic", "vru" })
            Assert.That(Resolve("client-links", Session(company)).Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public void SectionIdsAreValidUniqueAndPagesAreDeterministic()
    {
        Assert.That(SectionIds(), Is.Unique);
        Assert.That(SectionIds().All(id => id.Length <= LocalLinksRequest.MaxIdLength
            && id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')));
        var expected = LocalSupport.Page("client-links");
        var pages = Enumerable.Range(0, 16).AsParallel().Select(_ => LocalSupport.Page("client-links")).ToArray();
        Assert.That(pages, Is.All.EqualTo(expected));
    }
}
