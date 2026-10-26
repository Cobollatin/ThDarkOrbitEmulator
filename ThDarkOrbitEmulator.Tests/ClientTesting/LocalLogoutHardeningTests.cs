using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogoutHardeningTests
{
    [Test]
    public async Task Logout_RedirectsToSignedOutStepWithoutNewSessionCookie()
    {
        var (services, sessions) = Store();
        using var _ = services;
        var created = await Create(sessions);
        var response = new DefaultHttpContext().Response;

        await LocalLogoutAction.HandleAsync(response, created.Session.Dosid, sessions, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(302));
            Assert.That(response.Headers.Location.ToString(), Is.EqualTo(LocalLogoutAction.LogoutLocation));
            Assert.That(response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(response.Headers.SetCookie.Count, Is.EqualTo(1));
            Assert.That(response.Headers.SetCookie.ToString(), Is.EqualTo(LocalLogoutAction.ExpiredCookie));
            Assert.That(response.Headers.SetCookie.ToString(), Does.Not.Contain(created.Session.Dosid));
        });
        Assert.That(await sessions.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("unknown-session")]
    public async Task Logout_WithoutLiveSession_StillExpiresCookieAndRedirects(string? dosid)
    {
        var (services, sessions) = Store();
        using var _ = services;
        var response = new DefaultHttpContext().Response;

        await LocalLogoutAction.HandleAsync(response, dosid, sessions, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(302));
            Assert.That(response.Headers.Location.ToString(), Is.EqualTo(LocalLogoutAction.LogoutLocation));
            Assert.That(response.Headers.SetCookie.ToString(), Is.EqualTo(LocalLogoutAction.ExpiredCookie));
        });
    }

    [Test]
    public async Task Logout_IsIdempotentForStaleCookie()
    {
        var (services, sessions) = Store();
        using var _ = services;
        var created = await Create(sessions);

        await LocalLogoutAction.HandleAsync(new DefaultHttpContext().Response, created.Session.Dosid, sessions, CancellationToken.None);
        var second = new DefaultHttpContext().Response;
        await LocalLogoutAction.HandleAsync(second, created.Session.Dosid, sessions, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.StatusCode, Is.EqualTo(302));
            Assert.That(second.Headers.SetCookie.ToString(), Is.EqualTo(LocalLogoutAction.ExpiredCookie));
        });
    }

    [Test]
    public async Task Logout_OnlyEndsTheCookieSession()
    {
        var (services, sessions) = Store();
        using var _ = services;
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var first = await sessions.CreateSessionAsync(account, null, CancellationToken.None);
        var second = await sessions.CreateSessionAsync(account, null, CancellationToken.None);

        await LocalLogoutAction.HandleAsync(new DefaultHttpContext().Response, first.Session.Dosid, sessions, CancellationToken.None);

        Assert.That(await sessions.TryGetByDosidAsync(first.Session.Dosid, CancellationToken.None), Is.Null);
        Assert.That(await sessions.TryGetByDosidAsync(second.Session.Dosid, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task Logout_InvalidatedSessionCannotBeRecovered()
    {
        var (services, sessions) = Store();
        using var _ = services;
        var created = await Create(sessions);

        await LocalLogoutAction.HandleAsync(new DefaultHttpContext().Response, created.Session.Dosid, sessions, CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await sessions.RotateRedirectTokenAsync(created.RedirectToken, CancellationToken.None), Is.Null);
            Assert.That(await sessions.SelectCompanyAsync(created.Session.Dosid, "eic", CancellationToken.None), Is.False);
            Assert.That(await sessions.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None), Is.Null);
        });
    }

    [Test]
    public async Task Logout_IgnoresOversizedCookieWithoutLookup()
    {
        var store = new RecordingStore();
        var response = new DefaultHttpContext().Response;

        await LocalLogoutAction.HandleAsync(response, new string('a', LocalLogoutAction.MaxDosidLength + 1), store, CancellationToken.None);
        await LocalLogoutAction.HandleAsync(new DefaultHttpContext().Response, new string('a', LocalLogoutAction.MaxDosidLength), store, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(store.Invalidated, Is.EqualTo(new[] { new string('a', LocalLogoutAction.MaxDosidLength) }));
            Assert.That(response.StatusCode, Is.EqualTo(302));
            Assert.That(response.Headers.SetCookie.ToString(), Is.EqualTo(LocalLogoutAction.ExpiredCookie));
        });
    }

    [Test]
    public void SignedOutStep_RedirectsToHomeWithLoginError()
    {
        var response = new DefaultHttpContext().Response;

        LocalLogoutAction.SignedOut(response);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(302));
            Assert.That(response.Headers.Location.ToString(), Is.EqualTo("/index.es?action=externalHome&loginError=94"));
            Assert.That(response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    // The dispatcher routes actions case-insensitively, so any spelling of the logout action logs out.
    [TestCase("externalLogout", true)]
    [TestCase("EXTERNALLOGOUT", true)]
    [TestCase("externallogout", true)]
    [TestCase("externalLogout ", false)]
    [TestCase("externalLogoutX", false)]
    [TestCase("internalWebGL", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsLogout_MatchesOnlyTheLogoutAction(string? action, bool expected)
        => Assert.That(LocalLogoutAction.IsLogout(action), Is.EqualTo(expected));

    [TestCase("internalWebGL", "externalLogout", true)]
    [TestCase("internalMenu", "externalLogout", true)]
    [TestCase("INTERNALWEBGL", "EXTERNALLOGOUT", true)]
    [TestCase("internalWebGL", "internalStart", false)]
    [TestCase("internalWebGL", null, false)]
    [TestCase("internalDock", "externalLogout", false)]
    [TestCase("internalStart", "externalLogout", false)]
    public void IsSignedOutStep_RequiresWebGlFromLogout(string? action, string? from, bool expected)
        => Assert.That(LocalLogoutAction.IsSignedOutStep(action, from), Is.EqualTo(expected));

    // A spoofed `from=externalLogout` on other actions no longer drops the session cookie refresh.
    [TestCase("externalLogout", null, true)]
    [TestCase("internalWebGL", "externalLogout", true)]
    [TestCase("internalDock", "externalLogout", false)]
    [TestCase("internalShop", "externalLogout", false)]
    [TestCase("internalWebGL", "internalStart", false)]
    [TestCase(null, null, false)]
    public void SuppressesSessionCookie_OnlyForLogoutChain(string? action, string? from, bool expected)
        => Assert.That(LocalLogoutAction.SuppressesSessionCookie(action, from), Is.EqualTo(expected));

    private static (ServiceProvider, GameAuthSessionStore) Store()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        return (services, new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>()));
    }

    private static Task<GameAuthSessionCreateResult> Create(GameAuthSessionStore sessions)
        => sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None);

    private sealed class RecordingStore : IGameAuthSessionStore
    {
        public List<string> Invalidated { get; } = [];

        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken)
        {
            Invalidated.Add(dosid);
            return Task.CompletedTask;
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
