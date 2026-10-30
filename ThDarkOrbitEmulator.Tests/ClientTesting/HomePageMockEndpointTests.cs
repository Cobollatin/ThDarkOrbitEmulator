using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.WwwDarkorbitCom;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.15: GET / home page cookies, headers, body rewriting and payment-proxy dispatch.
public class HomePageMockEndpointTests
{
    private const string LegacyFormAction = "https://sas.bpsecure.com/Sas/Authentication/Bigpoint";

    [Test]
    public async Task HomePage_WithoutDosid_IssuesFreshSessionCookieThenFixedAffiliateCookies()
    {
        var context = await InvokeAsync(NewContext("localhost", 60952));
        var cookies = context.Response.Headers.SetCookie.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(cookies, Has.Length.EqualTo(5));
            Assert.That(cookies[0], Does.Match("^dosid=[0-9a-f]{32}; path=/; samesite=none; secure; HttpOnly$"));
            foreach (var (cookie, name) in cookies.Skip(1).Take(3).Zip(new[] { "aid", "aip", "aig" }))
            {
                Assert.That(cookie, Is.EqualTo($"{name}=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure"));
            }
            Assert.That(cookies[4], Is.EqualTo("acr=22; expires=Wed, 15-Apr-2026 00:12:29 GMT; Max-Age=2592000; path=/; samesite=none; secure"));
        });
    }

    [Test]
    public async Task HomePage_ReusesNonBlankDosidAndReplacesBlankOrRepeatedRequestsWithFreshIds()
    {
        var reused = await InvokeAsync(NewContext("localhost", 60952, cookie: "dosid=existing-session-id"));
        var blank = await InvokeAsync(NewContext("localhost", 60952, cookie: "dosid="));
        var first = await InvokeAsync(NewContext("localhost", 60952));
        var second = await InvokeAsync(NewContext("localhost", 60952));
        Assert.Multiple(() =>
        {
            Assert.That(reused.Response.Headers.SetCookie[0], Is.EqualTo("dosid=existing-session-id; path=/; samesite=none; secure; HttpOnly"));
            Assert.That(Dosid(blank), Does.Match("^[0-9a-f]{32}$"));
            Assert.That(Dosid(first), Is.Not.EqualTo(Dosid(second)));
        });
    }

    [Test]
    public async Task HomePage_SendsNoStoreLanguageTransportAndFrameAncestorHeaders()
    {
        var context = await InvokeAsync(NewContext("localhost", 60952));
        var headers = context.Response.Headers;
        var csp = headers.ContentSecurityPolicy.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(headers.CacheControl.ToString(), Is.EqualTo("no-store, no-cache, must-revalidate"));
            Assert.That(headers.Pragma.ToString(), Is.EqualTo("no-cache"));
            Assert.That(headers.Expires.ToString(), Is.EqualTo("Thu, 19 Nov 1981 08:52:00 GMT"));
            Assert.That(headers.ContentLanguage.ToString(), Is.EqualTo("es"));
            Assert.That(headers.StrictTransportSecurity.ToString(), Is.EqualTo("max-age=31536000; includeSubDomains"));
            Assert.That(csp, Does.StartWith("frame-ancestors https://*.darkorbit.com/ "));
            Assert.That(csp, Does.EndWith(";"));
            Assert.That(Regex.Matches(csp, "frame-ancestors").Count, Is.EqualTo(1));
            foreach (var name in new[] { "Cache-Control", "Pragma", "Expires", "Content-Security-Policy", "Content-Language", "Strict-Transport-Security" })
            {
                Assert.That(headers[name].Count, Is.EqualTo(1), name);
            }
        });
    }

    [Test]
    public async Task HomePage_LocalBodyIsRewrittenAndReplayBodyMatchesPublicHost()
    {
        var local = Body(await InvokeAsync(NewContext("localhost", 60952)));
        var gateway = NewContext("api-game", null);
        gateway.Request.Headers["X-Forwarded-Host"] = "localhost:60952";
        var forwarded = Body(await InvokeAsync(gateway));
        var replayContext = NewContext("localhost", 60952);
        replayContext.Request.Headers["X-Original-Host"] = "www.darkorbit.com";
        var replay = Body(await InvokeAsync(replayContext));
        var publicHost = Body(await InvokeAsync(NewContext("www.darkorbit.com", null)));
        Assert.Multiple(() =>
        {
            Assert.That(local, Is.Not.Empty);
            Assert.That(forwarded, Is.EqualTo(local));
            Assert.That(replay, Is.EqualTo(publicHost));
            Assert.That(local, Does.Not.Contain(LegacyFormAction));
            Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(NewContext("localhost", 60952).Request, local), Is.EqualTo(local));
        });
        if (publicHost.Contains(LegacyFormAction, StringComparison.OrdinalIgnoreCase))
        {
            // Capture present: only emulator hosts receive the placeholder rewrite.
            Assert.That(local, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(local, Does.Contain("action=\"/Sas/Authentication/Bigpoint"));
        }
        else
        {
            Assert.That(local, Is.EqualTo(publicHost));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \t\r\n")]
    public void HomePage_MissingCaptureUsesGenericSelfContainedFallback(string? capturedHtml)
    {
        var local = HomeEndpoint.GetBody(NewContext("localhost", 60952).Request, capturedHtml);
        var publicHost = HomeEndpoint.GetBody(NewContext("www.darkorbit.com", null).Request, capturedHtml);
        Assert.Multiple(() =>
        {
            Assert.That(local, Is.EqualTo(publicHost));
            Assert.That(local, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(local, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(local, Does.Not.Contain("Bigpoint").IgnoreCase);
            Assert.That(local, Does.Not.Contain("http://").And.Not.Contain("https://"));
            Assert.That(local, Does.Contain("action=\"/Sas/Authentication/Local\""));
            Assert.That(local, Does.Contain("name=\"username\"").And.Contain("name=\"password\""));
        });
    }

    [Test]
    public void HomePage_CapturedContentRewritesOnlyForLocalRequests()
    {
        const string captured = "<title>DarkOrbit Reloaded | MMO de acción espacial</title>" +
            "<form action=\"https://sas.bpsecure.com/Sas/Authentication/Bigpoint?next=1\"></form>";
        var local = HomeEndpoint.GetBody(NewContext("localhost", 60952).Request, captured);
        var replay = NewContext("localhost", 60952);
        replay.Request.Headers["X-Original-Host"] = "www.darkorbit.com";
        Assert.Multiple(() =>
        {
            Assert.That(local, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(local, Does.Contain("action=\"/Sas/Authentication/Bigpoint?next=1\""));
            Assert.That(HomeEndpoint.GetBody(replay.Request, captured), Is.EqualTo(captured));
            Assert.That(HomeEndpoint.GetBody(NewContext("www.darkorbit.com", null).Request, captured), Is.EqualTo(captured));
        });
    }

    [Test]
    public async Task HomePage_IgnoresQueryAndNonDosidCookiesForBody()
    {
        var plain = await InvokeAsync(NewContext("localhost", 60952));
        var noisy = NewContext("localhost", 60952, cookie: "aid=1; acr=99");
        noisy.Request.QueryString = new QueryString("?lang=en&loginError=94&action=internalPaymentProxy");
        await InvokeAsync(noisy);
        Assert.Multiple(() =>
        {
            Assert.That(noisy.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Body(noisy), Is.EqualTo(Body(plain)));
            Assert.That(noisy.Response.Headers.SetCookie.Count, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task PaymentProxyOnGameHost_WithoutValidSession_IsUnauthorizedWithoutHomeHeaders()
    {
        foreach (var cookie in new string?[] { null, "dosid=", "dosid=unknown-session" })
        {
            var direct = NewContext("gbl3.darkorbit.com", null, cookie);
            direct.Request.QueryString = new QueryString("?action=internalPaymentProxy");
            await InvokeAsync(direct);
            var replay = NewContext("localhost", 60952, cookie);
            replay.Request.Headers["X-Original-Host"] = "gbl6.darkorbit.com";
            replay.Request.QueryString = new QueryString("?action=INTERNALPAYMENTPROXY");
            await InvokeAsync(replay);
            foreach (var context in new[] { direct, replay })
            {
                Assert.Multiple(() =>
                {
                    Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized), cookie);
                    Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero, cookie);
                    Assert.That(context.Response.Headers.ContentSecurityPolicy.ToString(), Is.Empty, cookie);
                    Assert.That(((MemoryStream)context.Response.Body).Length, Is.Zero, cookie);
                });
            }
        }
    }

    [Test]
    public async Task PaymentProxyOnGameHost_WithSessionButNoCompany_RedirectsToCompanyChoice()
    {
        var cache = NewCache();
        var sessions = new GameAuthSessionStore(cache);
        var created = await sessions.CreateSessionAsync(new AccountSummary(
            Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);
        var context = NewContext("gbl3.darkorbit.com", null, $"dosid={created.Session.Dosid}");
        context.Request.QueryString = new QueryString("?action=internalPaymentProxy");
        await InvokeAsync(context, cache, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
            Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain("aid=deleted"));
        });
    }

    [Test]
    public void Gateway_HomeRouteAcceptsAnyMethodAnonymouslyWhileUnityConfigOwnsRootPost()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var routes = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");
        var home = routes.GetProperty("home-page");
        var rootRoutes = routes.EnumerateObject()
            .Where(route => route.Value.GetProperty("Match").GetProperty("Path").GetString() == "/")
            .Select(route => route.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(home.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
            Assert.That(home.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
            Assert.That(home.GetProperty("Match").GetProperty("Methods").GetArrayLength(), Is.Zero);
            Assert.That(rootRoutes, Is.EqualTo(new[] { "home-page", "unity-config" }));
        });
    }

    private static string Dosid(HttpContext context)
        => Regex.Match(context.Response.Headers.SetCookie[0]!, "^dosid=([^;]*);").Groups[1].Value;

    private static string Body(HttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static MemoryDistributedCache NewCache()
        => new(Options.Create(new MemoryDistributedCacheOptions()));

    private static DefaultHttpContext NewContext(string host, int? port, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/";
        context.Request.Host = port is null ? new HostString(host) : new HostString(host, port.Value);
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<HttpContext> InvokeAsync(DefaultHttpContext context, IDistributedCache? cache = null, IGameAuthSessionStore? sessions = null)
    {
        cache ??= NewCache();
        var economy = new EconomyStore(new ConfigurationBuilder().Build());
        var endpoint = Factory.Create<HomeEndpoint>(context, NullLogger<HomeEndpoint>.Instance,
            sessions ?? new GameAuthSessionStore(cache), new LocalHangarEquipmentStore(cache, economy), economy);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
