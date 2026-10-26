using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLogoutTests
{
    [Test]
    public async Task Logout_InvalidatesSessionAndExpiresCookie()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var created = await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None);
        var response = new DefaultHttpContext().Response;

        await LocalLogoutAction.InvalidateAsync(response, created.Session.Dosid, sessions, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Headers.SetCookie.ToString(), Does.Contain("dosid=; path=/; expires="));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=externalLogout"));
        });
        Assert.That(await sessions.TryGetByDosidAsync(created.Session.Dosid, CancellationToken.None), Is.Null);
        Assert.That(await sessions.TryGetByRedirectTokenAsync(created.RedirectToken, CancellationToken.None), Is.Null);
    }
}
