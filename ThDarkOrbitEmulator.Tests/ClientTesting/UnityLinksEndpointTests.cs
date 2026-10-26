using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class UnityLinksEndpointTests
{
    private static readonly string[] CapturedKeys = ["imprint", "credits", "forum", "support", "help", "accountCenter"];

    [Test]
    public async Task Links_ReturnCapturedKeysHeadersAndExactContentLength()
    {
        using var env = new Env();
        var context = Context();

        var body = await env.HandleAsync(context);

        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.ContentLength, Is.EqualTo(Encoding.UTF8.GetByteCount(body)));
            Assert.That(context.Response.Headers["X-Custom-Header"].ToString(), Is.EqualTo("DarkOrbitUnity"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo(UnityLinksAction.CacheControl));
            Assert.That(context.Response.Headers.Pragma.ToString(), Is.EqualTo("no-cache"));
            Assert.That(context.Response.Headers.Expires.ToString(), Is.EqualTo("Thu, 19 Nov 1981 08:52:00 GMT"));
            Assert.That(context.Response.Headers.ContentSecurityPolicy.ToString(),
                Is.EqualTo(UnityLinksAction.FrameAncestorsPolicy));
            Assert.That(context.Response.Headers.StrictTransportSecurity.ToString(),
                Is.EqualTo("max-age=31536000; includeSubDomains"));
            Assert.That(json.Select(pair => pair.Key), Is.EqualTo(CapturedKeys));
            Assert.That(json.Select(pair => pair.Value!.GetValueKind()),
                Is.All.EqualTo(System.Text.Json.JsonValueKind.String));
            Assert.That(json["forum"]!.GetValue<string>(), Is.EqualTo("/GameAPI.php?action=portal.redirectToBoard"));
            Assert.That(json["credits"]!.GetValue<string>(), Is.Empty);
        });
    }

    [Test]
    public async Task Links_WithLiveSession_RefreshExactlyOneDosidCookie()
    {
        using var env = new Env();
        var session = await env.CreateAsync();
        var context = Context(session.Dosid);

        await env.HandleAsync(context);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.EqualTo(1));
            Assert.That(cookie, Does.StartWith($"dosid={session.Dosid};"));
            Assert.That(cookie.ToLowerInvariant(),
                Does.Contain("path=/").And.Contain("httponly").And.Contain("secure").And.Contain("samesite=none"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("unknown-session")]
    public async Task Links_WithoutLiveSession_StayPublicWithoutCookie(string? dosid)
    {
        using var env = new Env();
        var context = Context(dosid);

        var body = await env.HandleAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(JsonNode.Parse(body)!.AsObject().Count, Is.EqualTo(CapturedKeys.Length));
        });
    }

    [Test]
    public async Task Links_AfterLogout_DoNotRefreshCookie()
    {
        using var env = new Env();
        var session = await env.CreateAsync();
        await env.Sessions.InvalidateAsync(session.Dosid, CancellationToken.None);
        var context = Context(session.Dosid);

        await env.HandleAsync(context);

        Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
    }

    [TestCase(128, 1)]
    [TestCase(129, 0)]
    public async Task Links_OnlyLookUpBoundedDosids(int length, int lookups)
    {
        using var env = new Env();
        var counting = new CountingStore(env.Sessions);
        var context = Context(new string('a', length));

        await env.HandleAsync(context, counting);

        Assert.Multiple(() =>
        {
            Assert.That(counting.Lookups, Is.EqualTo(lookups));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [TestCase("localhost", "gbl3.darkorbit.com", "https://gbl3.darkorbit.com")]
    [TestCase("localhost", "gbl5.darkorbit.com", "https://gbl5.darkorbit.com")]
    [TestCase("localhost", "evil.example", "https://localhost:60952")]
    [TestCase("localhost", null, "https://localhost:60952")]
    [TestCase("gbl3.darkorbit.com", "evil.example", "https://gbl3.darkorbit.com")]
    public async Task Links_PointLocalPagesAtTheValidatedGameHost(string host, string? originalHost, string expected)
    {
        using var env = new Env();
        var context = Context(host: host, originalHost: originalHost);

        var json = JsonNode.Parse(await env.HandleAsync(context))!;

        Assert.Multiple(() =>
        {
            Assert.That(json["support"]!.GetValue<string>(),
                Is.EqualTo($"{expected}/indexInternal.es?action=internalSupport"));
            Assert.That(json["accountCenter"]!.GetValue<string>(),
                Is.EqualTo($"{expected}/indexInternal.es?action=internalAccount"));
            Assert.That(json.ToJsonString(), Does.Not.Contain("evil.example"));
        });
    }

    [TestCase("localhost:60952", "HTTPS", "https://localhost:60952")]
    [TestCase("127.0.0.1:60952", "http", "http://127.0.0.1:60952")]
    [TestCase("[::1]:60952", "ftp", "https://[::1]:60952")]
    [TestCase("public.example", "https", "https://api-game")]
    public async Task Links_ForwardedHostUsesOnlyTrustedLoopbackAndHttpScheme(
        string forwardedHost, string forwardedProto, string expected)
    {
        using var env = new Env();
        var context = Context(host: "api-game", originalHost: null);
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;

        var json = JsonNode.Parse(await env.HandleAsync(context))!;

        Assert.Multiple(() =>
        {
            Assert.That(json["support"]!.GetValue<string>(),
                Is.EqualTo($"{expected}/indexInternal.es?action=internalSupport"));
            Assert.That(json["accountCenter"]!.GetValue<string>(),
                Is.EqualTo($"{expected}/indexInternal.es?action=internalAccount"));
        });
    }

    [Test]
    public async Task Links_FirstForwardedHostControlsResolution()
    {
        using var env = new Env();
        var context = Context(host: "api-game", originalHost: null);
        context.Request.Headers["X-Forwarded-Host"] = "public.example, localhost:60952";

        var json = JsonNode.Parse(await env.HandleAsync(context))!;

        Assert.That(json["support"]!.GetValue<string>(),
            Is.EqualTo("https://api-game/indexInternal.es?action=internalSupport"));
    }

    [Test]
    public async Task Links_QueryAndOtherCookiesDoNotChangePublicBody()
    {
        using var env = new Env();
        var plain = await env.HandleAsync(Context(originalHost: null));
        var noisy = Context(originalHost: null);
        noisy.Request.QueryString = new QueryString("?lang=de&next=https://evil.example");
        noisy.Request.Headers.Cookie = "other=untrusted";

        var body = await env.HandleAsync(noisy);

        Assert.Multiple(() =>
        {
            Assert.That(body, Is.EqualTo(plain));
            Assert.That(noisy.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    private static DefaultHttpContext Context(string? dosid = null, string host = "localhost",
        string? originalHost = "gbl3.darkorbit.com")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = host == "localhost" ? new HostString(host, 60952) : new HostString(host);
        if (originalHost is not null)
        {
            context.Request.Headers["X-Original-Host"] = originalHost;
        }

        if (dosid is not null)
        {
            context.Request.Headers.Cookie = $"dosid={dosid}";
        }

        return context;
    }

    private sealed class Env : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();

        public Env() => Sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());

        public GameAuthSessionStore Sessions { get; }

        public async Task<GameAuthSession> CreateAsync()
        {
            var created = await Sessions.CreateSessionAsync(
                new AccountSummary(Guid.NewGuid(), "pilot7", null, 7), null, CancellationToken.None);
            Assert.That(await Sessions.SelectCompanyAsync(created.Session.Dosid, "mmo", CancellationToken.None), Is.True);
            return (await Sessions.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None))!;
        }

        public async Task<string> HandleAsync(HttpContext context, IGameAuthSessionStore? store = null)
        {
            await using var body = new MemoryStream();
            context.Response.Body = body;
            await UnityLinksAction.HandleAsync(context, store ?? Sessions, CancellationToken.None);
            return Encoding.UTF8.GetString(body.ToArray());
        }

        public void Dispose() => services.Dispose();
    }

    private sealed class CountingStore(IGameAuthSessionStore inner) : IGameAuthSessionStore
    {
        public int Lookups { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            Lookups++;
            return inner.TryGetByDosidAsync(dosid, cancellationToken);
        }

        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) => inner.InvalidateAsync(dosid, cancellationToken);
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken, CancellationToken cancellationToken) => inner.CreateSessionAsync(account, keycloakAccessToken, cancellationToken);
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken) => inner.RotateRedirectTokenAsync(redirectToken, cancellationToken);
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken) => inner.TryGetByRedirectTokenAsync(redirectToken, cancellationToken);
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken) => inner.SelectCompanyAsync(dosid, company, cancellationToken);
    }
}
