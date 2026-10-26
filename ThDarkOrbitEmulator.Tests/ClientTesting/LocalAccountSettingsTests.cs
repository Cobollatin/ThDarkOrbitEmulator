using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAccountSettingsTests
{
    [Test]
    public async Task Preferences_PersistPerAccount_AndRejectInvalidValues()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalAccountPreferencesStore(services.GetRequiredService<IDistributedCache>());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.That(await store.SaveAsync(first, "en", "false", "true", CancellationToken.None), Is.True);
        Assert.That(await store.GetAsync(first, CancellationToken.None),
            Is.EqualTo(new LocalAccountPreferences("en", false, true)));
        Assert.That(await store.GetAsync(second, CancellationToken.None), Is.EqualTo(LocalAccountPreferencesStore.Default));
        Assert.That(await store.SaveAsync(first, "unknown", "true", "false", CancellationToken.None), Is.False);
        Assert.That(await store.GetAsync(first, CancellationToken.None),
            Is.EqualTo(new LocalAccountPreferences("en", false, true)));
    }

    [Test]
    public async Task SaveAction_RequiresSessionAndCompany_AndRedirectsOnSuccess()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalAccountPreferencesStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "language=en&sound=false&notifications=true");
        await LocalAccountSettingsAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "language=en&sound=false&notifications=true");
        await LocalAccountSettingsAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var invalid = Request(session.Dosid, "language=xx&sound=false&notifications=true");
        await LocalAccountSettingsAction.HandleAsync(invalid, sessions, store, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        var valid = Request(session.Dosid, "language=en&sound=false&notifications=true");
        await LocalAccountSettingsAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
            Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=1"));
        });
    }

    [Test]
    public void Page_ShowsKnownIdentityAndEscapesName()
    {
        var page = LocalAccountSettings.Page(new GameAuthSession
        { Username = "<pilot>", GameUserId = 7, Company = "mmo" }, new LocalAccountPreferences("en", false, true));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"localAccountCenter\""));
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(page, Does.Not.Contain("<pilot>"));
            Assert.That(page, Does.Contain("value=\"en\" selected"));
            Assert.That(page, Does.Contain("action=internalAccountSave"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalAccount"));
        });
    }

    private static DefaultHttpContext Request(string? dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
