using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarSelectionHardeningTests
{
    [TestCase("", true)]
    [TestCase("&from=internalStart&prc=100&lang=es", true)]
    [TestCase("&FROM=internalStart", false)] // Menu-191: keys match exactly
    [TestCase("&slot=2", false)]
    [TestCase("&key=abc", false)]
    [TestCase("&lang=en&lang=es", false)]
    [TestCase("&action=internalDock", false)]
    public void PageQuery_AcceptsOnlyShellParameters(string extra, bool valid)
    {
        var query = new DefaultHttpContext { Request = { QueryString = new QueryString("?action=internalDock" + extra) } }.Request.Query;
        Assert.That(LocalHangars.ValidPageQuery(query), Is.EqualTo(valid));
    }

    [Test]
    public void PageQuery_RejectsOverlongValues()
    {
        var query = new DefaultHttpContext
        {
            Request = { QueryString = new QueryString("?action=internalDock&from=" + new string('a', 65)) }
        }.Request.Query;
        Assert.That(LocalHangars.ValidPageQuery(query), Is.False);
    }

    [TestCase("1", 1)]
    [TestCase("2", 2)]
    [TestCase("02", 0)]
    [TestCase("+2", 0)]
    [TestCase(" 2", 0)]
    [TestCase("3", 0)]
    [TestCase("", 0)]
    [TestCase(null, 0)]
    public void TryParseSlot_AcceptsOnlyCanonicalSlots(string? value, int expected)
    {
        Assert.That(LocalHangars.TryParseSlot(value, out var slot), Is.EqualTo(expected != 0));
        Assert.That(slot, Is.EqualTo(expected));
    }

    [Test]
    public void Page_SignsOnlyTheInactiveSlotAgainstTheActiveOne()
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "dock-dosid", Company = "eic" };
        var page = LocalHangars.Page(session, 2);

        Assert.That(Regex.Matches(page, "<form ").Count, Is.EqualTo(1));
        Assert.That(page, Does.Contain("data-slot=\"2\" data-active=\"true\""));
        Assert.That(page, Does.Contain("name=\"slot\" value=\"1\""));
        Assert.That(page, Does.Contain("name=\"current\" value=\"2\""));
        Assert.That(page, Does.Contain("ship_phoenix (model 1)"));
        var key = Regex.Match(page, "name=\"key\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.That(LocalHangars.KeyIssued(session.Dosid, 1, 2, key), Is.Not.Null);
        Assert.That(LocalHangars.KeyIssued(session.Dosid, 2, 1, key), Is.Null);
        Assert.That(LocalHangars.KeyIssued("other-dosid", 1, 2, key), Is.Null);
        // A corrupt stored slot still renders exactly one active card.
        Assert.That(Regex.Matches(LocalHangars.Page(session, 9), "data-active=\"true\"").Count, Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("a.b.c")]
    [TestCase("00000000000000000000000000000000.0123.0000000000000000000000000000000000000000000000000000000000000000")]
    public void KeyIssued_RejectsMalformedKeys(string key) =>
        Assert.That(LocalHangars.KeyIssued("dosid", 2, 1, key), Is.Null);

    [Test]
    public async Task Store_CompareAndSet_RejectsStaleExpectedSlot()
    {
        var (_, _, store, _) = await CreateAsync();
        var account = Guid.NewGuid();
        Assert.That(await store.ChangeAsync(account, 2, 2, CancellationToken.None), Is.EqualTo(LocalHangarChange.Invalid));
        Assert.That(await store.ChangeAsync(account, 2, 3, CancellationToken.None), Is.EqualTo(LocalHangarChange.Invalid));
        Assert.That(await store.ChangeAsync(account, 1, 2, CancellationToken.None), Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(await store.ChangeAsync(account, 2, 1, CancellationToken.None), Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(await store.ChangeAsync(account, 2, 1, CancellationToken.None), Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(await store.ChangeAsync(account, 1, 2, CancellationToken.None), Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(await store.GetActiveSlotAsync(account, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task Selection_RoundTripsWithFreshPageKeys()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        var first = await PostAsync(sessions, hangars, clock, session.Dosid, Body(2, 1, Key(session, 2, 1, clock)));
        Assert.That(first.Response.StatusCode, Is.EqualTo(303));
        Assert.That(first.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var back = await PostAsync(sessions, hangars, clock, session.Dosid, Body(1, 2, Key(session, 1, 2, clock)));
        Assert.That(back.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task Selection_StaleTabAfterAnotherChange_Returns409()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        // Two tabs rendered while slot 1 was active; the first submit wins, the second is stale.
        var firstTab = Body(2, 1, Key(session, 2, 1, clock));
        var secondTab = Body(2, 1, Key(session, 2, 1, clock));
        Assert.That((await PostAsync(sessions, hangars, clock, session.Dosid, firstTab)).Response.StatusCode, Is.EqualTo(303));

        var result = await PostAsync(sessions, hangars, clock, session.Dosid, secondTab);
        Assert.That(result.Response.StatusCode, Is.EqualTo(409));
        Assert.That(await ReadBodyAsync(result), Does.Contain("active hangar changed"));
        Assert.That(await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None), Is.EqualTo(2));
    }

    [Test]
    public async Task Selection_ExpiredAndFutureKeys_AreRejected()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        var expired = Body(2, 1, LocalHangars.CreateFormKey(session.Dosid, 2, 1, clock.GetUtcNow() - TimeSpan.FromMinutes(61)));
        Assert.That((await PostAsync(sessions, hangars, clock, session.Dosid, expired)).Response.StatusCode, Is.EqualTo(409));
        var future = Body(2, 1, LocalHangars.CreateFormKey(session.Dosid, 2, 1, clock.GetUtcNow() + TimeSpan.FromMinutes(6)));
        Assert.That((await PostAsync(sessions, hangars, clock, session.Dosid, future)).Response.StatusCode, Is.EqualTo(400));
        var nearEdge = Body(2, 1, LocalHangars.CreateFormKey(session.Dosid, 2, 1, clock.GetUtcNow() - TimeSpan.FromMinutes(59)));
        Assert.That((await PostAsync(sessions, hangars, clock, session.Dosid, nearEdge)).Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task Selection_TamperedOrForeignForms_Return400WithoutChangingState()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        var key = Key(session, 2, 1, clock);
        var otherKey = LocalHangars.CreateFormKey("someone-else", 2, 1, clock.GetUtcNow());
        foreach (var body in new[]
                 {
                     $"slot=1&current=2&key={key}",
                     $"slot=2&current=2&key={key}",
                     $"slot=2&current=1&key={otherKey}",
                     $"slot=02&current=1&key={key}",
                     $"slot=2&current=1",
                     $"slot=2&current=1&key={key}&key={key}",
                     $"slot=2&current=1&key={key}&extra=1",
                     $"slot=2&current=1&key={key}x",
                     $"slot=2&current=1&key={new string('a', 200)}"
                 })
        {
            var context = await PostAsync(sessions, hangars, clock, session.Dosid, body);
            Assert.That(context.Response.StatusCode, Is.EqualTo(400), body);
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"), body);
        }
        Assert.That(await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task Selection_RejectsWrongMethodQueryAndContentType()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        var body = Body(2, 1, Key(session, 2, 1, clock));

        var get = Request(session.Dosid, body);
        get.Request.Method = "GET";
        await LocalHangarSelection.HandleAsync(get, sessions, hangars, CancellationToken.None, clock);
        Assert.That(get.Response.StatusCode, Is.EqualTo(405));

        var extraQuery = Request(session.Dosid, body, "?action=internalDockSelect&slot=1");
        await LocalHangarSelection.HandleAsync(extraQuery, sessions, hangars, CancellationToken.None, clock);
        Assert.That(extraQuery.Response.StatusCode, Is.EqualTo(400));

        var repeatedAction = Request(session.Dosid, body, "?action=internalDockSelect&action=internalDockSelect");
        await LocalHangarSelection.HandleAsync(repeatedAction, sessions, hangars, CancellationToken.None, clock);
        Assert.That(repeatedAction.Response.StatusCode, Is.EqualTo(400));

        var json = Request(session.Dosid, "{\"slot\":2}");
        json.Request.ContentType = "application/json";
        await LocalHangarSelection.HandleAsync(json, sessions, hangars, CancellationToken.None, clock);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));

        var unknown = Request(session.Dosid, body, "?action=internalDockSomething");
        await LocalHangarSelection.HandleAsync(unknown, sessions, hangars, CancellationToken.None, clock);
        Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
        Assert.That(await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task Selection_ErrorPagesEncodeNothingFromTheRequest()
    {
        var (sessions, session, hangars, clock) = await CreateAsync();
        var context = await PostAsync(sessions, hangars, clock, session.Dosid, "slot=<script>&current=1&key=x");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var html = await ReadBodyAsync(context);
        Assert.That(html, Does.Not.Contain("<script>"));
        Assert.That(html, Does.Contain("action=internalDock\""));
    }

    private static string Key(GameAuthSession session, int slot, int current, TimeProvider clock) =>
        LocalHangars.CreateFormKey(session.Dosid, slot, current, clock.GetUtcNow());

    private static string Body(int slot, int current, string key) => $"slot={slot}&current={current}&key={key}";

    private static async Task<(GameAuthSessionStore, GameAuthSession, LocalHangarStore, TestClock)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!,
            new LocalHangarStore(cache), new TestClock());
    }

    private static async Task<DefaultHttpContext> PostAsync(IGameAuthSessionStore sessions, LocalHangarStore hangars,
        TimeProvider clock, string dosid, string body)
    {
        var context = Request(dosid, body);
        await LocalHangarSelection.HandleAsync(context, sessions, hangars, CancellationToken.None, clock);
        return context;
    }

    private static DefaultHttpContext Request(string dosid, string body, string query = "?action=internalDockSelect")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
