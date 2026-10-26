using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class UnityLoginEndpointHardeningTests
{
    [Test]
    public async Task Login_WithLiveSession_ReturnsPayloadRefreshesCookieAndIsNotCached()
    {
        using var env = new Env();
        var session = await env.CreateAsync("mmo");
        var context = new DefaultHttpContext();

        var payload = await env.HandleAsync(context, session.Dosid);

        Assert.That(payload, Is.Not.Null);
        var json = JsonNode.Parse(payload!)!;
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo(UnityLoginAction.CacheControl));
            Assert.That(context.Response.Headers["X-Custom-Header"].ToString(), Is.EqualTo("DarkOrbitUnity"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.EqualTo(1));
            Assert.That(cookie, Does.StartWith($"dosid={session.Dosid};"));
            Assert.That(cookie.ToLowerInvariant(), Does.Contain("httponly").And.Contain("secure").And.Contain("samesite=none"));
            Assert.That(json["sessionID"]!.GetValue<string>(), Is.EqualTo(session.Dosid));
            Assert.That(json["nextAction"]!.GetValue<string>(), Is.EqualTo("internalWebGL"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("unknown-session")]
    public async Task Login_WithoutLiveSession_Returns401WithoutCookieOrPayload(string? dosid)
    {
        using var env = new Env();
        var context = new DefaultHttpContext();

        var payload = await env.HandleAsync(context, dosid);

        Assert.Multiple(() =>
        {
            Assert.That(payload, Is.Null);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo(UnityLoginAction.CacheControl));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(context.Response.Headers.ContainsKey("X-Custom-Header"), Is.False);
        });
    }

    [Test]
    public async Task Login_AfterLogout_Returns401()
    {
        using var env = new Env();
        var session = await env.CreateAsync("eic");
        await LocalLogoutAction.InvalidateAsync(new DefaultHttpContext().Response, session.Dosid, env.Sessions,
            CancellationToken.None);
        var context = new DefaultHttpContext();

        Assert.That(await env.HandleAsync(context, session.Dosid), Is.Null);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task Login_OversizedCookie_IsRejectedWithoutLookup()
    {
        using var env = new Env();
        var counting = new CountingStore(env.Sessions);
        var context = new DefaultHttpContext();

        var payload = await UnityLoginAction.HandleAsync(context, new string('a', LocalLogoutAction.MaxDosidLength + 1),
            counting, env.Hangars, env.Preferences, CancellationToken.None);
        await UnityLoginAction.HandleAsync(new DefaultHttpContext(), new string('a', LocalLogoutAction.MaxDosidLength),
            counting, env.Hangars, env.Preferences, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(payload, Is.Null);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(counting.Lookups, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Login_OnlyReturnsTheCookieSessionsOwnAccount()
    {
        using var env = new Env();
        var first = await env.CreateAsync("mmo", 11);
        var second = await env.CreateAsync("vru", 22);
        Assert.That(await env.Hangars.SelectAsync(second.AccountId, 2, CancellationToken.None), Is.True);

        var a = JsonNode.Parse((await env.HandleAsync(new DefaultHttpContext(), first.Dosid))!)!;
        var b = JsonNode.Parse((await env.HandleAsync(new DefaultHttpContext(), second.Dosid))!)!;

        Assert.Multiple(() =>
        {
            Assert.That(a["userID"]!.GetValue<string>(), Is.EqualTo("11"));
            Assert.That(b["userID"]!.GetValue<string>(), Is.EqualTo("22"));
            Assert.That(ActiveSlots(a), Is.EqualTo(new[] { true, false }));
            Assert.That(ActiveSlots(b), Is.EqualTo(new[] { false, true }));
        });
    }

    [TestCase("es")]
    [TestCase("en")]
    [TestCase("de")]
    [TestCase("fr")]
    [TestCase("it")]
    [TestCase("pl")]
    [TestCase("tr")]
    [TestCase("pt")]
    public async Task Login_UsesEveryAccountLanguage(string language)
    {
        using var env = new Env();
        var session = await env.CreateAsync("mmo");
        Assert.That(await env.Preferences.SaveAsync(session.AccountId, language, "true", "true", CancellationToken.None), Is.True);

        var saved = JsonNode.Parse((await env.HandleAsync(new DefaultHttpContext(), session.Dosid))!)!;
        Assert.That(saved["lang"]!.GetValue<string>(), Is.EqualTo(language));
    }

    [TestCase("<script>")]
    [TestCase("EN")]
    [TestCase("")]
    public void Login_FallsBackForUnsupportedLanguage(string language)
    {
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { Company = "mmo", GameUserId = 7 }, new DefaultHttpContext().Request,
            language: language))!;
        Assert.That(payload["lang"]!.GetValue<string>(), Is.EqualTo("es"));
    }

    private static bool[] ActiveSlots(JsonNode payload)
        => payload["hangars"]!["data"]!["hangars"]!.AsArray().Select(h => h!["hangarActive"]!.GetValue<bool>()).ToArray();

    private sealed class Env : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();

        public Env()
        {
            var cache = services.GetRequiredService<IDistributedCache>();
            Sessions = new GameAuthSessionStore(cache);
            Hangars = new LocalHangarStore(cache);
            Preferences = new LocalAccountPreferencesStore(cache);
        }

        public GameAuthSessionStore Sessions { get; }
        public LocalHangarStore Hangars { get; }
        public LocalAccountPreferencesStore Preferences { get; }

        public async Task<GameAuthSession> CreateAsync(string company, long gameUserId = 7)
        {
            var created = await Sessions.CreateSessionAsync(
                new AccountSummary(Guid.NewGuid(), $"pilot{gameUserId}", null, gameUserId), null, CancellationToken.None);
            Assert.That(await Sessions.SelectCompanyAsync(created.Session.Dosid, company, CancellationToken.None), Is.True);
            return (await Sessions.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None))!;
        }

        public Task<string?> HandleAsync(HttpContext context, string? dosid)
            => UnityLoginAction.HandleAsync(context, dosid, Sessions, Hangars, Preferences, CancellationToken.None);

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
