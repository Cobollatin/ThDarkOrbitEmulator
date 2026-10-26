using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMessagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task NewInbox_IsSeededWithSystemNotices_AndAccountsAreIsolated()
    {
        var (_, store, session, _) = await SetupAsync();
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Messages, Has.Count.EqualTo(3));
            Assert.That(state.Messages.All(message => message.FromPilot == LocalMessagingStore.SystemSender), Is.True);
            Assert.That(state.Friends, Is.Empty);
            Assert.That(state.NextId, Is.EqualTo(4));
        });
        Assert.That(await store.AddFriendAsync(session.AccountId, 1001, 0, null, CancellationToken.None),
            Is.EqualTo(MessagingChange.Applied));
        Assert.That((await store.GetAsync(Guid.NewGuid(), Now, CancellationToken.None)).Friends, Is.Empty);
    }

    [Test]
    public async Task Store_EnforcesFriendRulesAndRevision()
    {
        var (_, store, session, _) = await SetupAsync();
        var id = session.AccountId;
        Assert.That(await store.AddFriendAsync(id, 9999, 0, null, CancellationToken.None), Is.EqualTo(MessagingChange.NotFound));
        Assert.That(await store.AddFriendAsync(id, 1001, 0, null, CancellationToken.None), Is.EqualTo(MessagingChange.Applied));
        Assert.That(await store.AddFriendAsync(id, 1001, 1, null, CancellationToken.None), Is.EqualTo(MessagingChange.Conflict));
        Assert.That(await store.AddFriendAsync(id, 1002, 0, null, CancellationToken.None), Is.EqualTo(MessagingChange.Conflict));
        Assert.That(await store.SendAsync(id, 1003, "hi", "", Now, 1, null, CancellationToken.None), Is.EqualTo(MessagingChange.Invalid));
        Assert.That(await store.SendAsync(id, 1001, "hi", "body", Now, 1, null, CancellationToken.None), Is.EqualTo(MessagingChange.Applied));
        Assert.That(await store.RemoveFriendAsync(id, 1002, 2, null, CancellationToken.None), Is.EqualTo(MessagingChange.Conflict));
        Assert.That(await store.RemoveFriendAsync(id, 1001, 2, null, CancellationToken.None), Is.EqualTo(MessagingChange.Applied));
        Assert.That(await store.DeleteMessageAsync(id, 4242, 3, null, CancellationToken.None), Is.EqualTo(MessagingChange.NotFound));
        var state = await store.GetAsync(id, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Revision, Is.EqualTo(3));
            Assert.That(state.Friends, Is.Empty);
            Assert.That(state.Messages.Single(message => message.Sent).ToPilot, Is.EqualTo(1001));
        });
    }

    [Test]
    public async Task Mailbox_IsCappedAndCorruptCacheIsSanitized()
    {
        var (_, store, session, cache) = await SetupAsync();
        var id = session.AccountId;
        await store.AddFriendAsync(id, 1001, null, null, CancellationToken.None);
        for (var i = 0; i < LocalMessagingStore.MaxMessages + 5; i++)
            Assert.That(await store.SendAsync(id, 1001, $"m{i}", "", Now.AddSeconds(i), null, null, CancellationToken.None),
                Is.EqualTo(MessagingChange.Applied));
        var state = await store.GetAsync(id, Now, CancellationToken.None);
        Assert.That(state.Messages, Has.Count.EqualTo(LocalMessagingStore.MaxMessages));
        Assert.That(state.Messages.Select(message => message.Id).Distinct().Count(), Is.EqualTo(LocalMessagingStore.MaxMessages));

        await cache.SetStringAsync($"localmessaging:{id:N}",
            """{"Messages":[{"Id":5,"FromPilot":0,"ToPilot":0,"Subject":"a","Body":"","SentAt":"2026-01-01T00:00:00+00:00"},{"Id":5,"FromPilot":0,"ToPilot":0,"Subject":"dup","Body":"","SentAt":"2026-01-01T00:00:00+00:00"},{"Id":-1,"FromPilot":0,"ToPilot":0,"Subject":"neg","Body":"","SentAt":"2026-01-01T00:00:00+00:00"},{"Id":7,"FromPilot":31337,"ToPilot":0,"Subject":"x","Body":"","SentAt":"2026-01-01T00:00:00+00:00"}],"Friends":[1001,1001,31337],"NextId":-4,"Revision":-9}""");
        state = await store.GetAsync(id, Now, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Messages.Select(message => message.Id), Is.EqualTo(new[] { 5 }));
            Assert.That(state.Friends, Is.EqualTo(new[] { 1001 }));
            Assert.That(state.NextId, Is.EqualTo(6));
            Assert.That(state.Revision, Is.Zero);
        });
        await cache.SetStringAsync($"localmessaging:{id:N}", "{not json");
        Assert.That((await store.GetAsync(id, Now, CancellationToken.None)).Messages, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task FriendList_IsBoundedByDirectoryAndLimit()
    {
        var (_, store, session, _) = await SetupAsync();
        foreach (var pilot in LocalMessagingStore.Directory)
            Assert.That(await store.AddFriendAsync(session.AccountId, pilot.Id, null, null, CancellationToken.None),
                Is.EqualTo(MessagingChange.Applied));
        Assert.That(LocalMessagingStore.Directory.Count, Is.LessThanOrEqualTo(LocalMessagingStore.MaxFriends));
        Assert.That((await store.GetAsync(session.AccountId, Now, CancellationToken.None)).Friends,
            Has.Count.EqualTo(LocalMessagingStore.Directory.Count));

    }

    [Test]
    public async Task ConcurrentSubmissionsOfOneForm_ApplyOnce()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var body = PilotBody(session.Dosid, "internalFriendAdd", "1001", 0);
        var contexts = Enumerable.Range(0, 8).Select(_ => Request(session.Dosid, "internalFriendAdd", body)).ToArray();
        await Task.WhenAll(contexts.Select(context =>
            LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None)));
        Assert.Multiple(() =>
        {
            Assert.That(contexts.Count(context => context.Response.StatusCode == 303), Is.EqualTo(1));
            Assert.That(contexts.Count(context => context.Response.StatusCode == 409), Is.EqualTo(7));
        });
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        Assert.That(state.Friends, Is.EqualTo(new[] { 1001 }));
        Assert.That(state.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Actions_RequireSessionAndCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalMessagingStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var body = PilotBody(session.Dosid, "internalFriendAdd", "1001", 0);
        var anonymous = Request(null, "internalFriendAdd", body);
        await LocalMessagingAction.HandleAsync(anonymous, sessions, store, new TestClock(Now), CancellationToken.None);
        var expired = Request("0123456789abcdef0123456789abcdef", "internalFriendAdd", body);
        await LocalMessagingAction.HandleAsync(expired, sessions, store, new TestClock(Now), CancellationToken.None);
        var noCompany = Request(session.Dosid, "internalFriendAdd", body);
        await LocalMessagingAction.HandleAsync(noCompany, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
            Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
            Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
            Assert.That(noCompany.Response.ContentType, Does.StartWith("text/html"));
        });
        Assert.That((await store.GetAsync(session.AccountId, Now, CancellationToken.None)).Friends, Is.Empty);
    }

    [TestCase("pilot=1001&rev=0", 400, TestName = "Missing key")]
    [TestCase("pilot=1001&pilot=1002&rev=0&key={KEY}", 400, TestName = "Repeated field")]
    [TestCase("pilot=1001&rev=0&key={KEY}&quantity=5", 400, TestName = "Extra field")]
    [TestCase("pilot=-1001&rev=0&key={KEY}", 400, TestName = "Negative id")]
    [TestCase("pilot=01001&rev=0&key={KEY}", 400, TestName = "Leading zero id")]
    [TestCase("pilot=99999999999999&rev=0&key={KEY}", 400, TestName = "Huge id")]
    [TestCase("pilot=abc&rev=0&key={KEY}", 400, TestName = "Non-numeric id")]
    [TestCase("pilot=1002&rev=0&key={KEY}", 400, TestName = "Tampered target")]
    [TestCase("pilot=1001&rev=-1&key={KEY}", 400, TestName = "Negative revision")]
    [TestCase("pilot=1001&rev=99999999&key={KEY}", 400, TestName = "Huge revision")]
    [TestCase("pilot=1001&rev=0&key=abc", 400, TestName = "Malformed key")]
    public async Task FriendAdd_RejectsTamperedForms(string template, int status)
    {
        var (sessions, store, session, _) = await SetupAsync();
        var key = LocalMessagingAction.CreateFormKey(session.Dosid, "internalFriendAdd", "1001", 0, Now);
        var context = Request(session.Dosid, "internalFriendAdd", template.Replace("{KEY}", key));
        await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        Assert.That((await store.GetAsync(session.AccountId, Now, CancellationToken.None)).Revision, Is.Zero);
    }

    [Test]
    public async Task Forms_AreBoundToSessionActionAndLifetime()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var foreign = Request(session.Dosid, "internalFriendAdd", PilotBody("other-session", "internalFriendAdd", "1001", 0));
        var otherAction = Request(session.Dosid, "internalFriendAdd", PilotBody(session.Dosid, "internalFriendRemove", "1001", 0));
        var future = Request(session.Dosid, "internalFriendAdd", PilotBody(session.Dosid, "internalFriendAdd", "1001", 0, Now.AddHours(1)));
        var old = Request(session.Dosid, "internalFriendAdd", PilotBody(session.Dosid, "internalFriendAdd", "1001", 0, Now.AddHours(-2)));
        var extraQuery = Request(session.Dosid, "internalFriendAdd&pilot=1001", PilotBody(session.Dosid, "internalFriendAdd", "1001", 0));
        foreach (var context in new[] { foreign, otherAction, future, old, extraQuery })
            await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(foreign.Response.StatusCode, Is.EqualTo(400));
            Assert.That(otherAction.Response.StatusCode, Is.EqualTo(400));
            Assert.That(future.Response.StatusCode, Is.EqualTo(400));
            Assert.That(old.Response.StatusCode, Is.EqualTo(409));
            Assert.That(extraQuery.Response.StatusCode, Is.EqualTo(400));
        });
    }

    [Test]
    public async Task UnknownPilotOrForeignMessage_Returns404_AndSelfFriendIs400()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var unknown = Request(session.Dosid, "internalFriendAdd", PilotBody(session.Dosid, "internalFriendAdd", "4242", 0));
        var self = Request(session.Dosid, "internalFriendAdd", PilotBody(session.Dosid, "internalFriendAdd", "7", 0));
        // Message 9 exists only in another account's mailbox, if at all; ids are account-scoped.
        var foreign = Request(session.Dosid, "internalMessageDelete", MessageBody(session.Dosid, "9", 0));
        foreach (var context in new[] { unknown, self, foreign })
            await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
            Assert.That(self.Response.StatusCode, Is.EqualTo(400));
            Assert.That(foreign.Response.StatusCode, Is.EqualTo(404));
        });
    }

    [Test]
    public async Task DeleteAndReplay_DeleteOnceThenConflict()
    {
        var (sessions, store, session, _) = await SetupAsync();
        var body = MessageBody(session.Dosid, "1", 0);
        var first = Request(session.Dosid, "internalMessageDelete", body);
        var replay = Request(session.Dosid, "internalMessageDelete", body);
        await LocalMessagingAction.HandleAsync(first, sessions, store, new TestClock(Now), CancellationToken.None);
        await LocalMessagingAction.HandleAsync(replay, sessions, store, new TestClock(Now), CancellationToken.None);
        var stale = Request(session.Dosid, "internalMessageDelete", MessageBody(session.Dosid, "2", 0));
        await LocalMessagingAction.HandleAsync(stale, sessions, store, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalMessages&deleted=1"));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(stale.Response.StatusCode, Is.EqualTo(409));
        });
        Assert.That((await store.GetAsync(session.AccountId, Now, CancellationToken.None)).Messages.Select(m => m.Id),
            Is.EquivalentTo(new[] { 2, 3 }));
    }

    [Test]
    public async Task Send_ValidatesRecipientSubjectAndBody()
    {
        var (sessions, store, session, _) = await SetupAsync();
        await store.AddFriendAsync(session.AccountId, 1001, null, null, CancellationToken.None);
        async Task<int> SendAsync(string to, string subject, string body)
        {
            var key = LocalMessagingAction.CreateFormKey(session.Dosid, "internalMessageSend", "", 1, Now);
            var context = Request(session.Dosid, "internalMessageSend",
                $"to={Uri.EscapeDataString(to)}&subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}&rev=1&key={key}");
            await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
            return context.Response.StatusCode;
        }
        Assert.That(await SendAsync("1001", "   ", "x"), Is.EqualTo(400));
        Assert.That(await SendAsync("1001", new string('s', 61), "x"), Is.EqualTo(400));
        Assert.That(await SendAsync("1001", "hi", new string('b', 501)), Is.EqualTo(400));
        Assert.That(await SendAsync("1001", "a\u0007b", "x"), Is.EqualTo(400));
        Assert.That(await SendAsync("-5", "hi", "x"), Is.EqualTo(400));
        Assert.That(await SendAsync("4242", "hi", "x"), Is.EqualTo(404));
        Assert.That(await SendAsync("1002", "hi", "x"), Is.EqualTo(400));
        Assert.That(await SendAsync("1001", "<b>hi</b>", "line1\nline2"), Is.EqualTo(303));
        var page = LocalMessaging.Page(session, await store.GetAsync(session.AccountId, Now, CancellationToken.None), 4, Now);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("&lt;b&gt;hi&lt;/b&gt;"));
            Assert.That(page, Does.Not.Contain("<b>hi</b>"));
            Assert.That(page, Does.Contain("Sent (1)"));
        });
    }

    [Test]
    public async Task Page_ShowsInboxFriendsDirectoryAndShellLink()
    {
        var (_, store, session, _) = await SetupAsync();
        await store.AddFriendAsync(session.AccountId, 1003, null, null, CancellationToken.None);
        var page = LocalMessaging.Page(new GameAuthSession { Dosid = session.Dosid, Username = "<pilot>", GameUserId = 7, Company = "mmo" },
            await store.GetAsync(session.AccountId, Now, CancellationToken.None), 1, Now);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"messaging\""));
            Assert.That(page, Does.Contain("Inbox (3)"));
            Assert.That(page, Does.Contain("Welcome to DarkOrbit"));
            Assert.That(page, Does.Contain("id=\"message\""));
            Assert.That(page, Does.Contain("Friends (1 / 20)"));
            Assert.That(page, Does.Contain("internalFriendRemove"));
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(page, Does.Contain("changes no Credits, Uridium, or Hangar items"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalMessages"));
        });
    }

    [Test]
    public async Task Page_FolderTabsAndDetailNavigationStayWithinSelectedFolder()
    {
        var (_, store, session, _) = await SetupAsync();
        await store.AddFriendAsync(session.AccountId, 1001, null, null, CancellationToken.None);
        await store.SendAsync(session.AccountId, 1001, "Sent subject", "Sent body", Now, null, null, CancellationToken.None);
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        var inbox = LocalMessaging.Page(session, state, 2, Now, "inbox");
        var sent = LocalMessaging.Page(session, state, 4, Now, "sent");
        Assert.Multiple(() =>
        {
            Assert.That(inbox, Does.Contain("All (4)"));
            Assert.That(inbox, Does.Contain("Inbox (3)"));
            Assert.That(inbox, Does.Contain("Sent (1)"));
            Assert.That(inbox, Does.Contain("id=\"previousMessage\""));
            Assert.That(inbox, Does.Contain("id=\"nextMessage\""));
            Assert.That(inbox, Does.Contain("folder=inbox&amp;message=1"));
            Assert.That(inbox, Does.Not.Contain("<table id=\"sent\""));
            Assert.That(inbox, Does.Not.Contain("Sent subject"));
            Assert.That(sent, Does.Contain("Sent subject"));
            Assert.That(sent, Does.Not.Contain("<table id=\"inbox\""));
            Assert.That(sent, Does.Not.Contain("id=\"previousMessage\""));
            Assert.That(sent, Does.Contain("id=\"closeMessage\" href=\"/indexInternal.es?action=internalMessages&amp;folder=sent\""));
        });
    }

    [Test]
    public async Task Page_SearchesSubjectBodyAndPilotWithinFolder_AndKeepsNavigationScoped()
    {
        var (_, store, session, _) = await SetupAsync();
        await store.AddFriendAsync(session.AccountId, 1001, null, null, CancellationToken.None);
        await store.SendAsync(session.AccountId, 1001, "Supply order", "Galaxy parts", Now, null, null, CancellationToken.None);
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        var inbox = LocalMessaging.Page(session, state, 3, Now, "inbox", search: "Galaxy");
        var sent = LocalMessaging.Page(session, state, 4, Now, "sent", search: "Nova");
        var empty = LocalMessaging.Page(session, state, null, Now, "inbox", search: "No matches");
        Assert.Multiple(() =>
        {
            Assert.That(inbox, Does.Contain("1 matching messages"));
            Assert.That(inbox, Does.Contain("folder=inbox&amp;q=Galaxy&amp;message=3"));
            Assert.That(inbox, Does.Not.Contain("Welcome to DarkOrbit"));
            Assert.That(inbox, Does.Not.Contain("Supply order"));
            Assert.That(sent, Does.Contain("1 matching messages"));
            Assert.That(sent, Does.Contain("folder=sent&amp;q=Nova&amp;message=4"));
            Assert.That(sent, Does.Contain("Supply order"));
            Assert.That(empty, Does.Contain("0 matching messages"));
            Assert.That(empty, Does.Contain("No messages match this search."));
            Assert.That(empty, Does.Not.Contain("Galaxy Gates"));
        });
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

    private static string PilotBody(string dosid, string action, string pilot, int rev, DateTimeOffset? issued = null) =>
        $"pilot={pilot}&rev={rev}&key={LocalMessagingAction.CreateFormKey(dosid, action, pilot, rev, issued ?? Now)}";

    private static string MessageBody(string dosid, string message, int rev) =>
        $"message={message}&rev={rev}&key={LocalMessagingAction.CreateFormKey(dosid, "internalMessageDelete", message, rev, Now)}";

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
        return context;
    }
}
