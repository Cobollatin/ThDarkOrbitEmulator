using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Menu-92: drives the Account center save POST (EconomyStore-backed LocalAccountPreferencesStore) against
// migrated Postgres. Language, sound, notifications, and revision live in economy.account_preferences, and the
// account advisory lock makes one revision apply once across replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalAccountPreferencesPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 92_000;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await _postgres.StartAsync(ct);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = _postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(_config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _postgres.DisposeAsync();

    [Test]
    public async Task NewPilot_SeesDefaultsWithoutRow()
    {
        var harness = await HarnessAsync();

        var (preferences, revision) = await harness.Store.GetStateAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(preferences, Is.EqualTo(LocalAccountPreferencesStore.Default));
            Assert.That(revision, Is.Zero);
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Save_PersistsRow_VisibleToFreshReplica()
    {
        var harness = await HarnessAsync();

        var context = await SaveAsync(harness, harness.Store, "language=de&sound=false&notifications=true&rev=0");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
        var (fresh, revision) = await NewStore(harness).GetStateAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(async () =>
        {
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("de", false, true, 1)));
            Assert.That(fresh, Is.EqualTo(new LocalAccountPreferences("de", false, true)));
            Assert.That(revision, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DefaultSave_DoesNotCreateRow_OrAdvanceRevision()
    {
        var harness = await HarnessAsync();

        var context = await SaveAsync(harness, harness.Store,
            "language=es&sound=true&notifications=true&rev=0");

        Assert.Multiple(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo(LocalAccountSettingsAction.UnchangedLocation));
            Assert.That(await RowAsync(harness), Is.Null);
            Assert.That(await NewStore(harness).GetStateAsync(harness.Session.AccountId, Ct),
                Is.EqualTo((LocalAccountPreferencesStore.Default, 0)));
        });
    }

    [Test]
    public async Task SavedValuesOnFreshReplica_AreUnchanged_WhileStaleRevisionConflicts()
    {
        var harness = await HarnessAsync();
        const string values = "language=de&sound=false&notifications=true";
        var first = await SaveAsync(harness, harness.Store, values + "&rev=0");
        var same = await SaveAsync(harness, NewStore(harness), values + "&rev=1");
        var legacySame = await SaveAsync(harness, NewStore(harness), values);
        var stale = await SaveAsync(harness, NewStore(harness), values + "&rev=0");

        Assert.Multiple(async () =>
        {
            Assert.That(first.Response.Headers.Location.ToString(),
                Is.EqualTo(LocalAccountSettingsAction.SavedLocation(1)));
            foreach (var context in new[] { same, legacySame })
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
                Assert.That(context.Response.Headers.Location.ToString(),
                    Is.EqualTo(LocalAccountSettingsAction.UnchangedLocation));
            }
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("de", false, true, 1)));
        });
    }

    [Test]
    public async Task ConcurrentIdenticalSaves_ThroughSeparateReplicas_AdvanceOnce()
    {
        var harness = await HarnessAsync();
        var contexts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            SaveAsync(harness, NewStore(harness), "language=it&sound=false&notifications=false&rev=0")));

        Assert.Multiple(async () =>
        {
            Assert.That(contexts.Count(context => context.Response.StatusCode == StatusCodes.Status303SeeOther),
                Is.EqualTo(1));
            Assert.That(contexts.Count(context => context.Response.StatusCode == StatusCodes.Status409Conflict),
                Is.EqualTo(3));
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("it", false, false, 1)));
        });
    }

    [Test]
    public async Task ReplayedOrStaleRevision_IsConflictOnAnotherReplica()
    {
        var harness = await HarnessAsync();
        const string form = "language=fr&sound=true&notifications=false&rev=0";
        Assert.That((await SaveAsync(harness, harness.Store, form)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var replay = await SaveAsync(harness, NewStore(harness), form);
        var stale = await SaveAsync(harness, NewStore(harness), "language=pl&sound=false&notifications=false&rev=0");

        Assert.Multiple(async () =>
        {
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("fr", true, false, 1)));
        });
    }

    [Test]
    public async Task ConcurrentSaves_ThroughSeparateReplicas_ApplyOnce()
    {
        var harness = await HarnessAsync();
        string[] languages = ["en", "de", "it", "tr"];

        var contexts = await Task.WhenAll(languages.Select(language =>
            SaveAsync(harness, NewStore(harness), $"language={language}&sound=false&notifications=false&rev=0")));

        var statuses = contexts.Select(context => context.Response.StatusCode).ToArray();
        var row = await RowAsync(harness);
        Assert.Multiple(() =>
        {
            Assert.That(statuses.Count(status => status == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(statuses.Count(status => status == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(row?.Revision, Is.EqualTo(1));
            Assert.That(languages, Does.Contain(row?.Language));
        });
    }

    [Test]
    public async Task SaveWithoutRev_StillAppliesAndAdvancesRevision()
    {
        var harness = await HarnessAsync();
        Assert.That((await SaveAsync(harness, harness.Store, "language=en&sound=true&notifications=true&rev=0"))
            .Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));

        var legacy = await SaveAsync(harness, NewStore(harness), "language=pt&sound=false&notifications=true");

        Assert.That(legacy.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(legacy));
        Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("pt", false, true, 2)));
    }

    [Test]
    public async Task InvalidValueOrRevision_SavesNothing()
    {
        var harness = await HarnessAsync();

        var invalid = await SaveAsync(harness, harness.Store, "language=xx&sound=true&notifications=true&rev=0");
        var badRev = await SaveAsync(harness, harness.Store, "language=en&sound=true&notifications=true&rev=-1");

        Assert.Multiple(async () =>
        {
            Assert.That(invalid.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(invalid));
            Assert.That(badRev.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(badRev));
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task RevisionAtMaximum_WrapsToOne()
    {
        var harness = await HarnessAsync();
        await InsertRowAsync(harness, "es", true, true, LocalAccountPreferencesStore.MaxRevision);

        var context = await SaveAsync(harness, NewStore(harness),
            $"language=en&sound=true&notifications=true&rev={LocalAccountPreferencesStore.MaxRevision}");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("en", true, true, 1)));
    }

    [Test]
    public async Task CachedPreferences_AreImportedOnce_AndNeverOverwriteRow()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync(CacheKey(harness), JsonSerializer.Serialize(new
        { Language = "it", SoundEnabled = false, NotificationsEnabled = false, Revision = 4 }), Ct);

        var imported = await harness.Store.GetStateAsync(harness.Session.AccountId, Ct);
        var cacheAfterImport = await harness.Cache.GetStringAsync(CacheKey(harness), Ct);

        // A second, conflicting cache entry must not replace the database row.
        await harness.Cache.SetStringAsync(CacheKey(harness), JsonSerializer.Serialize(new
        { Language = "tr", SoundEnabled = true, NotificationsEnabled = true, Revision = 9 }), Ct);
        var again = await harness.Store.GetStateAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(imported, Is.EqualTo((new LocalAccountPreferences("it", false, false), 4)));
            Assert.That(cacheAfterImport, Is.Null);
            Assert.That(again, Is.EqualTo(imported));
            Assert.That(await harness.Cache.GetStringAsync(CacheKey(harness), Ct), Is.Null);
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("it", false, false, 4)));
        });
    }

    [Test]
    public async Task CachedRevision_IsHonouredByTheFirstPostgresSave()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync(CacheKey(harness), JsonSerializer.Serialize(new
        { Language = "de", SoundEnabled = true, NotificationsEnabled = false, Revision = 3 }), Ct);

        var stale = await SaveAsync(harness, harness.Store, "language=en&sound=true&notifications=true&rev=0");
        var current = await SaveAsync(harness, harness.Store, "language=en&sound=true&notifications=true&rev=3");

        Assert.Multiple(async () =>
        {
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            Assert.That(current.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(current));
            Assert.That(await RowAsync(harness), Is.EqualTo(new PreferencesRow("en", true, true, 4)));
        });
    }

    [Test]
    public async Task HandEditedRow_UnknownLanguageFallsBackToDefault()
    {
        var harness = await HarnessAsync();
        await InsertRowAsync(harness, "zz", false, true, 7);

        var (preferences, revision) = await NewStore(harness).GetStateAsync(harness.Session.AccountId, Ct);
        var page = LocalAccountSettings.Page(harness.Session, preferences, revision);

        Assert.Multiple(() =>
        {
            Assert.That(preferences, Is.EqualTo(new LocalAccountPreferences("es", false, true)));
            Assert.That(revision, Is.EqualTo(7));
            Assert.That(page, Does.Contain("<html lang=\"es\">"));
            Assert.That(page, Does.Contain("Saved 7 times"));
        });
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalAccountPreferencesStore Store);

    private sealed record PreferencesRow(string Language, bool Sound, bool Notifications, int Revision);

    private static string CacheKey(Harness harness) => $"localaccountprefs:{harness.Session.AccountId:N}";

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalAccountPreferencesStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

    private async Task<Harness> HarnessAsync()
    {
        var gameUserId = Interlocked.Increment(ref _nextGameUserId);
        var accountId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,@g)", db);
            insert.Parameters.AddWithValue("a", accountId);
            insert.Parameters.AddWithValue("u", $"account-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"account-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalAccountPreferencesStore(cache, economy));
    }

    private static async Task<DefaultHttpContext> SaveAsync(Harness harness, LocalAccountPreferencesStore store, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalAccountSave");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalAccountSettingsAction.HandleAsync(context, harness.Sessions, store, Ct);
        return context;
    }

    private async Task<PreferencesRow?> RowAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT language, sound_enabled, notifications_enabled, revision FROM economy.account_preferences WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new PreferencesRow(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt32(3));
    }

    private async Task InsertRowAsync(Harness harness, string language, bool sound, bool notifications, int revision)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.account_preferences(account_id,language,sound_enabled,notifications_enabled,revision) VALUES (@a,@l,@s,@n,@v)", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("l", language);
        cmd.Parameters.AddWithValue("s", sound);
        cmd.Parameters.AddWithValue("n", notifications);
        cmd.Parameters.AddWithValue("v", revision);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
