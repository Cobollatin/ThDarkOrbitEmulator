using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Save redirect carries the applied revision; the page confirms it only while it is current.
public class LocalAccountSettingsSavedNoticeTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" };

    [TestCase("action=internalAccount&saved=1", true)]
    [TestCase("action=internalAccount&saved=1000000", true)]
    [TestCase("action=internalAccount&from=internalStart&saved=42", true)]
    [TestCase("action=internalAccount&saved=0", false)]
    [TestCase("action=internalAccount&saved=1000001", false)]
    [TestCase("action=internalAccount&saved=", false)]
    [TestCase("action=internalAccount&saved=-1", false)]
    [TestCase("action=internalAccount&saved=%2B1", false)]
    [TestCase("action=internalAccount&saved=%201", false)]
    [TestCase("action=internalAccount&saved=1.0", false)]
    [TestCase("action=internalAccount&saved=abc", false)]
    [TestCase("action=internalAccount&saved=%D9%A1", false)]
    [TestCase("action=internalAccount&saved=00000001", false)]
    [TestCase("action=internalAccount&saved=1&saved=1", false)]
    [TestCase("action=internalAccount&SAVED=1", false)]
    [TestCase("action=internalAccount&Saved=1", false)]
    [TestCase("ACTION=internalAccount", false)]
    [TestCase("Action=internalAccount", false)]
    [TestCase("action=INTERNALACCOUNT", false)]
    [TestCase("action=internalaccount", false)]
    [TestCase("action=internalAccount&ACTION=internalAccount", false)]
    [TestCase("action=internalAccount&FROM=internalStart", false)]
    [TestCase("action=internalAccount&Prc=1", false)]
    [TestCase("action=internalAccount&LANG=en", false)]
    [TestCase("action=internalAccount&from=internalStart&prc=1&lang=en", true)]
    public void PageQuery_ValidatesSavedRevision(string query, bool expected) =>
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void SavedRevision_IsNullWithoutParameter_AndParsedWhenPresent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalAccountSettingsAction.SavedRevision(Query("action=internalAccount")), Is.Null);
            Assert.That(LocalAccountSettingsAction.SavedRevision(Query("action=internalAccount&saved=7")), Is.EqualTo(7));
            Assert.That(LocalAccountSettingsAction.SavedLocation(7), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=7"));
        });
    }

    [Test]
    public void Page_ConfirmsCurrentRevision()
    {
        var html = LocalAccountSettings.Page(Session, new LocalAccountPreferences("en", false, true), 3, 3);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<p id=\"accountSaved\" role=\"status\" data-rev=\"3\">Preferences saved (change 3).</p>"));
            Assert.That(html, Does.Not.Contain("accountSavedStale"));
            Assert.That(html.IndexOf("accountSaved", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("<h2>Current preferences</h2>", StringComparison.Ordinal)));
        });
    }

    [TestCase(2, 3)]
    [TestCase(9, 3)]
    public void Page_ShowsNeutralNoticeForOutdatedRevision(int saved, int current)
    {
        var html = LocalAccountSettings.Page(Session, LocalAccountPreferencesStore.Default, current, saved);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"id=\"accountSavedStale\" role=\"status\" data-rev=\"{saved}\""));
            Assert.That(html, Does.Contain($"Preferences changed again after change {saved}."));
            Assert.That(html, Does.Not.Contain("Preferences saved ("));
        });
    }

    [Test]
    public void Page_WithoutSavedRevision_HasNoNotice()
    {
        var html = LocalAccountSettings.Page(Session, LocalAccountPreferencesStore.Default, 4);
        Assert.That(html, Does.Not.Contain("accountSaved"));
    }

    [Test]
    public async Task SaveRedirects_CarryEachAppliedRevision_AndConflictsDoNot()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalAccountPreferencesStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        var first = await SaveAsync(sessions, store, session.Dosid, "language=en&sound=false&notifications=true&rev=0");
        var second = await SaveAsync(sessions, store, session.Dosid, "language=de&sound=true&notifications=false&rev=1");
        var replay = await SaveAsync(sessions, store, session.Dosid, "language=de&sound=true&notifications=false&rev=1");
        var legacy = await SaveAsync(sessions, store, session.Dosid, "language=fr&sound=true&notifications=true");
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=1"));
            Assert.That(second.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=2"));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(replay.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(legacy.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=3"));
        });
        // Each redirect target is itself a valid page query.
        foreach (var context in new[] { first, second, legacy })
        {
            var location = context.Response.Headers.Location.ToString();
            Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query(location[(location.IndexOf('?') + 1)..])), Is.True);
        }
    }

    [Test]
    public async Task SaveRedirect_AfterRevisionWrap_PointsAtRevisionOne()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalAccountPreferencesStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        await cache.SetStringAsync($"localaccountprefs:{session.AccountId:N}",
            $"{{\"Language\":\"en\",\"SoundEnabled\":true,\"NotificationsEnabled\":true,\"Revision\":{LocalAccountPreferencesStore.MaxRevision}}}");
        var context = await SaveAsync(sessions, store, session.Dosid,
            $"language=de&sound=true&notifications=true&rev={LocalAccountPreferencesStore.MaxRevision}");
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=1"));
    }

    private static async Task<DefaultHttpContext> SaveAsync(IGameAuthSessionStore sessions,
        LocalAccountPreferencesStore store, string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalAccountSave");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalAccountSettingsAction.HandleAsync(context, sessions, store, CancellationToken.None);
        return context;
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));
}
