using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class SetLangEndpointTests
{
    [TestCase("lang=en", "en")]
    [TestCase("lang=en_US", "en_US")]
    [TestCase("lang=pt_br", "pt_BR")]
    [TestCase("lang=es_AR", "es_AR")]
    public async Task SetLang_SavesLanguage_AndKeepsOtherPreferences(string body, string expected)
    {
        var (sessions, store, session) = await CreateAsync();
        await store.SaveAsync(session.AccountId, "es", "false", "true", null, CancellationToken.None);

        var status = await SetLangEndpoint.SaveAsync(Request(session.Dosid, body), sessions, store, CancellationToken.None);

        Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(await store.GetAsync(session.AccountId, CancellationToken.None),
            Is.EqualTo(new LocalAccountPreferences(expected, false, true)));
    }

    [TestCase(null, "lang=en", StatusCodes.Status401Unauthorized)]
    [TestCase("unknown", "lang=en", StatusCodes.Status401Unauthorized)]
    [TestCase("valid", "lang=xx", StatusCodes.Status400BadRequest)]
    [TestCase("valid", null, StatusCodes.Status400BadRequest)]
    public async Task SetLang_RejectsBadRequests_WithoutSaving(string? dosid, string? body, int expected)
    {
        var (sessions, store, session) = await CreateAsync();

        var status = await SetLangEndpoint.SaveAsync(Request(dosid == "valid" ? session.Dosid : dosid, body),
            sessions, store, CancellationToken.None);

        Assert.That(status, Is.EqualTo(expected));
        Assert.That(await store.GetAsync(session.AccountId, CancellationToken.None),
            Is.EqualTo(LocalAccountPreferencesStore.Default));
    }

    private static HttpRequest Request(string? dosid, string? body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/unityApi/setLang.php";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        if (body is not null)
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }
        return context.Request;
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalAccountPreferencesStore Store, GameAuthSession Session)> CreateAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        return (sessions, new LocalAccountPreferencesStore(cache), session);
    }
}
