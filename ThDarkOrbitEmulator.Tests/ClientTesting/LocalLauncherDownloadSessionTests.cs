using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexEs;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Drives /index.es?action=externalDownload through IndexEsEndpoint with a real session store.
public class LocalLauncherDownloadSessionTests
{
    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session);

    [Test]
    public async Task WithoutACookieTheSignedOutPageIsServedWithoutMintingADosid()
    {
        var harness = await HarnessAsync(company: null);
        var context = await SendAsync("?action=externalDownload", dosid: null, harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Body(context), Is.EqualTo(LocalLauncherDownload.Page));
            Assert.That(Body(context), Does.Contain("data-state=\"signed-out\""));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [TestCase("unknown-dosid")]
    [TestCase("   ")]
    public async Task UnknownOrBlankDosidReadsAsSignedOut(string dosid)
    {
        var harness = await HarnessAsync(company: null);
        var context = await SendAsync("?action=externalDownload", dosid, harness);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(Body(context), Is.EqualTo(LocalLauncherDownload.Page));
        Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
    }

    [Test]
    public async Task SessionWithoutCompanyLinksToCompanyChoice()
    {
        var harness = await HarnessAsync(company: null);
        var body = Body(await SendAsync("?action=externalDownload", harness.Session.Dosid, harness));

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("data-state=\"no-company\""));
            Assert.That(body, Does.Contain("Signed in as <strong>pilot</strong> (game ID 7)"));
            Assert.That(body, Does.Contain("href=\"/indexInternal.es?action=internalCompanyChoose\""));
            Assert.That(body, Does.Contain(harness.Session.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC"));
            Assert.That(body, Does.Not.Contain("data-state=\"signed-out\""));
        });
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    public async Task SessionWithCompanyShowsCompanyAndOpensTheLocalMenu(string company)
    {
        var harness = await HarnessAsync(company);
        var body = Body(await SendAsync("?action=externalDownload", harness.Session.Dosid, harness));
        var name = ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts
            .LocalCompanySelection.DisplayName(company);

        Assert.Multiple(() =>
        {
            Assert.That(name, Is.Not.Null);
            Assert.That(body, Does.Contain("data-state=\"ready\""));
            Assert.That(body, Does.Contain($"company <strong>{name}</strong>"));
            Assert.That(body, Does.Contain("<aside id=\"launcherSession\" data-state=\"ready\"><h2>Your local sign-in</h2>"));
            Assert.That(body, Does.Contain("action=internalWebGL&amp;from=internalStart\">Open local menu</a></p></aside>"));
        });
    }

    [TestCase("requirements")]
    [TestCase("connection")]
    [TestCase("troubleshooting")]
    public async Task EverySectionCarriesTheSignInCard(string section)
    {
        var harness = await HarnessAsync("mmo");
        var context = await SendAsync($"?action=externalDownload&section={section}", harness.Session.Dosid, harness);
        var body = Body(context);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(body, Does.Contain($"data-section=\"{section}\""));
        Assert.That(body, Does.Contain("data-state=\"ready\""));
        Assert.That(Body(await SendAsync($"?action=externalDownload&section={section}", null, harness)),
            Does.Contain("data-state=\"signed-out\""));
    }

    [Test]
    public async Task PilotNameIsHtmlEncoded()
    {
        var harness = await HarnessAsync("mmo", username: "<b>x</b>&\"");
        var body = Body(await SendAsync("?action=externalDownload", harness.Session.Dosid, harness));

        Assert.That(body, Does.Not.Contain("<b>x</b>"));
        Assert.That(body, Does.Contain("&lt;b&gt;x&lt;/b&gt;&amp;&quot;"));
    }

    [Test]
    public async Task UnknownStoredCompanyCountsAsNoCompany()
    {
        var harness = await HarnessAsync(company: null);
        harness.Session.Company = "zzz";
        var result = LocalLauncherDownload.Resolve(Query("?action=externalDownload"), harness.Session);

        Assert.That(result.Html, Does.Contain("data-state=\"no-company\""));
        Assert.That(result.Html, Does.Not.Contain("zzz"));
    }

    [Test]
    public async Task InvalidatedSessionReadsAsSignedOut()
    {
        var harness = await HarnessAsync("mmo");
        await harness.Sessions.InvalidateAsync(harness.Session.Dosid, CancellationToken.None);

        var body = Body(await SendAsync("?action=externalDownload", harness.Session.Dosid, harness));
        Assert.That(body, Is.EqualTo(LocalLauncherDownload.Page));
    }

    [TestCase("?ACTION=externalDownload", StatusCodes.Status400BadRequest)]
    [TestCase("?action=externalDownload&installer=1", StatusCodes.Status400BadRequest)]
    [TestCase("?action=externalDownload&section=install", StatusCodes.Status404NotFound)]
    public async Task ErrorsWinOverTheSessionAndNeverShowTheSignInCard(string query, int status)
    {
        var harness = await HarnessAsync("mmo");
        var context = await SendAsync(query, harness.Session.Dosid, harness);
        var body = Body(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status));
            Assert.That(body, Does.Contain("id=\"localLauncherDownloadError\""));
            Assert.That(body, Does.Not.Contain("launcherSession"));
            Assert.That(body, Does.Not.Contain("pilot"));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [Test]
    public async Task ErrorsDoNotReadTheSessionStore()
    {
        var store = new CountingStore();
        var result = await LocalLauncherDownload.ResolveAsync(Query("?action=externalDownload&x=1"), "dosid", store,
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(store.Reads, Is.Zero);
    }

    [TestCase(128, 1)]
    [TestCase(129, 0)]
    [TestCase(4096, 0)]
    public async Task DosidLengthBoundsSessionLookup(int length, int expectedReads)
    {
        var store = new CountingStore();
        var result = await LocalLauncherDownload.ResolveAsync(Query("?action=externalDownload"),
            new string('a', length), store, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(result.Html, Is.EqualTo(LocalLauncherDownload.Page));
            Assert.That(store.Reads, Is.EqualTo(expectedReads));
        });
    }

    [Test]
    public async Task OversizedDosidCookieServesSignedOutPageWithoutChangingTheCookie()
    {
        var harness = await HarnessAsync("mmo");
        var context = await SendAsync("?action=externalDownload", new string('a', 129), harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Body(context), Is.EqualTo(LocalLauncherDownload.Page));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    private sealed class CountingStore : IGameAuthSessionStore
    {
        public int Reads { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<GameAuthSession?>(null);
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static IQueryCollection Query(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return context.Request.Query;
    }

    private static async Task<Harness> HarnessAsync(string? company, string username = "pilot")
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), username, null, 7), null, CancellationToken.None)).Session;
        if (company is not null)
        {
            Assert.That(await sessions.SelectCompanyAsync(session.Dosid, company, CancellationToken.None), Is.True);
            session = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!;
        }
        return new Harness(sessions, session);
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid, Harness harness)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/index.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        var endpoint = Factory.Create<IndexEsEndpoint>(context, NullLogger<IndexEsEndpoint>.Instance, harness.Sessions);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
