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

// Menu-91: drives the Messages send/delete/friend POST handlers (EconomyStore-backed LocalMessagingStore)
// against migrated Postgres. Mailbox, friends, and revision live in economy.messaging_states, form keys are
// single-use across replicas (messaging_actions), and the account advisory lock serializes submissions.
[TestFixture]
[Category("Integration")]
public sealed class LocalMessagingPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 91_000;

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
    public async Task NewPilot_SeesSeededInboxWithoutRow()
    {
        var harness = await HarnessAsync();

        var state = await harness.Messaging.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Messages.Select(message => message.Subject),
                Is.EquivalentTo(new[] { "Welcome to DarkOrbit", "Server maintenance", "Galaxy Gates" }));
            Assert.That(state.Friends, Is.Empty);
            Assert.That(state.Revision, Is.Zero);
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    // Menu-112: opening a message persists its read flag at the same revision; "Mark all read" is a revisioned action.
    [Test]
    public async Task MarkRead_PersistsWithoutRevision_AndMarkAllReadBumpsIt()
    {
        var harness = await HarnessAsync();
        var account = harness.Session.AccountId;

        Assert.That(await harness.Messaging.MarkReadAsync(account, 1, Ct), Is.EqualTo(MessagingChange.Applied));
        var afterOpen = await RowAsync(harness);
        Assert.That(await NewStore(harness).MarkAllReadAsync(account, 0, "read-all-key", Ct), Is.EqualTo(MessagingChange.Applied));
        var fresh = await NewStore(harness).GetAsync(account, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(afterOpen?.Revision, Is.Zero);
            Assert.That(afterOpen?.State.Messages.Count(LocalMessagingStore.IsUnread), Is.EqualTo(2));
            Assert.That(fresh.Revision, Is.EqualTo(1));
            Assert.That(fresh.Messages.Any(LocalMessagingStore.IsUnread), Is.False);
            Assert.That(await harness.Messaging.MarkAllReadAsync(account, 1, "read-all-key-2", Ct),
                Is.EqualTo(MessagingChange.Unchanged));
        });
    }

    [Test]
    public async Task AddFriend_PersistsStateRevisionAndKey_VisibleToFreshReplica()
    {
        var harness = await HarnessAsync();

        var context = await FriendAsync(harness, harness.Messaging, "internalFriendAdd", 1001, 0);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
        var row = await RowAsync(harness);
        var fresh = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(async () =>
        {
            Assert.That(row?.Revision, Is.EqualTo(1));
            Assert.That(row?.State.Friends, Is.EqualTo(new[] { 1001 }));
            Assert.That(row?.State.Messages, Has.Count.EqualTo(3), "the seeded inbox is persisted with the first change");
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
            Assert.That(fresh.Friends, Is.EqualTo(new[] { 1001 }));
            Assert.That(fresh.Revision, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Send_ToFriendPersistsSentMail_NonFriendIsRefused()
    {
        var harness = await HarnessAsync();
        Assert.That((await FriendAsync(harness, harness.Messaging, "internalFriendAdd", 1003, 0)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var stranger = await SendAsync(harness, NewStore(harness), 1005, "Hello", "Not a friend", 1);
        var sent = await SendAsync(harness, NewStore(harness), 1003, "Patrol", "Meet at 1-1 <b>now</b>", 1);

        Assert.That(stranger.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(stranger));
        Assert.That(sent.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(sent));
        var fresh = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        var mail = fresh.Messages.Single(message => message.Sent);
        Assert.Multiple(async () =>
        {
            Assert.That(mail.ToPilot, Is.EqualTo(1003));
            Assert.That(mail.Subject, Is.EqualTo("Patrol"));
            Assert.That(mail.Body, Is.EqualTo("Meet at 1-1 <b>now</b>"));
            Assert.That(mail.Id, Is.EqualTo(4));
            Assert.That(fresh.NextId, Is.EqualTo(5));
            Assert.That(fresh.Revision, Is.EqualTo(2));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(2), "the refused send records no key");
        });
    }

    [Test]
    public async Task ReplayedKeyAndStaleRevision_AreConflictsAcrossReplicas()
    {
        var harness = await HarnessAsync();
        var form = FriendForm(harness, "internalFriendAdd", 1002, 0);
        Assert.That((await PostAsync(harness, harness.Messaging, "internalFriendAdd", form)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        Assert.That((await FriendAsync(harness, harness.Messaging, "internalFriendRemove", 1002, 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        // After the remove, only the stored key stops a replay of the add form on another replica.
        var replay = await PostAsync(harness, NewStore(harness), "internalFriendAdd", form);
        var stale = await FriendAsync(harness, NewStore(harness), "internalFriendAdd", 1004, 1);

        Assert.Multiple(async () =>
        {
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            var row = await RowAsync(harness);
            Assert.That(row?.Revision, Is.EqualTo(2));
            Assert.That(row?.State.Friends, Is.Empty);
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ConcurrentSubmissions_ThroughSeparateReplicas_ApplyOnce()
    {
        var harness = await HarnessAsync();

        var results = await Task.WhenAll(new[] { 1001, 1002, 1003, 1004 }.Select(pilot =>
            FriendAsync(harness, NewStore(harness), "internalFriendAdd", pilot, 0)));

        Assert.Multiple(async () =>
        {
            Assert.That(results.Count(context => context.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(context => context.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            var row = await RowAsync(harness);
            Assert.That(row?.Revision, Is.EqualTo(1));
            Assert.That(row?.State.Friends, Has.Count.EqualTo(1));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DeleteMessage_PersistsAndUnknownIdIsNotFound()
    {
        var harness = await HarnessAsync();

        var deleted = await DeleteAsync(harness, harness.Messaging, 2, 0);
        var again = await DeleteAsync(harness, NewStore(harness), 2, 1);

        Assert.That(deleted.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(deleted));
        Assert.That(again.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(again));
        var row = await RowAsync(harness);
        Assert.Multiple(() =>
        {
            Assert.That(row?.State.Messages.Select(message => message.Id), Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(row?.Revision, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RemovingNonFriendOrAddingUnknownPilot_SavesNothing()
    {
        var harness = await HarnessAsync();

        var notFriend = await FriendAsync(harness, harness.Messaging, "internalFriendRemove", 1006, 0);
        var unknown = await FriendAsync(harness, harness.Messaging, "internalFriendAdd", 4242, 0);

        Assert.Multiple(async () =>
        {
            Assert.That(notFriend.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(notFriend));
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(unknown));
            Assert.That(await RowAsync(harness), Is.Null);
            Assert.That(await ActionCountAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task CacheEraState_IsImportedOnceAndRemovedFromCache()
    {
        var harness = await HarnessAsync();
        var legacy = new LocalMessagingState(
            [new LocalMailMessage(7, LocalMessagingStore.SystemSender, 0, "Old notice", "Kept", Start.AddDays(-5))],
            [1001, 1001, 9999], NextId: 8, Revision: 4);
        await harness.Cache.SetStringAsync(CacheKey(harness), JsonSerializer.Serialize(legacy), Ct);

        var state = await harness.Messaging.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Messages.Select(message => message.Id), Is.EqualTo(new[] { 7 }));
            Assert.That(state.Friends, Is.EqualTo(new[] { 1001 }), "duplicates and unknown pilots are dropped");
            Assert.That(state.NextId, Is.EqualTo(8));
            Assert.That(state.Revision, Is.EqualTo(4));
            Assert.That(await harness.Cache.GetStringAsync(CacheKey(harness), Ct), Is.Null);
            Assert.That((await RowAsync(harness))?.Revision, Is.EqualTo(4));
        });

        // A later cache entry never overwrites the database row.
        await harness.Cache.SetStringAsync(CacheKey(harness),
            JsonSerializer.Serialize(legacy with { Friends = [1002], Revision = 40 }), Ct);
        var after = await harness.Messaging.GetAsync(harness.Session.AccountId, Ct);
        Assert.That(after.Friends, Is.EqualTo(new[] { 1001 }));
        Assert.That(after.Revision, Is.EqualTo(4));
    }

    [Test]
    public async Task HandEditedRow_IsSanitizedWhenRead()
    {
        var harness = await HarnessAsync();
        var edited = new LocalMessagingState(
        [
            new LocalMailMessage(5, LocalMessagingStore.SystemSender, 0, "Keep", "ok", Start),
            new LocalMailMessage(5, LocalMessagingStore.SystemSender, 0, "Duplicate", "dup", Start.AddMinutes(-1)),
            new LocalMailMessage(-3, LocalMessagingStore.SystemSender, 0, "Negative", "bad", Start),
            new LocalMailMessage(6, 4242, 0, "Unknown sender", "bad", Start),
            new LocalMailMessage(8, LocalMessagingStore.SystemSender, 0, "", "empty subject", Start)
        ], [1001, 4242, 1001], NextId: 1);
        await InsertRowAsync(harness, JsonSerializer.Serialize(edited), 3);

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(state.Messages.Select(message => message.Subject), Is.EqualTo(new[] { "Keep" }));
            Assert.That(state.Friends, Is.EqualTo(new[] { 1001 }));
            Assert.That(state.NextId, Is.EqualTo(6));
            Assert.That(state.Revision, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task UnreadableRowJson_FallsBackToEmptyMailboxAndKeepsRevision()
    {
        var harness = await HarnessAsync();
        await InsertRowAsync(harness, "{\"Messages\":\"oops\"}", 2);

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        var add = await FriendAsync(harness, harness.Messaging, "internalFriendAdd", 1001, 2);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Messages, Is.Empty);
            Assert.That(state.Revision, Is.EqualTo(2));
            Assert.That(add.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(add));
            Assert.That((await RowAsync(harness))?.State.Friends, Is.EqualTo(new[] { 1001 }));
        });
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalMessagingStore Messaging);

    private sealed record MessagingRow(LocalMessagingState State, int Revision);

    private static string CacheKey(Harness harness) => $"localmessaging:{harness.Session.AccountId:N}";

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalMessagingStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"mail-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"mail-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalMessagingStore(cache, economy));
    }

    private static string FormKey(Harness harness, string action, string target, int rev) =>
        Uri.EscapeDataString(LocalMessagingAction.CreateFormKey(harness.Session.Dosid, action, target, rev, Start));

    private static string FriendForm(Harness harness, string action, int pilot, int rev) =>
        $"pilot={pilot}&rev={rev}&key={FormKey(harness, action, pilot.ToString(), rev)}";

    private static Task<DefaultHttpContext> FriendAsync(Harness harness, LocalMessagingStore store, string action, int pilot, int rev) =>
        PostAsync(harness, store, action, FriendForm(harness, action, pilot, rev));

    private static Task<DefaultHttpContext> DeleteAsync(Harness harness, LocalMessagingStore store, int message, int rev) =>
        PostAsync(harness, store, "internalMessageDelete",
            $"message={message}&rev={rev}&key={FormKey(harness, "internalMessageDelete", message.ToString(), rev)}");

    private static Task<DefaultHttpContext> SendAsync(Harness harness, LocalMessagingStore store, int to, string subject,
        string body, int rev) =>
        PostAsync(harness, store, "internalMessageSend",
            $"to={to}&subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}&rev={rev}&key={FormKey(harness, "internalMessageSend", "", rev)}");

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalMessagingStore store, string action, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalMessagingAction.HandleAsync(context, harness.Sessions, store, new FixedClock(Start), Ct);
        return context;
    }

    private async Task<MessagingRow?> RowAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT state::text, revision FROM economy.messaging_states WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new MessagingRow(JsonSerializer.Deserialize<LocalMessagingState>(reader.GetString(0))!, reader.GetInt32(1));
    }

    private async Task InsertRowAsync(Harness harness, string json, int revision)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.messaging_states(account_id,state,revision) VALUES (@a,@s::jsonb,@v)", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("s", json);
        cmd.Parameters.AddWithValue("v", revision);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ActionCountAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM economy.messaging_actions WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
