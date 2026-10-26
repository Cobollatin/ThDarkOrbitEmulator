using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAccountLinkResponseTests
{
    [TestCase("internalSupport", "localSupport", "Support")]
    [TestCase("internalAccount", "localAccountCenter", "Account center")]
    [TestCase("internalForum", "localForum", "Forum")]
    [TestCase("internalHelp", "localHelp", "Help")]
    public async Task LinksResolveToDistinctLocalPagesForAnAuthenticatedSession(
        string action, string pageId, string title)
    {
        var loginContext = new DefaultHttpContext();
        loginContext.Request.Scheme = "https";
        loginContext.Request.Host = new HostString("localhost", 60952);
        loginContext.Request.Headers["X-Original-Host"] = "gbl3.darkorbit.com";
        var login = System.Text.Json.Nodes.JsonNode.Parse(
            ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.UnityLoginPayloadBuilder.Build(
                new ThDarkOrbitEmulator.ApiService.Features.GameAuth.GameAuthSession(), loginContext.Request))!;
        var links = ClientStubResponses.Links("https://gbl3.darkorbit.com");
        var url = action switch
        {
            "internalSupport" => links.Support,
            "internalAccount" => links.AccountCenter,
            "internalForum" => login["boardLink"]!.GetValue<string>(),
            _ => login["helpLink"]!.GetValue<string>()
        };
        Assert.That(url, Is.EqualTo($"https://gbl3.darkorbit.com/indexInternal.es?action={action}"));
        Assert.That(LocalAccountLinkResponse.IsKnownAction(new Uri(url).Query[8..]), Is.True);

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await LocalAccountLinkResponse.WriteAsync(context.Response, action, true, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var html = await reader.ReadToEndAsync();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(html, Does.Contain($"id=\"{pageId}\""));
            Assert.That(html, Does.Contain($"<h1>{title}</h1>"));
            Assert.That(html, Does.Contain("/indexInternal.es?action=internalWebGL&amp;from=internalStart"));
            Assert.That(html, Does.Not.Contain("bpsecure.com"));
        });
    }

    [TestCase("internalSupport")]
    [TestCase("internalAccount")]
    [TestCase("internalForum")]
    [TestCase("internalHelp")]
    public async Task AccountLinksRequireAnExistingSession(string action)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await LocalAccountLinkResponse.WriteAsync(context.Response, action, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(body.Length, Is.Zero);
    }

    [Test]
    public void UnknownActionsAreNotHandledAsAccountLinks()
    {
        Assert.That(LocalAccountLinkResponse.IsKnownAction("internalWebGL"), Is.False);
        Assert.That(LocalAccountLinkResponse.IsKnownAction(null), Is.False);
    }
}
