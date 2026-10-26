using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-130: confirmation notices after successful Messaging actions (all emulator-defined).
public class LocalMessagingNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static IQueryCollection Query(string query) => new QueryCollection(
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));

    [TestCase("?action=internalMessages&sent=4")]
    [TestCase("?action=internalMessages&deleted=1")]
    [TestCase("?action=internalMessages&added=1001")]
    [TestCase("?action=internalMessages&removed=1008")]
    [TestCase("?action=internalMessages&readall=1")]
    [TestCase("?action=internalMessages&folder=sent&sent=4")]
    [TestCase("?action=internalMessages&message=2&readall=1&from=internalStart&prc=1&lang=es")]
    public void PageQuery_AcceptsOneNotice(string query) =>
        Assert.That(LocalMessagingAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("?action=internalMessages&sent=4&deleted=1")]
    [TestCase("?action=internalMessages&added=1001&removed=1001")]
    [TestCase("?action=internalMessages&sent=4&sent=4")]
    [TestCase("?action=internalMessages&SENT=4")]
    [TestCase("?action=internalMessages&Readall=1")]
    [TestCase("?action=internalMessages&readall=0")]
    [TestCase("?action=internalMessages&readall=true")]
    [TestCase("?action=internalMessages&readall=")]
    [TestCase("?action=internalMessages&deleted=0")]
    [TestCase("?action=internalMessages&deleted=01")]
    [TestCase("?action=internalMessages&deleted=-1")]
    [TestCase("?action=internalMessages&added=%2B1001")]
    [TestCase("?action=internalMessages&added=1234567890")]
    [TestCase("?action=internalMessages&removed=Nova")]
    [TestCase("?action=internalMessages&sent=")]
    public void PageQuery_RejectsBadNotices(string query) =>
        Assert.That(LocalMessagingAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void NoticeOf_ReadsTheSingleNoticeKey()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalMessagingAction.NoticeOf(Query("?action=internalMessages")), Is.Null);
            Assert.That(LocalMessagingAction.NoticeOf(Query("?action=internalMessages&added=1003")), Is.EqualTo(("added", 1003)));
            Assert.That(LocalMessagingAction.NoticeOf(Query("?action=internalMessages&readall=1")), Is.EqualTo(("readall", 1)));
        });
    }

    [Test]
    public async Task FullFlow_EveryRedirectPassesThePageCheck_AndShowsStateDerivedNotice()
    {
        var (sessions, store, session) = await SetupAsync();
        var add = await PostAsync(sessions, store, session, "internalFriendAdd", "1001");
        var addPage = await PageAsync(store, session, add);
        var send = await PostAsync(sessions, store, session, "internalMessageSend", "",
            "to=1001&subject=Hello%20%3Cb%3E&body=See%20you");
        var sendPage = await PageAsync(store, session, send);
        var readAll = await PostAsync(sessions, store, session, "internalMessageReadAll", "");
        var readAllPage = await PageAsync(store, session, readAll);
        var delete = await PostAsync(sessions, store, session, "internalMessageDelete", "1");
        var deletePage = await PageAsync(store, session, delete);
        var remove = await PostAsync(sessions, store, session, "internalFriendRemove", "1001");
        var removePage = await PageAsync(store, session, remove);
        Assert.Multiple(() =>
        {
            Assert.That(new[] { add, send, readAll, delete, remove }.Select(Location), Is.EqualTo(new[]
            {
                "/indexInternal.es?action=internalMessages&added=1001",
                "/indexInternal.es?action=internalMessages&sent=4",
                "/indexInternal.es?action=internalMessages&readall=1",
                "/indexInternal.es?action=internalMessages&deleted=1",
                "/indexInternal.es?action=internalMessages&removed=1001"
            }));
            Assert.That(addPage, Does.Contain("<p id=\"messagingNotice\" role=\"status\">Nova-Hunter (MMO) added to your friends. Friends: 1 / 20. You can now send Nova-Hunter mail.</p>"));
            Assert.That(sendPage, Does.Contain("Mail sent to Nova-Hunter: &quot;Hello &lt;b&gt;&quot;. Sent folder: 1. Mailbox: 4 / 50."));
            Assert.That(sendPage, Does.Not.Contain("Hello <b>"));
            Assert.That(readAllPage, Does.Contain("All 3 received messages marked read."));
            Assert.That(deletePage, Does.Contain("Message deleted. Inbox: 2, Sent: 1. Mailbox: 3 / 50."));
            Assert.That(removePage, Does.Contain("Nova-Hunter removed from your friends. Friends: 0 / 20. Add a friend to send mail."));
        });
    }

    [Test]
    public void Notice_IsHiddenWhenStateDoesNotMatch()
    {
        var seeded = LocalMessagingStore.Seed(Now);
        var withFriend = seeded with { Friends = [1001] };
        Assert.Multiple(() =>
        {
            Assert.That(LocalMessaging.Notice(seeded, "added", 1001), Is.Null, "pilot is not a friend");
            Assert.That(LocalMessaging.Notice(withFriend, "removed", 1001), Is.Null, "pilot is still a friend");
            Assert.That(LocalMessaging.Notice(seeded, "removed", 9999), Is.Null, "unknown pilot");
            Assert.That(LocalMessaging.Notice(seeded, "added", 9999), Is.Null, "unknown pilot");
            Assert.That(LocalMessaging.Notice(seeded, "sent", 1), Is.Null, "received mail is not sent mail");
            Assert.That(LocalMessaging.Notice(seeded, "sent", 77), Is.Null, "no such message");
            Assert.That(LocalMessaging.Notice(seeded, "deleted", 1), Is.Null, "message still exists");
            Assert.That(LocalMessaging.Notice(seeded, "deleted", 50), Is.Null, "id never issued");
            Assert.That(LocalMessaging.Notice(seeded, "readall", 1), Is.Null, "messages are still unread");
            Assert.That(LocalMessaging.Notice(seeded with { Messages = [] }, "readall", 1), Is.Null, "empty inbox");
            Assert.That(LocalMessaging.Notice(seeded, "bogus", 1), Is.Null);
            Assert.That(LocalMessaging.Page(TestSession(), seeded, notice: ("added", 1001)), Does.Not.Contain("messagingNotice"));
        });
    }

    [Test]
    public void Notice_ReportsFullFriendListAndFullMailbox()
    {
        var friends = LocalMessagingStore.Directory.Select(pilot => pilot.Id).ToList();
        var mail = Enumerable.Range(1, LocalMessagingStore.MaxMessages).Select(id =>
            new LocalMailMessage(id, LocalMessagingStore.SystemSender, 1002, "S" + id, "", Now.AddMinutes(id), Sent: true)).ToList();
        var full = new LocalMessagingState(mail, friends, LocalMessagingStore.MaxMessages + 1);
        Assert.Multiple(() =>
        {
            Assert.That(LocalMessaging.Notice(full, "sent", 50),
                Does.EndWith("Mailbox: 50 / 50. The mailbox is full; the oldest message is removed with each new one."));
            Assert.That(LocalMessaging.Notice(full, "added", 1008), Does.Contain("Friends: 8 / 20."));
            Assert.That(LocalMessaging.Notice(full, "added", 1008), Does.Not.Contain("full"));
        });
    }

    [Test]
    public async Task FailedActions_DoNotRedirect()
    {
        var (sessions, store, session) = await SetupAsync();
        var duplicate = await PostAsync(sessions, store, session, "internalFriendRemove", "1001");
        var unknown = await PostAsync(sessions, store, session, "internalMessageDelete", "99");
        var nonFriend = await PostAsync(sessions, store, session, "internalMessageSend", "", "to=1002&subject=Hi&body=");
        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Response.StatusCode, Is.EqualTo(409));
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
            Assert.That(nonFriend.Response.StatusCode, Is.EqualTo(400));
            Assert.That(new[] { duplicate, unknown, nonFriend }.Select(Location), Has.All.Empty);
        });
    }

    private static string Location(DefaultHttpContext context) => context.Response.Headers.Location.ToString();

    private static async Task<string> PageAsync(LocalMessagingStore store, GameAuthSession session, DefaultHttpContext redirect)
    {
        var query = Query(Location(redirect)[Location(redirect).IndexOf('?')..]);
        Assert.That(LocalMessagingAction.ValidPageQuery(query), Is.True, Location(redirect));
        var state = await store.GetAsync(session.AccountId, Now, CancellationToken.None);
        return LocalMessaging.Page(session, state, null, Now, "all", LocalMessagingAction.NoticeOf(query));
    }

    private static async Task<DefaultHttpContext> PostAsync(GameAuthSessionStore sessions, LocalMessagingStore store,
        GameAuthSession session, string action, string target, string fields = "")
    {
        var revision = (await store.GetAsync(session.AccountId, Now, CancellationToken.None)).Revision;
        var targetField = target.Length == 0 ? "" : $"{(action == "internalMessageDelete" ? "message" : "pilot")}={target}&";
        var extra = fields.Length == 0 ? "" : fields + "&";
        var body = $"{extra}{targetField}rev={revision}&key={LocalMessagingAction.CreateFormKey(session.Dosid, action, target, revision, Now)}";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalMessagingAction.HandleAsync(context, sessions, store, new TestClock(Now), CancellationToken.None);
        return context;
    }

    private static GameAuthSession TestSession() =>
        new() { AccountId = Guid.NewGuid(), Username = "pilot", GameUserId = 7, Dosid = "dosid", Company = "mmo" };

    private static async Task<(GameAuthSessionStore, LocalMessagingStore, GameAuthSession)> SetupAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!;
        return (sessions, new LocalMessagingStore(cache), session);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
