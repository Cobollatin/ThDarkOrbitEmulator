using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-131: the welcome page shown once after internalCompanyConfirm redirects to
// internalWebGL&from=internalCompanyChoose, and the shell's HTML 401.
[TestFixture]
public sealed class LocalCompanyConfirmationPageTests
{
    private static IEnumerable<string> Codes => CompanyCatalog.Companies.Select(company => company.Code);

    [TestCaseSource(nameof(Codes))]
    public void ConfirmationPage_ShowsStoredCompanyFacts(string code)
    {
        var company = CompanyCatalog.Find(code)!;
        var server = LocalMapsXml.Maps.Single(map => map.Id == company.StarterMap).GameServerIp;
        var html = LocalCompanySelection.ConfirmationPage(code);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"id=\"companyConfirmation\" data-company=\"{code}\""));
            Assert.That(html, Does.Contain($"<p id=\"companyName\">{company.DisplayName}</p>"));
            Assert.That(html, Does.Contain($"Welcome to {code.ToUpperInvariant()}."));
            Assert.That(html, Does.Contain("the choice is permanent"));
            Assert.That(html, Does.Contain($"<dt>Code</dt><dd>{code.ToUpperInvariant()}</dd>"));
            Assert.That(html, Does.Contain($"<dt>Faction</dt><dd>{company.NumericFactionId}</dd>"));
            Assert.That(html, Does.Contain(
                $"<dt>Starter map</dt><dd>{company.NumericFactionId}-1 (map {company.StarterMap})</dd>"));
            Assert.That(html, Does.Contain($"<dt>Game server</dt><dd><code>{server}</code></dd>"));
            Assert.That(html, Does.Contain(
                $"id=\"nextMap\" href=\"/indexInternal.es?action=internalMapOverview&amp;map={company.StarterMap}\""));
        });
    }

    [Test]
    public void ConfirmationPage_LinksNextStepsAndContinue()
    {
        var html = LocalCompanySelection.ConfirmationPage("eic");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"nextHangar\" href=\"/indexInternal.es?action=internalDock\""));
            Assert.That(html, Does.Contain("id=\"nextShop\" href=\"/indexInternal.es?action=internalShop\""));
            Assert.That(html, Does.Contain(
                "id=\"nextHelp\" href=\"/indexInternal.es?action=internalHelp&amp;topic=company\""));
            Assert.That(html, Does.Contain(
                "id=\"continueToGame\" href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\""));
            // Raw ampersands in attributes would be invalid HTML.
            Assert.That(html, Does.Not.Contain("&from="));
        });
    }

    [Test]
    public void ConfirmationPage_MarksOnlyTstAsTestCompany()
    {
        Assert.That(LocalCompanySelection.ConfirmationPage("tst"), Does.Contain("id=\"companyTestNotice\""));
        foreach (var code in new[] { "mmo", "eic", "vru" })
            Assert.That(LocalCompanySelection.ConfirmationPage(code), Does.Not.Contain("companyTestNotice"), code);
    }

    [Test]
    public void ConfirmationPage_StarterMapsMatchMapsCatalogue()
    {
        foreach (var company in CompanyCatalog.Companies)
            Assert.That(LocalMapsXml.Maps.Any(map => map.Id == company.StarterMap), Is.True, company.Code);
    }

    [TestCase("?action=internalWebGL&from=internalCompanyChoose")]
    [TestCase("?action=internalWebGL&from=internalStart")]
    [TestCase("?action=internalMenu&from=internalCompanyChoose")]
    [TestCase("?action=internalMenu&from=internalStart")]
    public async Task Shell_WithoutSession_ReturnsHtml401WithSignIn(string query)
    {
        var context = await SendAsync(query, dosid: null);
        var body = Body(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(body, Does.Contain("id=\"sessionExpired\""));
            Assert.That(body, Does.Contain("href=\"/index.es?action=externalHome\""));
            Assert.That(body, Does.Not.Contain("companyConfirmation"));
        });
    }

    [Test]
    public async Task Shell_WithUnknownDosid_ReturnsHtml401()
    {
        var context = await SendAsync("?action=internalWebGL&from=internalCompanyChoose", dosid: "missing-session");

        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(Body(context), Does.Contain("id=\"sessionExpired\""));
    }

    [Test]
    public void AdaptHtmlAndLocationForClient_RewritesInternalWebGlToInternalMenuForUnityClientOnly()
    {
        var unityContext = new DefaultHttpContext();
        unityContext.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        var browserContext = new DefaultHttpContext();
        browserContext.Request.Headers.UserAgent = "Mozilla/5.0";

        var sampleHtml = "<a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Menu</a>";
        var sampleLocation = "/indexInternal.es?action=internalWebGL&from=internalStart";

        Assert.Multiple(() =>
        {
            Assert.That(IndexInternalStartEndpoint.IsUnityClient(unityContext.Request), Is.True);
            Assert.That(IndexInternalStartEndpoint.IsUnityClient(browserContext.Request), Is.False);
            Assert.That(
                IndexInternalStartEndpoint.AdaptHtmlForClient(unityContext.Request, sampleHtml),
                Is.EqualTo("<a href=\"/indexInternal.es?action=internalMenu&amp;from=internalStart\">Menu</a>"));
            Assert.That(
                IndexInternalStartEndpoint.AdaptHtmlForClient(browserContext.Request, sampleHtml),
                Is.EqualTo(sampleHtml));
            Assert.That(
                IndexInternalStartEndpoint.AdaptLocationForClient(unityContext.Request, sampleLocation),
                Is.EqualTo("/indexInternal.es?action=internalMenu&from=internalStart"));
            Assert.That(
                IndexInternalStartEndpoint.AdaptLocationForClient(browserContext.Request, sampleLocation),
                Is.EqualTo(sampleLocation));
        });
    }

    [Test]
    public void EconomyHeader_InjectsSharedThemeWithoutChangingMenuLinks()
    {
        var html = LocalEconomyHeader.Add(LocalCompanySelection.GamePage("mmo"), Array.Empty<EconomyBalance>());
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"localMenuTheme\""));
            Assert.That(html, Does.Contain("background:#060d18"));
            Assert.That(html, Does.Contain("background:#0f172a"));
            Assert.That(html, Does.Contain("max-width:1200px"));
            Assert.That(html, Does.Contain("id=\"mainMenu\""));
            Assert.That(html, Does.Contain("href=\"/indexInternal.es?action=internalShop\""));
        });
    }

    [Test]
    public async Task NativeWebGlHandoff_ReturnsOnlyTheAuthenticatedDosid()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42, "mmo"), null, CancellationToken.None)).Session;
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalWebGL&from=internalStart");
        context.Request.Headers.Cookie = $"dosid={session.Dosid}";
        context.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        context.Response.Body = new MemoryStream();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, sessions, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(Body(context), Is.EqualTo(session.Dosid));
        });
    }

    [Test]
    public async Task NativeInternalStart_RedirectsToTheCapturedHandoffAction()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42, "mmo"), null, CancellationToken.None)).Session;
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalStart&prc=100");
        context.Request.Headers.Cookie = $"dosid={session.Dosid}";
        context.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        context.Response.Body = new MemoryStream();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, sessions, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(302));
        Assert.That(context.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalWebGL&from=internalStart"));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid)
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        // Without a session the shell never reaches the other stores.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, new GameAuthSessionStore(cache), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
