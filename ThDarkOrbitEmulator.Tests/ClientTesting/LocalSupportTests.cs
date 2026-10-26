using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSupportTests
{
    [Test]
    public void IndexListsAllSectionsAndLinksBackToMenu()
    {
        var page = LocalSupport.Page();
        Assert.That(page, Does.Contain("id=\"localSupport\""));
        foreach (var section in LocalSupport.Sections)
            Assert.That(page, Does.Contain($"action=internalSupport&amp;section={section.Id}"));
        Assert.That(page, Does.Contain("action=internalWebGL&amp;from=internalStart"));
        Assert.That(page, Does.Not.Contain("being built"));
    }

    [Test]
    public void DetailShowsLocalGuidanceAndOnlyCapturedExternalDestination()
    {
        var legal = LocalSupport.Page("legal");
        Assert.That(legal, Does.Contain("https://legal.bigpoint.com/PE/imprint/es"));
        Assert.That(legal, Does.Contain("no credits destination"));
        Assert.That(legal, Does.Not.Contain("Connection help"));
        Assert.That(LocalSupport.Page("account"), Does.Contain("action=internalAccount"));
        Assert.That(LocalSupport.Page("community"), Does.Contain("action=internalForum"));
        Assert.That(LocalSupport.IsSection("invalid"), Is.False);
    }

    [Test]
    public async Task SupportLinkNeedsSessionAndRendersHub()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await LocalAccountLinkResponse.WriteAsync(context.Response, "internalSupport", false, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(body.Length, Is.Zero);

        await LocalAccountLinkResponse.WriteAsync(context.Response, "internalSupport", true, CancellationToken.None);
        body.Position = 0;
        var html = await new StreamReader(body).ReadToEndAsync();
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(html, Does.Contain("id=\"localSupport\""));
    }
}
