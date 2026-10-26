using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAssemblyValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    // Mirrors the economy.craft_requests contract: one craft per request key, balance-checked.
    private sealed class FakeCraft
    {
        public readonly ConcurrentDictionary<string, string> Keys = new();
        public int Components = 300;
        public int Crafts;
        public EconomyCraftResult? Forced;

        public Task<EconomyCraftResult> CraftAsync(GameAuthSession session, string recipe, string key, CancellationToken ct)
        {
            if (Forced is { } forced) return Task.FromResult(forced);
            if (recipe is not ("lf3" or "flax")) return Task.FromResult(EconomyCraftResult.NotFound);
            lock (this)
            {
                if (!Keys.TryAdd($"{session.AccountId}:{key}", recipe)) return Task.FromResult(EconomyCraftResult.Duplicate);
                if (Components < 150) { Keys.TryRemove($"{session.AccountId}:{key}", out _); return Task.FromResult(EconomyCraftResult.InsufficientInputs); }
                Components -= 150;
                Crafts++;
                return Task.FromResult(EconomyCraftResult.Applied);
            }
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(GameAuthSessionStore Sessions, GameAuthSession Session)> SessionAsync(string? company = "mmo")
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        if (company is not null) Assert.That(await sessions.SelectCompanyAsync(session.Dosid, company, CancellationToken.None), Is.True);
        return (sessions, session);
    }

    private static DefaultHttpContext Request(string? dosid, string body, string method = "POST",
        string query = "?action=internalAssemblyCraft", string contentType = "application/x-www-form-urlencoded")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = contentType;
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string Body)> SendAsync(GameAuthSessionStore sessions, FakeCraft craft,
        DefaultHttpContext context, DateTimeOffset? now = null)
    {
        await LocalAssemblyAction.HandleAsync(context, sessions, craft.CraftAsync, new Clock(now ?? Now), CancellationToken.None);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static string Body(string dosid, string recipe = "lf3", DateTimeOffset? issued = null) =>
        $"recipe={recipe}&key={LocalAssemblyAction.CreateFormKey(dosid, recipe, issued ?? Now)}";

    [Test]
    public async Task SignedForm_CraftsOnceAndRedirects()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var context = Request(session.Dosid, Body(session.Dosid));
        var (status, _) = await SendAsync(sessions, craft, context);
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(303));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAssembly&crafted=lf3"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(craft.Crafts, Is.EqualTo(1));
            Assert.That(craft.Keys.Keys.Single(), Does.Contain(":assembly:"));
        });
    }

    [Test]
    public async Task Replay_DoubleSubmitAndConcurrentSubmits_CraftOnce()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft { Components = 10_000 };
        var body = Body(session.Dosid);
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, body))).Status, Is.EqualTo(303));
        var replay = await SendAsync(sessions, craft, Request(session.Dosid, body));
        Assert.That(replay.Status, Is.EqualTo(409));
        Assert.That(replay.Body, Does.Contain("already submitted"));

        var concurrentBody = Body(session.Dosid, "flax");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            SendAsync(sessions, craft, Request(session.Dosid, concurrentBody))));
        Assert.Multiple(() =>
        {
            Assert.That(results.Count(result => result.Status == 303), Is.EqualTo(1));
            Assert.That(results.Count(result => result.Status == 409), Is.EqualTo(15));
            Assert.That(craft.Crafts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task SessionAndCompany_Return401And403Pages()
    {
        var (sessions, session) = await SessionAsync(company: null);
        var craft = new FakeCraft();
        var anonymous = await SendAsync(sessions, craft, Request(null, Body("x")));
        var expired = await SendAsync(sessions, craft, Request("expired-dosid", Body("expired-dosid")));
        var noCompany = await SendAsync(sessions, craft, Request(session.Dosid, Body(session.Dosid)));
        Assert.Multiple(() =>
        {
            Assert.That(anonymous.Status, Is.EqualTo(401));
            Assert.That(anonymous.Body, Does.Contain("Assembly request failed (401)"));
            Assert.That(expired.Status, Is.EqualTo(401));
            Assert.That(noCompany.Status, Is.EqualTo(403));
            Assert.That(craft.Crafts, Is.Zero);
        });
    }

    [Test]
    public async Task NonPost_Returns405WithAllowHeader()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var context = Request(session.Dosid, Body(session.Dosid), method: "GET");
        var (status, _) = await SendAsync(sessions, craft, context);
        Assert.That(status, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(craft.Crafts, Is.Zero);
    }

    [TestCase("recipe=lf3")]
    [TestCase("key=abc")]
    [TestCase("")]
    [TestCase("recipe=lf3&recipe=flax&key={key}")]
    [TestCase("recipe=lf3&key={key}&key={key}")]
    [TestCase("recipe=lf3&key={key}&quantity=5")]
    [TestCase("recipe=lf3&key={key}&amount=-1")]
    [TestCase("recipe=LF3&key={key}")]
    [TestCase("recipe=%3Cscript%3E&key={key}")]
    [TestCase("recipe=lf3&key=not-a-key")]
    [TestCase("recipe=flax&key={key}")]
    public async Task MalformedOrTamperedForms_Return400WithoutCrafting(string template)
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var body = template.Replace("{key}", LocalAssemblyAction.CreateFormKey(session.Dosid, "lf3", Now));
        var (status, page) = await SendAsync(sessions, craft, Request(session.Dosid, body));
        Assert.That(status, Is.EqualTo(400));
        Assert.That(page, Does.Not.Contain("<script>"));
        Assert.That(craft.Crafts, Is.Zero);
    }

    [Test]
    public async Task OversizedRecipeAndKey_Return400()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var longRecipe = new string('a', LocalAssemblyAction.MaxRecipeIdLength + 1);
        var longKey = new string('a', LocalAssemblyAction.MaxKeyLength + 1);
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid,
            $"recipe={longRecipe}&key={LocalAssemblyAction.CreateFormKey(session.Dosid, longRecipe, Now)}"))).Status, Is.EqualTo(400));
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, $"recipe=lf3&key={longKey}"))).Status, Is.EqualTo(400));
    }

    [Test]
    public async Task ExtraQueryOrNonFormBody_Return400()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var body = Body(session.Dosid);
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, body,
            query: "?action=internalAssemblyCraft&recipe=lf3"))).Status, Is.EqualTo(400));
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, body,
            query: "?action=internalAssemblyCraft&action=internalAssemblyCraft"))).Status, Is.EqualTo(400));
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, "{\"recipe\":\"lf3\"}",
            contentType: "application/json"))).Status, Is.EqualTo(400));
        Assert.That(craft.Crafts, Is.Zero);
    }

    [Test]
    public async Task ForeignSessionKey_Returns400()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var (status, _) = await SendAsync(sessions, craft, Request(session.Dosid, Body("another-session-dosid")));
        Assert.That(status, Is.EqualTo(400));
        Assert.That(craft.Crafts, Is.Zero);
    }

    [Test]
    public async Task ExpiredKey_Returns409AndFutureKey_Returns400()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft();
        var expired = await SendAsync(sessions, craft, Request(session.Dosid,
            Body(session.Dosid, issued: Now - LocalAssemblyAction.FormLifetime - TimeSpan.FromSeconds(1))));
        var future = await SendAsync(sessions, craft, Request(session.Dosid, Body(session.Dosid, issued: Now.AddHours(1))));
        var edge = await SendAsync(sessions, craft, Request(session.Dosid,
            Body(session.Dosid, issued: Now - LocalAssemblyAction.FormLifetime)));
        Assert.Multiple(() =>
        {
            Assert.That(expired.Status, Is.EqualTo(409));
            Assert.That(expired.Body, Does.Contain("expired"));
            Assert.That(future.Status, Is.EqualTo(400));
            Assert.That(edge.Status, Is.EqualTo(303));
        });
    }

    [Test]
    public async Task StoreResults_MapToStatusCodes()
    {
        var (sessions, session) = await SessionAsync();
        var unknown = await SendAsync(sessions, new FakeCraft(), Request(session.Dosid, Body(session.Dosid, "missing_recipe")));
        var insufficient = await SendAsync(sessions, new FakeCraft { Components = 0 }, Request(session.Dosid, Body(session.Dosid)));
        var disabled = await SendAsync(sessions, new FakeCraft { Forced = EconomyCraftResult.Unavailable }, Request(session.Dosid, Body(session.Dosid)));
        var invalid = await SendAsync(sessions, new FakeCraft { Forced = EconomyCraftResult.Invalid }, Request(session.Dosid, Body(session.Dosid)));
        Assert.Multiple(() =>
        {
            Assert.That(unknown.Status, Is.EqualTo(404));
            Assert.That(insufficient.Status, Is.EqualTo(409));
            Assert.That(insufficient.Body, Does.Contain("Not enough"));
            Assert.That(disabled.Status, Is.EqualTo(409));
            Assert.That(disabled.Body, Does.Contain("unavailable"));
            Assert.That(invalid.Status, Is.EqualTo(400));
        });
    }

    [Test]
    public async Task InsufficientCraft_DoesNotConsumeTheFormKey()
    {
        var (sessions, session) = await SessionAsync();
        var craft = new FakeCraft { Components = 0 };
        var body = Body(session.Dosid);
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, body))).Status, Is.EqualTo(409));
        craft.Components = 150;
        Assert.That((await SendAsync(sessions, craft, Request(session.Dosid, body))).Status, Is.EqualTo(303));
        Assert.That(craft.Components, Is.Zero);
    }

    [Test]
    public void DatabasePage_ShowsStatusSignedFormsAndEscapesValues()
    {
        static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
        var session = new GameAuthSession { Username = "<pilot>", Dosid = "page-dosid" };
        var recipes = new[]
        {
            new EconomyRecipe("lf3", "LF-3 laser", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":150},{\"kind\":\"currency\",\"id\":\"credits\",\"quantity\":500}]"),
                Json("[{\"kind\":\"item\",\"id\":\"laser_lf3\",\"quantity\":1}]"), 0, true),
            new EconomyRecipe("flax", "<Flax>", Json("[{\"kind\":\"item\",\"id\":\"drone_iris\",\"quantity\":1}]"),
                Json("[{\"kind\":\"item\",\"id\":\"drone_flax\",\"quantity\":1}]"), 0, true),
            new EconomyRecipe("old", "Retired", Json("[]"), Json("[{\"kind\":\"item\",\"id\":\"x\",\"quantity\":1}]"), 0, false)
        };
        var page = LocalAssembly.PageFromDatabase(session, recipes,
            [new EconomyBalance("components", "resource", 200), new EconomyBalance("credits", "currency", 500)],
            new Dictionary<string, long>());
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(page, Does.Contain("&lt;Flax&gt;"));
            Assert.That(page, Does.Not.Contain("<Flax>"));
            Assert.That(page, Does.Contain("1 of 3 recipes ready"));
            Assert.That(page, Does.Contain("Missing: drone_iris"));
            Assert.That(page, Does.Contain("Unavailable"));
            Assert.That(page, Does.Contain("name=\"key\""));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(page, "name=\"key\"").Count, Is.EqualTo(2));
        });
    }
}
