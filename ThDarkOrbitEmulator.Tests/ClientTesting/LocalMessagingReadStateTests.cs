using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-112: read/unread state, mark-on-open, and the "Mark all read" action (all emulator-defined).
public class LocalMessagingReadStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private const string ReadAll = "internalMessageReadAll";

    [Test]
    public async Task SeededInbox_IsUnread_AndPageShowsCountMarkersAndMarkAllForm()
    {
        var (_, store, session, _) = await SetupAsync();
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        var page = LocalMessaging.Page(session, state, null, Now);
        Assert.Multiple(() =>
        {
            Assert.That(state.Messages.All(LocalMessagingStore.IsUnread), Is.True);
            Assert.That(page, Does.Contain("Inbox (3)"));
            Assert.That(page, Does.Contain("<span id=\"unreadCount\">3 unread</span>"));
            Assert.That(CountOf(page, "class=\"unread\""), Is.EqualTo(3));
            Assert.That(page, Does.Contain("<strong>Welcome to DarkOrbit</strong> (new)"));
            Assert.That(page, Does.Contain($"action={ReadAll}"));
            Assert.That(page, Does.Contain("Mark all read"));
        });
    }

    [Test]
    public async Task MarkRead_MarksOneMessage_KeepsRevision_AndIsIdempotent()
    {
        var (_, store, session, _) = await SetupAsync();
        var id = session.AccountId;
        Assert.That(await store.MarkReadAsync(id, 2, CancellationToken.None), Is.EqualTo(MessagingChange.Applied));
        var state = await store.GetAsync(id, Now, CancellationToken.None);
        Assert.Multiple(async () =>
        {
            Assert.That(state.Revision, Is.Zero);
            Assert.That(state.Messages.Single(message => message.Id == 2).Read, Is.True);
            Assert.That(state.Messages.Count(LocalMessagingStore.IsUnread), Is.EqualTo(2));
            Assert.That(await store.MarkReadAsync(id, 2, CancellationToken.None), Is.EqualTo(MessagingChange.Unchanged));
            Assert.That(await store.MarkReadAsync(id, 99, CancellationToken.None), Is.EqualTo(MessagingChange.NotFound));
            Assert.That(await store.MarkReadAsync(Guid.Empty, 1, CancellationToken.None), Is.EqualTo(MessagingChange.Invalid));
        });
        var page = LocalMessaging.Page(session, state, 2, Now);
        Assert.That(page, Does.Contain("<span id=\"unreadCount\">2 unread</span>"));
        Assert.That(page, Does.Not.Contain("<strong>Server maintenance</strong>"));
    }

    [Test]
    public async Task SentMail_IsNeverUnread()
    {
        var (_, store, session, _) = await SetupAsync();
        await store.AddFriendAsync(session.AccountId, 1001, null, null, CancellationToken.None);
        await store.SendAsync(session.AccountId, 1001, "Out", "Body", Now, null, null, CancellationToken.None);
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        var sent = state.Messages.Single(message => message.Sent);
        Assert.Multiple(async () =>
        {
            Assert.That(LocalMessagingStore.IsUnread(sent), Is.False);
            Assert.That(await store.MarkReadAsync(session.AccountId, sent.Id, CancellationToken.None),
                Is.EqualTo(MessagingChange.Unchanged));
            Assert.That(LocalMessaging.Page(session, state, null, Now, "sent"), Does.Not.Contain("class=\"unread\""));
        });
    }

    [Test]
    public async Task MarkRead_DoesNotInvalidatePendingForms()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var body = PilotBody(session.Dosid, "internalFriendAdd", "1001", 0);
        await store.MarkReadAsync(session.AccountId, 1, CancellationToken.None);
        var context = Request(session.Dosid, "internalFriendAdd", body);
        await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task MarkAllRead_ClearsUnread_BumpsRevision_AndRejectsReplayAndNoOp()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var key = ReadAllBody(session.Dosid, 0);
        var first = await HandleAsync(sessions, store, session.Dosid, key);
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        var replay = await HandleAsync(sessions, store, session.Dosid, key);
        var noOp = await HandleAsync(sessions, store, session.Dosid, ReadAllBody(session.Dosid, 1));
        var page = LocalMessaging.Page(session, state, null, Now);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalMessages&readall=1"));
            Assert.That(state.Revision, Is.EqualTo(1));
            Assert.That(state.Messages.Any(LocalMessagingStore.IsUnread), Is.False);
            Assert.That(state.Messages, Has.Count.EqualTo(3));
            Assert.That(page, Does.Not.Contain("unreadCount"));
            Assert.That(page, Does.Not.Contain($"action={ReadAll}"));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(noOp.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(noOp), Does.Contain("There are no unread messages."));
        });
    }

    [Test]
    public async Task MarkAllRead_RejectsBadRequestsWithoutChangingState()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var valid = ReadAllBody(session.Dosid, 0);
        var get = Request(session.Dosid, ReadAll, valid);
        get.Request.Method = "GET";
        await LocalMessagingAction.HandleAsync(get, sessions, store, new TestClock(Now), CancellationToken.None);
        var statuses = new[]
        {
            (await HandleAsync(sessions, store, null, valid)).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid, valid + "&message=1")).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid, valid + "&rev=0")).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid, "rev=0")).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid,
                $"rev=0&key={LocalMessagingAction.CreateFormKey(session.Dosid, "internalMessageDelete", "", 0, Now)}")).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid,
                $"rev=0&key={LocalMessagingAction.CreateFormKey("other-session", ReadAll, "", 0, Now)}")).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid, ReadAllBody(session.Dosid, 0, Now.AddHours(-2)))).Response.StatusCode,
            (await HandleAsync(sessions, store, session.Dosid, ReadAllBody(session.Dosid, 5))).Response.StatusCode
        };
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(get.Response.StatusCode, Is.EqualTo(405));
            Assert.That(get.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
            Assert.That(statuses, Is.EqualTo(new[] { 401, 400, 400, 400, 400, 400, 409, 409 }));
            Assert.That(state.Revision, Is.Zero);
            Assert.That(state.Messages.Count(LocalMessagingStore.IsUnread), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ConcurrentMarkAllRead_AppliesOnce()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            HandleAsync(sessions, store, session.Dosid, ReadAllBody(session.Dosid, 0))));
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(results.Count(context => context.Response.StatusCode == 303), Is.EqualTo(1));
            Assert.That(results.Count(context => context.Response.StatusCode == 409), Is.EqualTo(7));
            Assert.That(state.Revision, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LegacyCachedMailbox_WithoutReadFlag_LoadsReceivedAsUnreadAndSentAsRead()
    {
        var (_, store, session, cache) = await SetupAsync();
        await cache.SetStringAsync($"localmessaging:{session.AccountId:N}",
            "{\"Messages\":[{\"Id\":1,\"FromPilot\":0,\"ToPilot\":0,\"Subject\":\"Old\",\"Body\":\"b\",\"SentAt\":\"2026-10-20T00:00:00+00:00\",\"Sent\":false}," +
            "{\"Id\":2,\"FromPilot\":0,\"ToPilot\":1001,\"Subject\":\"Out\",\"Body\":\"b\",\"SentAt\":\"2026-10-21T00:00:00+00:00\",\"Sent\":true}]," +
            "\"Friends\":[1001],\"NextId\":3,\"Revision\":4}");
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(LocalMessagingStore.IsUnread(state.Messages.Single(message => message.Id == 1)), Is.True);
            Assert.That(LocalMessagingStore.IsUnread(state.Messages.Single(message => message.Id == 2)), Is.False);
            Assert.That(state.Revision, Is.EqualTo(4));
        });
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

    private static async Task<DefaultHttpContext> HandleAsync(GameAuthSessionStore sessions, LocalMessagingStore store,
        string? dosid, string body)
    {
        var context = Request(dosid, ReadAll, body);
        await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        return context;
    }

    private static async Task<(GameAuthSessionStore, LocalMessagingStore, GameAuthSession, IDistributedCache)> SetupAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!;
        return (sessions, new LocalMessagingStore(cache), session, cache);
    }

    private static string ReadAllBody(string dosid, int rev, DateTimeOffset? issued = null) =>
        $"rev={rev}&key={LocalMessagingAction.CreateFormKey(dosid, ReadAll, "", rev, issued ?? Now)}";

    private static string PilotBody(string dosid, string action, string pilot, int rev) =>
        $"pilot={pilot}&rev={rev}&key={LocalMessagingAction.CreateFormKey(dosid, action, pilot, rev, Now)}";

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static DefaultHttpContext Request(string? dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
