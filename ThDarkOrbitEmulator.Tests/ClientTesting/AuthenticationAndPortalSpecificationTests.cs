using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexEs;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.ProjectApi.Authentication;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 (docs/specs/01-authentication-and-portal.md) executable contract.
public class AuthenticationAndPortalSpecificationTests
{
    private const string LiveLocation = "https://www.darkorbit.com/ProjectApi/Authentication?authUser=291&token=t";
    private const string LocalLocation = "/ProjectApi/Authentication?authUser=291&token=t";

    private const string CapturedLoginHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>DarkOrbit Reloaded | MMO de acción espacial</title>
        <script src="//assets.bpsecure.com/bpid/bpid.js"></script></head>
        <body>
        <img src="https://darkorbit-22.bpsecure.com/do_img/global/externalHome/assets/logo_rework_shadow_420x168.png?__cv=abc" class="eh_mc_logo_center">
        <form name="bgcdw_login_form" method="post" action="https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=22&amp;token=abc">
            <input type="text" name="username" />
            <input type="password" name="password" />
        </form>
        <script src="https://js.hcaptcha.com/1/api.js?hl=es" async defer></script>
        <script src="https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js" defer></script>
        <footer>&copy; Bigpoint <span id="gl_footer_element_logo" class="gl_footer_element"><a href="https://www.bigpoint.net/"><img class="gl_footer_element_image" src="https://pit-835.bpsecure.com/published/globalfooter/bigpoint_logo_light.png" alt=""/></a></span></footer>
        </body>
        </html>
        """;

    private static HttpRequest Request(string host, string scheme = "http", params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context.Request;
    }

    private static HttpRequest GatewayRequest(params (string Name, string Value)[] extra)
        => Request("api-game", "http",
            [("X-Forwarded-Host", "localhost:60952"), ("X-Forwarded-Proto", "https"), .. extra]);

    // ---- 1. Host classification (EmulatorRedirectHelper.IsEmulatorHost) ----

    [TestCase("localhost:60952")]
    [TestCase("127.0.0.1:60952")]
    [TestCase("[::1]:60952")]
    [TestCase("LOCALHOST")]
    [TestCase("api-game")]
    [TestCase("API-GAME:8080")]
    public void IsEmulatorHost_TrueForLoopbackAndGatewayHosts(string host)
        => Assert.That(EmulatorRedirectHelper.IsEmulatorHost(Request(host)), Is.True);

    [TestCase("www.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com")]
    [TestCase("sas.bpsecure.com")]
    [TestCase("localhost.evil.example")]
    [TestCase("10.0.0.5:60952")]
    public void IsEmulatorHost_FalseForPublicOrUnknownHosts(string host)
        => Assert.That(EmulatorRedirectHelper.IsEmulatorHost(Request(host)), Is.False);

    [TestCase("localhost:60952")]
    [TestCase("127.0.0.1:60952")]
    [TestCase("[::1]:60952")]
    [TestCase("localhost:60952, proxy.internal")]
    public void IsEmulatorHost_TrustsLoopbackForwardedHostFromAnyHost(string forwardedHost)
        => Assert.That(EmulatorRedirectHelper.IsEmulatorHost(
            Request("www.darkorbit.com", "https", ("X-Forwarded-Host", forwardedHost))), Is.True);

    [TestCase("www.darkorbit.com")]
    [TestCase("proxy.internal, localhost:60952")]
    [TestCase("")]
    public void IsEmulatorHost_IgnoresNonLoopbackFirstForwardedHost(string forwardedHost)
        => Assert.That(EmulatorRedirectHelper.IsEmulatorHost(
            Request("www.darkorbit.com", "https", ("X-Forwarded-Host", forwardedHost))), Is.False);

    [TestCase(" , localhost:60952")]
    [TestCase(",localhost:60952")]
    [TestCase("   ")]
    public void IsEmulatorHost_BlankFirstForwardedValueIsNotTrusted(string forwardedHost)
        => Assert.That(EmulatorRedirectHelper.IsEmulatorHost(
            Request("www.darkorbit.com", "https", ("X-Forwarded-Host", forwardedHost))), Is.False);

    [TestCase(new[] { "localhost:60952", "attacker.example" }, true)]
    [TestCase(new[] { "attacker.example", "localhost:60952" }, false)]
    public void IsEmulatorHost_MultiValuedForwardedHostUsesFirstEntry(string[] values, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("www.darkorbit.com");
        context.Request.Headers["X-Forwarded-Host"] = new Microsoft.Extensions.Primitives.StringValues(values);

        Assert.That(EmulatorRedirectHelper.IsEmulatorHost(context.Request), Is.EqualTo(expected));
    }

    [TestCase("api-game")]
    [TestCase("API-GAME:8080")]
    public void GatewayForwardedHost_IsEmulatorButClientFacingHostEchoesRequestHost(string forwardedHost)
    {
        var request = Request("www.darkorbit.com", "https", ("X-Forwarded-Host", forwardedHost), ("X-Forwarded-Proto", "http"));
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorRedirectHelper.IsEmulatorHost(request), Is.True);
            Assert.That(EmulatorRedirectHelper.ToRedirectLocation(request, LiveLocation, LocalLocation), Is.EqualTo(LocalLocation));
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("https://www.darkorbit.com"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("www.darkorbit.com"));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo("https"));
        });
    }

    // ---- 2. Redirect locations (EmulatorRedirectHelper.ToRedirectLocation) ----

    [TestCase("localhost:60952")]
    [TestCase("127.0.0.1:60952")]
    [TestCase("[::1]:60952")]
    [TestCase("api-game")]
    public void ToRedirectLocation_EmulatorHostsReceiveRelativePath(string host)
        => Assert.That(EmulatorRedirectHelper.ToRedirectLocation(Request(host), LiveLocation, LocalLocation),
            Is.EqualTo(LocalLocation));

    [Test]
    public void ToRedirectLocation_GatewayForwardedRequestReceivesRelativePath()
        => Assert.That(EmulatorRedirectHelper.ToRedirectLocation(GatewayRequest(), LiveLocation, LocalLocation),
            Is.EqualTo(LocalLocation));

    [TestCase("www.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com")]
    public void ToRedirectLocation_ReplayWithOriginalHostKeepsProductionUrl(string originalHost)
    {
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorRedirectHelper.ToRedirectLocation(
                Request("localhost:60952", "https", ("X-Original-Host", originalHost)), LiveLocation, LocalLocation),
                Is.EqualTo(LiveLocation));
            Assert.That(EmulatorRedirectHelper.ToRedirectLocation(
                GatewayRequest(("X-Original-Host", originalHost)), LiveLocation, LocalLocation),
                Is.EqualTo(LiveLocation));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("attacker.example")]
    public void ToRedirectLocation_AnyOriginalHostHeaderPresenceKeepsProductionUrl(string originalHost)
        => Assert.That(EmulatorRedirectHelper.ToRedirectLocation(
            Request("localhost:60952", "https", ("X-Original-Host", originalHost)), LiveLocation, LocalLocation),
            Is.EqualTo(LiveLocation));

    [Test]
    public void ToRedirectLocation_PublicHostAlwaysKeepsProductionUrl()
        => Assert.That(EmulatorRedirectHelper.ToRedirectLocation(Request("www.darkorbit.com", "https"), LiveLocation, LocalLocation),
            Is.EqualTo(LiveLocation));

    [Test]
    public void ToRedirectLocation_LoginFailureFollowsSameHostRules()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorRedirectHelper.ToRedirectLocation(Request("localhost:60952"),
                "https://www.darkorbit.com/?loginError=94", "/?loginError=94"), Is.EqualTo("/?loginError=94"));
            Assert.That(EmulatorRedirectHelper.ToRedirectLocation(Request("www.darkorbit.com"),
                "https://www.darkorbit.com/?loginError=94", "/?loginError=94"), Is.EqualTo("https://www.darkorbit.com/?loginError=94"));
        });
    }

    // ---- 3. Client-facing host (ClientFacingGameHost) ----

    [TestCase("localhost:60952", "http", "http://localhost:60952")]
    [TestCase("127.0.0.1:60952", "https", "https://127.0.0.1:60952")]
    [TestCase("[::1]:60952", "https", "https://[::1]:60952")]
    [TestCase("api-game", "http", "http://api-game")]
    public void Resolve_DirectEmulatorHostUsesRequestSchemeAndHost(string host, string scheme, string expected)
        => Assert.That(ClientFacingGameHost.Resolve(Request(host, scheme)), Is.EqualTo(expected));

    [Test]
    public void Resolve_GatewayRequestUsesForwardedProtoAndHost()
    {
        var request = GatewayRequest();
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("https://localhost:60952"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("localhost"));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo("https"));
            Assert.That(ClientFacingGameHost.IsRedirectedGameHost(request), Is.False);
        });
    }

    [TestCase("HTTPS", "https")]
    [TestCase("http", "http")]
    [TestCase("https, http", "https")]
    [TestCase("ftp", "http")]
    [TestCase("", "http")]
    public void Resolve_ForwardedProtoIsNormalisedOrFallsBackToRequestScheme(string proto, string expectedScheme)
    {
        var request = Request("api-game", "http", ("X-Forwarded-Host", "localhost:60952"), ("X-Forwarded-Proto", proto));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo($"{expectedScheme}://localhost:60952"));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo(expectedScheme));
        });
    }

    [Test]
    public void Resolve_LoopbackForwardedHostIsEchoedVerbatim()
    {
        var request = Request("api-game", "http", ("X-Forwarded-Host", " LOCALHOST:60952 , proxy.internal"), ("X-Forwarded-Proto", "https"));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("https://LOCALHOST:60952"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("LOCALHOST"));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo("https"));
        });
    }

    [Test]
    public void Resolve_PublicHostFallsBackToProductionGameHost()
        => Assert.That(ClientFacingGameHost.Resolve(Request("www.darkorbit.com", "https")), Is.EqualTo("https://gbl3.darkorbit.com"));

    [TestCase("gbl3.darkorbit.com")]
    [TestCase("GBL12.darkorbit.com")]
    public void Resolve_TrustedGameOriginalHostOnEmulatorReplaysGameHost(string originalHost)
    {
        var request = Request("localhost:60952", "http", ("X-Original-Host", originalHost));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.IsRedirectedGameHost(request), Is.True);
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo($"https://{originalHost}"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo(originalHost));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo("https"));
        });
    }

    [TestCase("www.darkorbit.com")]
    [TestCase("gbl0.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com.evil.example")]
    [TestCase("evil.example/gbl3.darkorbit.com")]
    [TestCase("attacker.example")]
    [TestCase("gbl01.darkorbit.com")]
    [TestCase("gbl.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com:443")]
    [TestCase(" gbl3.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com\n")]
    [TestCase("gbl3xdarkorbit.com")]
    public void Resolve_UntrustedOriginalHostIsNeverEchoed(string spoofedHost)
    {
        var request = GatewayRequest(("X-Original-Host", spoofedHost));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.IsRedirectedGameHost(request), Is.False);
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("https://localhost:60952"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("localhost"));
        });
    }

    [Test]
    public void Resolve_OriginalHostOnPublicHostIsNotTrusted()
    {
        var request = Request("attacker.example", "https", ("X-Original-Host", "gbl3.darkorbit.com"));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.IsRedirectedGameHost(request), Is.False);
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("https://gbl3.darkorbit.com"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("attacker.example"));
        });
    }

    [Test]
    public void Resolve_NonLoopbackForwardedHostIsNotEchoed()
    {
        var request = Request("localhost:60952", "http", ("X-Forwarded-Host", "attacker.example"), ("X-Forwarded-Proto", "https"));
        Assert.Multiple(() =>
        {
            Assert.That(ClientFacingGameHost.Resolve(request), Is.EqualTo("http://localhost:60952"));
            Assert.That(ClientFacingGameHost.ResolveHostName(request), Is.EqualTo("localhost"));
            Assert.That(ClientFacingGameHost.ResolveScheme(request), Is.EqualTo("http"));
        });
    }

    // ---- 4. Login page rewriting (EmulatorRedirectHelper.RewriteLoginPageHtml) ----

    private static IEnumerable<TestCaseData> EmulatorRequests()
    {
        yield return new TestCaseData("localhost:60952", false).SetName("Rewrite_localhost");
        yield return new TestCaseData("127.0.0.1:60952", false).SetName("Rewrite_ipv4Loopback");
        yield return new TestCaseData("[::1]:60952", false).SetName("Rewrite_ipv6Loopback");
        yield return new TestCaseData("api-game", true).SetName("Rewrite_gatewayForwarded");
    }

    [TestCaseSource(nameof(EmulatorRequests))]
    public void RewriteLoginPageHtml_EmulatorHostsGetLocalFormScriptsAndPlaceholderBranding(string host, bool viaGateway)
    {
        var request = viaGateway ? GatewayRequest() : Request(host);
        var html = EmulatorRedirectHelper.RewriteLoginPageHtml(request, CapturedLoginHtml);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("action=\"/Sas/Authentication/Bigpoint?authUser=22&amp;token=abc\""));
            Assert.That(html, Does.Not.Contain("sas.bpsecure.com"));
            Assert.That(html, Does.Contain("src=\"/1/api.js?hl=es\""));
            Assert.That(html, Does.Not.Contain("js.hcaptcha.com"));
            Assert.That(html, Does.Contain("src=\"/bgc/js/bgc-2.0.0.min.js\""));
            Assert.That(html, Does.Not.Contain("sharedservices.bpsecure.com"));
            Assert.That(html, Does.Contain("src=\"/bpid/bpid.js\""));
            Assert.That(html, Does.Not.Contain("assets.bpsecure.com"));
            Assert.That(html, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(html, Does.Not.Contain("DarkOrbit Reloaded"));
            Assert.That(html, Does.Not.Contain("&copy; Bigpoint"));
            Assert.That(html, Does.Contain("<footer>Space Emulator Placeholder </footer>"));
            Assert.That(html, Does.Not.Contain("gl_footer_element_logo").And.Not.Contain("bigpoint_logo_light.png"));
            Assert.That(html, Does.Contain($"<img src=\"{EmulatorRedirectHelper.PlaceholderLogoDataUri}\" alt=\"Space Emulator Placeholder Logo\" class=\"eh_mc_logo_center\">"));
            Assert.That(html, Does.Not.Contain("logo_rework_shadow_420x168.png"));
            Assert.That(html, Does.Contain("name=\"username\"").And.Contain("name=\"password\""));
        });
    }

    [Test]
    public void PlaceholderLogo_IsSelfContainedGenericSvg()
    {
        var logo = EmulatorRedirectHelper.PlaceholderLogoDataUri;
        Assert.Multiple(() =>
        {
            Assert.That(logo, Does.StartWith("data:image/svg+xml;utf8,<svg"));
            Assert.That(logo, Does.Contain("[ SPACE EMULATOR ]"));
            Assert.That(logo, Does.Contain("LOCAL SERVER PORTAL (PLACEHOLDER)"));
            Assert.That(logo.Replace("http://www.w3.org/2000/svg", string.Empty), Does.Not.Contain("http").And.Not.Contain("//"), "Logo must not reference external assets.");
            Assert.That(logo, Does.Not.Contain("\""), "Data URI must be embeddable inside a double-quoted src attribute.");
            Assert.That(logo.ToLowerInvariant(), Does.Not.Contain("darkorbit").And.Not.Contain("bigpoint"));
        });
    }

    [Test]
    public void RewriteLoginPageHtml_IsCaseInsensitiveForUpstreamUrls()
    {
        var html = EmulatorRedirectHelper.RewriteLoginPageHtml(Request("localhost:60952"),
            "<form action=\"HTTPS://SAS.BPSECURE.COM/Sas/Authentication/Bigpoint?authUser=22\"></form>");
        Assert.That(html, Is.EqualTo("<form action=\"/Sas/Authentication/Bigpoint?authUser=22\"></form>"));
    }

    [Test]
    public void RewriteLoginPageHtml_IsIdempotent()
    {
        var request = GatewayRequest();
        var once = EmulatorRedirectHelper.RewriteLoginPageHtml(request, CapturedLoginHtml);
        Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(request, once), Is.EqualTo(once));
    }

    [Test]
    public void RewriteLoginPageHtml_LeavesUnknownLogoMarkupUntouched()
    {
        const string html = "<img src=\"https://cdn.example/other.png\" class=\"eh_mc_logo_center\">";
        Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(Request("localhost"), html), Is.EqualTo(html));
    }

    [TestCase("www.darkorbit.com")]
    [TestCase("gbl3.darkorbit.com")]
    public void RewriteLoginPageHtml_ReplayWithOriginalHostReturnsCapturedHtmlVerbatim(string originalHost)
    {
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(
                Request("localhost:60952", "https", ("X-Original-Host", originalHost)), CapturedLoginHtml), Is.EqualTo(CapturedLoginHtml));
            Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(
                GatewayRequest(("X-Original-Host", originalHost)), CapturedLoginHtml), Is.EqualTo(CapturedLoginHtml));
        });
    }

    [Test]
    public void RewriteLoginPageHtml_PublicHostReturnsCapturedHtmlVerbatim()
        => Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(Request("www.darkorbit.com", "https"), CapturedLoginHtml),
            Is.EqualTo(CapturedLoginHtml));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void RewriteLoginPageHtml_BlankInputIsReturnedUnchanged(string? html)
        => Assert.That(EmulatorRedirectHelper.RewriteLoginPageHtml(Request("localhost"), html!), Is.EqualTo(html));

    // ---- 5. Session cookie issuance (GET /index.es, GET /ProjectApi/Authentication) ----

    private static DefaultHttpContext EndpointContext(string host, string path, string query = "", string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null)
        {
            context.Request.Headers.Cookie = $"dosid={dosid}";
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string[] SetCookies(HttpContext context) => context.Response.Headers.SetCookie.ToArray()!;

    private static GameAuthSessionStore NewSessionStore()
        => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    [Test]
    public async Task IndexEs_IssuesFreshHttpOnlyDosidAndClearsTrackingCookies()
    {
        var context = EndpointContext("localhost:60952", "/index.es");
        var endpoint = Factory.Create<IndexEsEndpoint>(context, NullLogger<IndexEsEndpoint>.Instance, NewSessionStore());

        await endpoint.HandleAsync(CancellationToken.None);

        var cookies = SetCookies(context);
        var dosidCookie = cookies.Single(cookie => cookie.StartsWith("dosid=", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(dosidCookie, Does.Match("^dosid=[0-9a-f]{32}; path=/; samesite=none; secure; HttpOnly$"));
            foreach (var cleared in new[] { "aid", "aip", "aig", "acr" })
            {
                Assert.That(cookies, Has.One.StartsWith($"{cleared}=deleted;").And.Contain("Max-Age=0"));
            }

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store, no-cache, must-revalidate"));
            Assert.That(context.Response.Headers.ContentSecurityPolicy.ToString(), Is.EqualTo("frame-ancestors https://*.darkorbit.com/;"));
        });
    }

    [Test]
    public async Task IndexEs_ReusesExistingDosid()
    {
        var context = EndpointContext("api-game", "/index.es", dosid: "existingdosid123");
        context.Request.Headers["X-Forwarded-Host"] = "localhost:60952";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var endpoint = Factory.Create<IndexEsEndpoint>(context, NullLogger<IndexEsEndpoint>.Instance, NewSessionStore());

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.That(SetCookies(context), Has.One.EqualTo("dosid=existingdosid123; path=/; samesite=none; secure; HttpOnly"));
    }

    [Test]
    public async Task ProjectApi_InvalidTokenRedirectsToExternalLogoutWithoutCookies()
    {
        var context = EndpointContext("localhost:60952", "/ProjectApi/Authentication", "?authUser=291&token=bogus");
        var endpoint = Factory.Create<ProjectApiAuthenticationEndpoint>(context,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, NewSessionStore());

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=externalLogout"));
            Assert.That(SetCookies(context), Is.Empty);
        });
    }

    [Test]
    public async Task ProjectApi_TwoHopHandshakeIssuesSessionCookiesAndLandsOnInternalStart()
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42, "MMO"), "kc-token", CancellationToken.None);

        var first = EndpointContext("api-game", "/ProjectApi/Authentication",
            $"?authUser=291&token={Uri.EscapeDataString(created.RedirectToken)}");
        first.Request.Headers["X-Forwarded-Host"] = "localhost:60952";
        first.Request.Headers["X-Forwarded-Proto"] = "https";
        await Factory.Create<ProjectApiAuthenticationEndpoint>(first,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);

        var firstLocation = first.Response.Headers.Location.ToString();
        var firstCookies = SetCookies(first);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(firstLocation, Does.StartWith("/ProjectApi/Authentication?authUser=22&token="));
            Assert.That(firstLocation, Does.Not.Contain(Uri.EscapeDataString(created.RedirectToken)), "Redirect token must rotate.");
            Assert.That(firstCookies, Has.One.EqualTo($"dosid={created.Session.Dosid}; path=/; samesite=none; secure; HttpOnly"));
            Assert.That(firstCookies, Has.One.StartsWith("acr=22;"));
        });

        var replay = EndpointContext("localhost:60952", "/ProjectApi/Authentication",
            $"?authUser=291&token={Uri.EscapeDataString(created.RedirectToken)}");
        await Factory.Create<ProjectApiAuthenticationEndpoint>(replay,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);
        Assert.That(replay.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=externalLogout"),
            "A consumed redirect token must not be replayable.");

        var second = EndpointContext("localhost:60952", "/ProjectApi/Authentication", firstLocation[firstLocation.IndexOf('?')..]);
        await Factory.Create<ProjectApiAuthenticationEndpoint>(second,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);

        var secondCookies = SetCookies(second);
        Assert.Multiple(() =>
        {
            Assert.That(second.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalStart&prc=100"));
            Assert.That(secondCookies, Has.One.EqualTo($"dosid={created.Session.Dosid}; path=/; samesite=none; secure; HttpOnly"));
            Assert.That(secondCookies, Has.One.StartsWith("acr=deleted;"));
            Assert.That(secondCookies, Has.One.StartsWith("lastLogin=").And.Not.Contain("domain="),
                "Emulator lastLogin cookie must be host-only.");
        });
    }

    [Test]
    public async Task ProjectApi_PublicHostKeepsProductionRedirectAndDomainCookie()
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);

        var first = EndpointContext("www.darkorbit.com", "/ProjectApi/Authentication",
            $"?authUser=291&token={Uri.EscapeDataString(created.RedirectToken)}");
        await Factory.Create<ProjectApiAuthenticationEndpoint>(first,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);
        var location = first.Response.Headers.Location.ToString();
        Assert.That(location, Does.StartWith("https://gbl3.darkorbit.com/ProjectApi/Authentication?authUser=22&token="));

        var second = EndpointContext("gbl3.darkorbit.com", "/ProjectApi/Authentication", location[location.IndexOf('?')..]);
        await Factory.Create<ProjectApiAuthenticationEndpoint>(second,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);
        Assert.That(SetCookies(second), Has.One.StartsWith("lastLogin=").And.EndsWith("domain=.gbl3.darkorbit.com"));
    }

    [TestCase("")]
    [TestCase("?authUser=22")]
    [TestCase("?authUser=291&token=unknown")]
    public async Task ProjectApi_MissingOrUnknownToken_FailsWithoutSessionHeaders(string query)
    {
        var context = EndpointContext("localhost:60952", "/ProjectApi/Authentication", query);

        await Factory.Create<ProjectApiAuthenticationEndpoint>(context,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, NewSessionStore()).HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=externalLogout"));
            Assert.That(SetCookies(context), Is.Empty);
            Assert.That(context.Response.Headers.ContainsKey("Content-Security-Policy"), Is.False);
            Assert.That(context.Response.Headers.ContainsKey("Strict-Transport-Security"), Is.False);
        });
    }

    [TestCase("291", false)]
    [TestCase("22 ", false)]
    [TestCase("", false)]
    [TestCase("22", true)]
    public async Task ProjectApi_OnlyExactAuthUser22SelectsInternalHop(string authUser, bool internalHop)
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);
        var context = EndpointContext("localhost:60952", "/ProjectApi/Authentication",
            $"?authUser={Uri.EscapeDataString(authUser)}&token={Uri.EscapeDataString(created.RedirectToken)}");

        await Factory.Create<ProjectApiAuthenticationEndpoint>(context,
            NullLogger<ProjectApiAuthenticationEndpoint>.Instance, store).HandleAsync(CancellationToken.None);

        var cookies = SetCookies(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(),
                internalHop ? Is.EqualTo("/indexInternal.es?action=internalStart&prc=100")
                    : Does.StartWith("/ProjectApi/Authentication?authUser=22&token="));
            Assert.That(cookies, Has.One.StartsWith(internalHop ? "acr=deleted;" : "acr=22;"));
            Assert.That(cookies.Any(cookie => cookie.StartsWith("lastLogin=", StringComparison.Ordinal)), Is.EqualTo(internalHop));
            Assert.That(cookies, Has.Length.EqualTo(internalHop ? 6 : 5));
            Assert.That(context.Response.Headers["Content-Security-Policy"].ToArray(),
                Is.EqualTo(new[] { "frame-ancestors https://*.darkorbit.com/;" }));
            Assert.That(context.Response.Headers["Strict-Transport-Security"].ToArray(),
                Is.EqualTo(new[] { "max-age=31536000; includeSubDomains" }));
        });
    }

    [Test]
    public async Task SessionStore_RotationConsumesOldTokenWithoutRenewingSession()
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42, "mmo"), null, CancellationToken.None);
        var rotated = await store.RotateRedirectTokenAsync(created.RedirectToken, CancellationToken.None);

        Assert.That(rotated, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(rotated!.RedirectToken, Is.Not.EqualTo(created.RedirectToken));
            Assert.That(rotated.Session.Dosid, Is.EqualTo(created.Session.Dosid));
            Assert.That(rotated.Session.CreatedAt, Is.EqualTo(created.Session.CreatedAt));
            Assert.That(rotated.Session.ExpiresAt, Is.EqualTo(created.Session.ExpiresAt));
            Assert.That(created.Session.ExpiresAt - created.Session.CreatedAt, Is.EqualTo(TimeSpan.FromHours(2)));
        });
        Assert.That(await store.TryGetByRedirectTokenAsync(created.RedirectToken, CancellationToken.None), Is.Null);
        Assert.That(await store.RotateRedirectTokenAsync(created.RedirectToken, CancellationToken.None), Is.Null);
        Assert.That((await store.TryGetByRedirectTokenAsync(rotated!.RedirectToken, CancellationToken.None))?.Dosid,
            Is.EqualTo(created.Session.Dosid));
    }

    [Test]
    public async Task SessionStore_LogoutInvalidatesDosidAndEveryOutstandingRedirectToken()
    {
        var store = NewSessionStore();
        var first = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "first", null, 1), null, CancellationToken.None);
        var second = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "second", null, 2), null, CancellationToken.None);

        await store.InvalidateAsync(first.Session.Dosid, CancellationToken.None);
        var invalidatedSession = await store.TryGetByDosidAsync(first.Session.Dosid, CancellationToken.None);
        var invalidatedToken = await store.TryGetByRedirectTokenAsync(first.RedirectToken, CancellationToken.None);
        var unaffectedSession = await store.TryGetByDosidAsync(second.Session.Dosid, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(invalidatedSession, Is.Null);
            Assert.That(invalidatedToken, Is.Null);
            Assert.That(unaffectedSession, Is.Not.Null);
        });
        Assert.That(await store.RotateRedirectTokenAsync(first.RedirectToken, CancellationToken.None), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("MMO")]
    [TestCase("unknown")]
    public async Task SessionStore_InvalidAccountCompanyIsNotCopiedIntoSession(string? company)
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42, company), null, CancellationToken.None);

        Assert.That(created.Session.Company, Is.Null);
        Assert.That((await store.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None))?.Company, Is.Null);
    }

    [Test]
    public async Task SessionStore_BlankIdentifiersDoNotResolveOrInvalidateAnotherSession()
    {
        var store = NewSessionStore();
        var created = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);

        Assert.That(await store.TryGetByDosidAsync(" ", CancellationToken.None), Is.Null);
        Assert.That(await store.TryGetByRedirectTokenAsync(" ", CancellationToken.None), Is.Null);
        Assert.That(await store.RotateRedirectTokenAsync(" ", CancellationToken.None), Is.Null);
        await store.InvalidateAsync(" ", CancellationToken.None);
        Assert.That(await store.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None), Is.Not.Null);
    }

    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{\"dosid\":42}")]
    [TestCase("{\"dosid\":\" \"}")]
    public async Task SessionStore_DamagedRedirectRecordCannotAuthenticateOrRotate(string payload)
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new GameAuthSessionStore(cache);
        var valid = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);
        await cache.SetStringAsync("gameauth:token:damaged", payload);

        Assert.That(await store.TryGetByRedirectTokenAsync("damaged", CancellationToken.None), Is.Null);
        Assert.That(await store.RotateRedirectTokenAsync("damaged", CancellationToken.None), Is.Null);
        Assert.That(await store.TryGetByRedirectTokenAsync(valid.RedirectToken, CancellationToken.None), Is.Not.Null);
    }

    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{\"expiresAt\":42}")]
    public async Task SessionStore_DamagedSessionCannotAuthenticateOrRotateItsToken(string payload)
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new GameAuthSessionStore(cache);
        var damaged = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "damaged", null, 41), null, CancellationToken.None);
        var valid = await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None);
        await cache.SetStringAsync($"gameauth:dosid:{damaged.Session.Dosid}", payload);

        Assert.That(await store.TryGetByDosidAsync(damaged.Session.Dosid, CancellationToken.None), Is.Null);
        Assert.That(await store.TryGetByRedirectTokenAsync(damaged.RedirectToken, CancellationToken.None), Is.Null);
        Assert.That(await store.RotateRedirectTokenAsync(damaged.RedirectToken, CancellationToken.None), Is.Null);
        Assert.That(await store.TryGetByRedirectTokenAsync(valid.RedirectToken, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public void UnityLogin_WithoutWorldOptions_OmitsGameServerField()
    {
        var payload = UnityLoginPayload("mmo", world: null, mesh: Mesh(Lease(1, "n1", TimeSpan.FromMinutes(1)), Node("n1")));

        Assert.That(payload.AsObject().ContainsKey("gameServerIP"), Is.False);
    }

    [Test]
    public void UnityLogin_WithoutMeshSnapshot_UsesConfiguredWorldEndpoint()
    {
        var payload = UnityLoginPayload("eic", WorldOptions("10.0.0.5", 61000), mesh: null);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("10.0.0.5:61000"));
    }

    [TestCase("mmo", "1")]
    [TestCase("eic", "5")]
    [TestCase("vru", "9")]
    [TestCase("tst", "13")]
    public void UnityLogin_UsesLiveLeaseOwnerOfCompanyStarterMap(string company, string starterMap)
    {
        var mesh = Mesh(
            [Lease(int.Parse(starterMap), "owner", TimeSpan.FromMinutes(1)), Lease(99, "other", TimeSpan.FromMinutes(1))],
            [Node("other", "other.local", 7001), Node("owner", "owner.local", 7002)]);

        var payload = UnityLoginPayload(company, WorldOptions("fallback", 60954), mesh);

        Assert.Multiple(() =>
        {
            Assert.That(payload["mapID"]!.GetValue<string>(), Is.EqualTo(starterMap));
            Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("owner.local:7002"));
            Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo(company));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("MMO")]
    [TestCase("unknown")]
    public void UnityLogin_WithoutCatalogCompany_FallsBackToMapOneAndFactionZero(string? company)
    {
        var mesh = Mesh(Lease(1, "owner", TimeSpan.FromMinutes(1)), Node("owner", "owner.local", 7002));

        var payload = UnityLoginPayload(company, WorldOptions("fallback", 60954), mesh);

        Assert.Multiple(() =>
        {
            Assert.That(payload["mapID"]!.GetValue<string>(), Is.EqualTo("1"));
            Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo("0"));
            Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("owner.local:7002"));
        });
    }

    [Test]
    public void UnityLogin_ExpiredStarterMapLease_FallsBackToConfiguredWorldEndpoint()
    {
        var mesh = Mesh(Lease(1, "owner", TimeSpan.FromMinutes(-1)), Node("owner", "owner.local", 7002));

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("fallback:60954"));
    }

    [TestCase(ServerMeshNodeStatus.Draining, 7002)]
    [TestCase(ServerMeshNodeStatus.Offline, 7002)]
    [TestCase(ServerMeshNodeStatus.Active, 0)]
    [TestCase(ServerMeshNodeStatus.Active, -1)]
    [TestCase(ServerMeshNodeStatus.Active, 65536)]
    public void UnityLogin_UnusableLeaseOwner_FallsBackToConfiguredWorldEndpoint(ServerMeshNodeStatus status, int port)
    {
        var mesh = Mesh(Lease(1, "owner", TimeSpan.FromMinutes(1)), Node("owner", "owner.local", port, status));

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("fallback:60954"));
    }

    [TestCase(1)]
    [TestCase(65535)]
    public void UnityLogin_LeaseOwnerPortBoundsAreInclusive(int port)
    {
        var mesh = Mesh(Lease(1, "owner", TimeSpan.FromMinutes(1)), Node("owner", "owner.local", port));

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo($"owner.local:{port}"));
    }

    [Test]
    public void UnityLogin_LeaseForUnknownNode_FallsBackToConfiguredWorldEndpoint()
    {
        var mesh = Mesh(Lease(1, "ghost", TimeSpan.FromMinutes(1)), Node("owner", "owner.local", 7002));

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("fallback:60954"));
    }

    [Test]
    public void UnityLogin_FirstLiveLeaseForStarterMapWins()
    {
        var mesh = Mesh(
            [Lease(1, "stale", TimeSpan.FromMinutes(-1)), Lease(1, "first", TimeSpan.FromMinutes(1)),
                Lease(1, "second", TimeSpan.FromMinutes(1))],
            [Node("stale", "stale.local", 7000), Node("second", "second.local", 7002), Node("first", "first.local", 7001)]);

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("first.local:7001"));
    }

    [Test]
    public void UnityLogin_FirstLiveLeaseOwnerUnusable_DoesNotFallThroughToLaterLease()
    {
        var mesh = Mesh(
            [Lease(1, "draining", TimeSpan.FromMinutes(1)), Lease(1, "active", TimeSpan.FromMinutes(1))],
            [Node("draining", "draining.local", 7001, ServerMeshNodeStatus.Draining), Node("active", "active.local", 7002)]);

        var payload = UnityLoginPayload("mmo", WorldOptions("fallback", 60954), mesh);

        Assert.That(payload["gameServerIP"]!.GetValue<string>(), Is.EqualTo("fallback:60954"));
    }

    [Test]
    public void UnityLoginRequestContext_ReadsKnownCookiesAndCollectsUnknownOnes()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "dosid=d1; lastLogin=l1; _ga=g1; __bpid=b1; extra=x1";
        context.Request.Headers.UserAgent = "UnityPlayer/6000";
        context.Request.Headers["X-Unity-Version"] = "6000.0.1f1";
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Request.Host = new HostString("localhost", 5000);

        var parsed = UnityLoginRequestContext.Parse(context.Request);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Cookies.Dosid, Is.EqualTo("d1"));
            Assert.That(parsed.Cookies.LastLogin, Is.EqualTo("l1"));
            Assert.That(parsed.Cookies.GoogleAnalytics, Is.EqualTo("g1"));
            Assert.That(parsed.Cookies.BpId, Is.EqualTo("b1"));
            Assert.That(parsed.Cookies.Quantcast, Is.Null);
            Assert.That(parsed.Cookies.Additional, Is.EquivalentTo(new Dictionary<string, string> { ["extra"] = "x1" }));
            Assert.That(parsed.Metadata.UserAgent, Is.EqualTo("UnityPlayer/6000"));
            Assert.That(parsed.Metadata.UnityVersion, Is.EqualTo("6000.0.1f1"));
            Assert.That(parsed.Metadata.AcceptEncoding, Is.EqualTo("gzip"));
            Assert.That(parsed.Metadata.Host, Is.EqualTo("localhost:5000"));
        });
    }

    [Test]
    public void UnityLoginRequestContext_WithoutHeaders_YieldsNoCookiesAndEmptyMetadata()
    {
        var parsed = UnityLoginRequestContext.Parse(new DefaultHttpContext().Request);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Cookies.Dosid, Is.Null);
            Assert.That(parsed.Cookies.Additional, Is.Empty);
            Assert.That(parsed.Metadata.UserAgent, Is.Empty);
            Assert.That(parsed.Metadata.UnityVersion, Is.Empty);
        });
    }

    private static JsonNode UnityLoginPayload(string? company, WorldServerOptions? world, ServerMeshSnapshot? mesh)
        => JsonNode.Parse(UnityLoginPayloadBuilder.Build(new GameAuthSession { Company = company, GameUserId = 7 },
            new DefaultHttpContext().Request, mesh: mesh, world: world))!;

    private static WorldServerOptions WorldOptions(string host, int port) => new() { Host = host, Port = port };

    private static ServerMapLease Lease(int mapId, string nodeId, TimeSpan expiresIn)
        => new(mapId, nodeId, 1, DateTimeOffset.UtcNow.Add(expiresIn));

    private static ServerMeshNode Node(string nodeId, string host = "node.local", int port = 7000,
        ServerMeshNodeStatus status = ServerMeshNodeStatus.Active)
        => new(nodeId, nodeId, $"http://{host}", host, port, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            0, [], 0, 0);

    private static ServerMeshSnapshot Mesh(ServerMapLease lease, ServerMeshNode node) => new([node], [lease]);

    private static ServerMeshSnapshot Mesh(ServerMapLease[] leases, ServerMeshNode[] nodes) => new(nodes, leases);
}
