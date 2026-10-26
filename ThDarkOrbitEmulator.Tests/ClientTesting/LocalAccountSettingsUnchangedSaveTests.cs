using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-187: a save that matches the stored preferences writes nothing, keeps the revision,
// and redirects to a "no changes" notice instead of a false "saved" confirmation.
public class LocalAccountSettingsUnchangedSaveTests
{
    private static readonly GameAuthSession PageSession = new() { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" };

    [Test]
    public async Task Store_SameValues_IsUnchanged_AndKeepsRevision()
    {
        var (_, _, store, session) = await CreateAsync();
        var account = session.AccountId;
        Assert.That(await store.SaveStateAsync(account, "es", "true", "true", 0, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Unchanged, 0)), "defaults resubmitted before any save");
        Assert.That(await store.SaveStateAsync(account, "de", "false", "true", 0, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Applied, 1)));
        Assert.That(await store.SaveStateAsync(account, "de", "false", "true", 1, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Unchanged, 1)));
        Assert.That(await store.SaveStateAsync(account, "de", "false", "true", null, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Unchanged, 1)), "legacy form without rev");
        Assert.That(await store.SaveAsync(account, "de", "false", "true", CancellationToken.None), Is.True);
        Assert.That(await store.GetStateAsync(account, CancellationToken.None),
            Is.EqualTo((new LocalAccountPreferences("de", false, true), 1)));
    }

    [Test]
    public async Task Store_StaleRevisionWithSameValues_IsStillConflict()
    {
        var (_, _, store, session) = await CreateAsync();
        Assert.That(await store.SaveStateAsync(session.AccountId, "en", "true", "true", 0, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Applied, 1)));
        Assert.That(await store.SaveStateAsync(session.AccountId, "en", "true", "true", 0, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Conflict, 1)));
        Assert.That(await store.SaveStateAsync(session.AccountId, "xx", "true", "true", 1, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Invalid, 0)));
    }

    [Test]
    public async Task Store_OnlyOneFieldChanged_IsApplied()
    {
        var (_, _, store, session) = await CreateAsync();
        Assert.That((await store.SaveStateAsync(session.AccountId, "es", "false", "true", 0, CancellationToken.None)).Change,
            Is.EqualTo(AccountPreferencesChange.Applied));
        Assert.That((await store.SaveStateAsync(session.AccountId, "es", "false", "false", 1, CancellationToken.None)).Change,
            Is.EqualTo(AccountPreferencesChange.Applied));
        Assert.That((await store.SaveStateAsync(session.AccountId, "pt", "false", "false", 2, CancellationToken.None)).Change,
            Is.EqualTo(AccountPreferencesChange.Applied));
        Assert.That((await store.GetStateAsync(session.AccountId, CancellationToken.None)).Revision, Is.EqualTo(3));
    }

    [Test]
    public async Task Store_CorruptCacheReadsAsDefaults_SoDefaultSaveIsUnchanged()
    {
        var (cache, _, store, session) = await CreateAsync();
        await cache.SetStringAsync($"localaccountprefs:{session.AccountId:N}",
            "{\"Language\":\"xx\",\"SoundEnabled\":true,\"NotificationsEnabled\":true,\"Revision\":4}");
        Assert.That(await store.SaveStateAsync(session.AccountId, "es", "true", "true", 4, CancellationToken.None),
            Is.EqualTo((AccountPreferencesChange.Unchanged, 4)));
    }

    [Test]
    public async Task Endpoint_UnchangedSave_Redirects303ToUnchangedNotice()
    {
        var (_, sessions, store, session) = await CreateAsync();
        var first = await SaveAsync(sessions, store, session.Dosid, "language=it&sound=true&notifications=false&rev=0");
        var same = await SaveAsync(sessions, store, session.Dosid, "language=it&sound=true&notifications=false&rev=1");
        var legacySame = await SaveAsync(sessions, store, session.Dosid, "language=it&sound=true&notifications=false");
        var stale = await SaveAsync(sessions, store, session.Dosid, "language=it&sound=true&notifications=false&rev=0");
        Assert.Multiple(async () =>
        {
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=1"));
            foreach (var context in new[] { same, legacySame })
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(303));
                Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo(LocalAccountSettingsAction.UnchangedLocation));
                Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            }
            Assert.That(stale.Response.StatusCode, Is.EqualTo(409));
            Assert.That(stale.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That((await store.GetStateAsync(session.AccountId, CancellationToken.None)).Revision, Is.EqualTo(1));
        });
        var location = same.Response.Headers.Location.ToString();
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query(location[(location.IndexOf('?') + 1)..])), Is.True);
    }

    [Test]
    public async Task Endpoint_ResetWhenAlreadyDefault_IsUnchanged()
    {
        var (_, sessions, store, session) = await CreateAsync();
        var reset = await SaveAsync(sessions, store, session.Dosid, "language=es&sound=true&notifications=true&rev=0");
        Assert.That(reset.Response.Headers.Location.ToString(), Is.EqualTo(LocalAccountSettingsAction.UnchangedLocation));
        Assert.That(await store.GetStateAsync(session.AccountId, CancellationToken.None),
            Is.EqualTo((LocalAccountPreferencesStore.Default, 0)));
    }

    [TestCase("action=internalAccount&unchanged=1", true)]
    [TestCase("action=internalAccount&from=internalStart&unchanged=1", true)]
    [TestCase("action=internalAccount&unchanged=0", false)]
    [TestCase("action=internalAccount&unchanged=", false)]
    [TestCase("action=internalAccount&unchanged=true", false)]
    [TestCase("action=internalAccount&unchanged=01", false)]
    [TestCase("action=internalAccount&unchanged=1&unchanged=1", false)]
    [TestCase("action=internalAccount&UNCHANGED=1", false)]
    [TestCase("action=internalAccount&unchanged=1&saved=1", false)]
    [TestCase("action=internalAccount&saved=2&unchanged=1", false)]
    public void PageQuery_UnchangedFlag_IsStrict(string query, bool valid) =>
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query(query)), Is.EqualTo(valid));

    [Test]
    public void Page_ShowsUnchangedNotice_OnlyWhenFlagged()
    {
        var preferences = new LocalAccountPreferences("de", false, true);
        var flagged = LocalAccountSettings.Page(PageSession, preferences, 3, null, unchanged: true);
        var plain = LocalAccountSettings.Page(PageSession, preferences, 3);
        Assert.Multiple(() =>
        {
            Assert.That(flagged, Does.Contain("<p id=\"accountUnchanged\" role=\"status\" data-rev=\"3\">No changes to save."));
            Assert.That(flagged, Does.Not.Contain("id=\"accountSaved\""));
            Assert.That(flagged, Does.Contain("Saved 3 times"));
            Assert.That(plain, Does.Not.Contain("accountUnchanged"));
            Assert.That(LocalAccountSettingsAction.IsUnchangedNotice(Query("action=internalAccount&unchanged=1")), Is.True);
            Assert.That(LocalAccountSettingsAction.IsUnchangedNotice(Query("action=internalAccount")), Is.False);
        });
    }

    private static async Task<(IDistributedCache Cache, GameAuthSessionStore Sessions, LocalAccountPreferencesStore Store,
        GameAuthSession Session)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (cache, sessions, new LocalAccountPreferencesStore(cache), session);
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
