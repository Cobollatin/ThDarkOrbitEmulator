using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMessagingHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalMessagingStore _messaging = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _messaging = new LocalMessagingStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("internalFriendAdd", "pilot")]
    [TestCase("internalFriendRemove", "rev")]
    [TestCase("internalMessageDelete", "message")]
    [TestCase("internalMessageSend", "body")]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing(string action, string field)
    {
        var context = Build(action, $"--b\r\nContent-Disposition: form-data; name=\"{field}\"\r\n\r\nhu");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        var state = await _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None);
        Assert.That(state.Revision, Is.Zero);
        Assert.That(state.Friends, Is.Empty);
        Assert.That(state.Messages, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ValidMultipartBody_StillAddsFriend()
    {
        var key = LocalMessagingAction.CreateFormKey(_session.Dosid, "internalFriendAdd", "1001", 0, Now);
        var context = Build("internalFriendAdd",
            string.Concat(Part("pilot", "1001"), Part("rev", "0"), Part("key", key), "--b--\r\n"));
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That((await _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None)).Friends,
            Does.Contain(1001));
    }

    [TestCase("internalMessageSend", "GET")]
    [TestCase("internalFriendAdd", "PUT")]
    [TestCase("internalMessageDelete", "DELETE")]
    public async Task WrongMethod_Returns405WithAllowHeader_BeforeSessionLookup(string action, string method)
    {
        var context = Build(action, "", "missing-session");
        context.Request.Method = method;
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [TestCase("?action=InternalFriendAdd")]
    [TestCase("?action=internalFriendAdd&action=internalFriendAdd")]
    public async Task ActionQuery_WrongCaseOrRepeated_Returns404(string query)
    {
        var context = Build("internalFriendAdd", "pilot=1001");
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task ExtraQueryParameter_Returns400()
    {
        var context = Build("internalFriendAdd", "pilot=1001");
        context.Request.QueryString = new QueryString("?action=internalFriendAdd&from=internalStart");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task ErrorPages_DoNotEchoInput()
    {
        var context = Build("internalFriendAdd", "pilot=%3Cscript%3Ealert(1)%3C%2Fscript%3E&rev=0&key=x");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var body = await Body(context);
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(body, Does.Not.Contain("alert"));
    }

    [TestCase("?action=internalMessages", true)]
    [TestCase("?action=internalMessages&message=2", true)]
    [TestCase("?action=internalMessages&folder=all", true)]
    [TestCase("?action=internalMessages&folder=inbox&message=2", true)]
    [TestCase("?action=internalMessages&folder=sent", true)]
    [TestCase("?action=internalMessages&from=internalStart&prc=100&lang=es", true)]
    [TestCase("?action=internalMessages&message=3&LANG=es", false)]
    [TestCase("?ACTION=internalMessages", false)]
    [TestCase("?action=InternalMessages", false)]
    [TestCase("?action=internalMessages&Folder=inbox", false)]
    [TestCase("?action=internalMessages&Message=2", false)]
    [TestCase("?action=internalMessages&pilot=1001", false)]
    [TestCase("?action=internalMessages&message=1&message=2", false)]
    [TestCase("?action=internalMessages&message=01", false)]
    [TestCase("?action=internalMessages&message=-1", false)]
    [TestCase("?action=internalMessages&message=", false)]
    [TestCase("?action=internalMessages&folder=", false)]
    [TestCase("?action=internalMessages&folder=Inbox", false)]
    [TestCase("?action=internalMessages&folder=archive", false)]
    [TestCase("?action=internalMessages&folder=inbox&folder=sent", false)]
    [TestCase("?action=internalMessages&from=a&from=b", false)]
    [TestCase("?action=internalMessages&action=internalMessages", false)]
    [TestCase("?from=internalStart", false)]
    [TestCase("?action=internalMessages&lang=%0A", false)]
    public void PageQuery_AcceptsOnlyNavigationParametersAndMessageId(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalMessagingAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValue()
    {
        Assert.That(LocalMessagingAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalMessages&prc=" + new string('a', 64)))), Is.True);
        Assert.That(LocalMessagingAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalMessages&prc=" + new string('a', 65)))), Is.False);
    }

    [Test]
    public async Task LastMessageId_IsReadable_ThenFurtherSendsReturnFullWithoutChangingState()
    {
        var cache = _services.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync($"localmessaging:{_session.AccountId:N}",
            System.Text.Json.JsonSerializer.Serialize(new LocalMessagingState([], [1001],
                LocalMessagingStore.MaxMessageId, 0)));
        Assert.That(await _messaging.SendAsync(_session.AccountId, 1001, "Last", "Body", Now,
            0, null, CancellationToken.None), Is.EqualTo(MessagingChange.Applied));
        var full = await _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None);
        Assert.That(full.Messages.Single().Id, Is.EqualTo(LocalMessagingStore.MaxMessageId));
        Assert.That(LocalMessagingAction.TryParseId(full.Messages.Single().Id.ToString(), out _), Is.True);
        Assert.That(await _messaging.SendAsync(_session.AccountId, 1001, "Overflow", "Body", Now,
            1, null, CancellationToken.None), Is.EqualTo(MessagingChange.Full));
        var unchanged = await _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None);
        Assert.That(unchanged.Revision, Is.EqualTo(full.Revision));
        Assert.That(unchanged.NextId, Is.EqualTo(full.NextId));
        Assert.That(unchanged.Messages.Select(message => message.Id), Is.EqualTo(full.Messages.Select(message => message.Id)));
    }

    [Test]
    public async Task CachedMessageIdsOutsidePageContract_AreDiscarded()
    {
        var cache = _services.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync($"localmessaging:{_session.AccountId:N}",
            System.Text.Json.JsonSerializer.Serialize(new LocalMessagingState(
                [new LocalMailMessage(1_000_000_000, 0, 0, "Invalid", "Body", Now),
                 new LocalMailMessage(42, 0, 0, "Valid", "Body", Now)], [], 43)));
        var state = await _messaging.GetAsync(_session.AccountId, Now, CancellationToken.None);
        Assert.That(state.Messages.Select(message => message.Id), Is.EqualTo(new[] { 42 }));
        Assert.That(state.NextId, Is.EqualTo(43));
    }

    private Task Handle(DefaultHttpContext context) =>
        LocalMessagingAction.HandleAsync(context, _sessions, _messaging, new FixedClock(Now), CancellationToken.None);

    private static string Part(string name, string value) =>
        $"--b\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n";

    private DefaultHttpContext Build(string action, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
