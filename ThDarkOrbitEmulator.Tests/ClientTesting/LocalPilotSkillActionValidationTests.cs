using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSkillActionValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalPilotSkillStore _skills = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _skills = new LocalPilotSkillStore(cache);
        _session = await CreateSessionAsync("mmo");
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task AllocateThenReset_AppliesAndAdvancesRevision()
    {
        Assert.That((await Allocate("hull", 0)).Status, Is.EqualTo(303));
        Assert.That((await Allocate("hull", 1)).Status, Is.EqualTo(303));
        var reset = await Reset(2);
        Assert.That(reset.Status, Is.EqualTo(303));
        Assert.That(reset.Context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var state = await State();
        Assert.Multiple(() =>
        {
            Assert.That(state.Spent, Is.Zero);
            Assert.That(state.Revision, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ReplayedKey_Returns409WithGenericMessage()
    {
        var key = Key("allocate", "hull", 0);
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}")).Status, Is.EqualTo(303));
        var replay = await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}");
        Assert.Multiple(() =>
        {
            Assert.That(replay.Status, Is.EqualTo(409));
            Assert.That(replay.Html, Does.Contain("already submitted"));
        });
        Assert.That((await State()).Ranks["hull"], Is.EqualTo(1));
    }

    [Test]
    public async Task StaleRevision_Returns409WithGenericMessage()
    {
        Assert.That((await Allocate("hull", 0)).Status, Is.EqualTo(303));
        var stale = await Allocate("shields", 0);
        Assert.Multiple(() =>
        {
            Assert.That(stale.Status, Is.EqualTo(409));
            Assert.That(stale.Html, Does.Contain("already submitted or your skills have changed"));
        });
        Assert.That((await State()).Ranks.ContainsKey("shields"), Is.False);
    }

    [Test]
    public async Task LockedAdvancedSkill_Returns409NamingThePrerequisite()
    {
        Assert.That((await Allocate("hull", 0)).Status, Is.EqualTo(303));
        var locked = await Allocate("evasion", 1);
        Assert.Multiple(() =>
        {
            Assert.That(locked.Status, Is.EqualTo(409));
            Assert.That(locked.Html, Does.Contain("Evasion requires Hull Training rank 3."));
        });
        Assert.That((await State()).Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task UnlockedAdvancedSkill_AppliesAfterPrerequisiteRank()
    {
        for (var rev = 0; rev < 3; rev++) Assert.That((await Allocate("hull", rev)).Status, Is.EqualTo(303));
        Assert.That((await Allocate("evasion", 3)).Status, Is.EqualTo(303));
        Assert.That((await State()).Ranks["evasion"], Is.EqualTo(1));
    }

    [Test]
    public async Task MaxedSkill_Returns409NamingTheMaximum()
    {
        for (var rev = 0; rev < 5; rev++) Assert.That((await Allocate("hull", rev)).Status, Is.EqualTo(303));
        var maxed = await Allocate("hull", 5);
        Assert.Multiple(() =>
        {
            Assert.That(maxed.Status, Is.EqualTo(409));
            Assert.That(maxed.Html, Does.Contain("Hull Training is already at its maximum rank."));
        });
        Assert.That((await State()).Ranks["hull"], Is.EqualTo(5));
    }

    [Test]
    public async Task ExhaustedBudget_Returns409AndNeverOverspends()
    {
        var rev = 0;
        for (var i = 0; i < 5; i++) Assert.That((await Allocate("hull", rev++)).Status, Is.EqualTo(303));
        for (var i = 0; i < 5; i++) Assert.That((await Allocate("shields", rev++)).Status, Is.EqualTo(303));
        var exhausted = await Allocate("lasers", rev);
        Assert.Multiple(() =>
        {
            Assert.That(exhausted.Status, Is.EqualTo(409));
            Assert.That(exhausted.Html, Does.Contain("no skill points left"));
        });
        var state = await State();
        Assert.Multiple(() =>
        {
            Assert.That(state.Spent, Is.EqualTo(LocalPilotSkillState.TotalPoints));
            Assert.That(state.Available, Is.Zero);
        });
    }

    [Test]
    public async Task ResetWithNothingSpent_Returns409NamingTheRule()
    {
        var reset = await Reset(0);
        Assert.Multiple(() =>
        {
            Assert.That(reset.Status, Is.EqualTo(409));
            Assert.That(reset.Html, Does.Contain("no spent skill points to reset"));
        });
        Assert.That((await State()).Revision, Is.Zero);
    }

    [Test]
    public async Task TamperedSignature_Returns400()
    {
        var key = Key("allocate", "hull", 0);
        var tampered = key[..^1] + (key[^1] == '0' ? '1' : '0');
        var result = await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(tampered)}");
        Assert.That(result.Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task KeyForAnotherSkillOrAction_Returns400()
    {
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=shields&rev=0&key={Esc(Key("allocate", "hull", 0))}")).Status, Is.EqualTo(400));
        Assert.That((await Post("internalPilotSkillReset", $"rev=0&key={Esc(Key("allocate", "hull", 0))}")).Status, Is.EqualTo(400));
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=hull&rev=1&key={Esc(Key("allocate", "hull", 0))}")).Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task KeyFromAnotherSession_Returns400()
    {
        var other = await CreateSessionAsync("eic");
        var key = LocalPilotSkillAction.CreateFormKey(other.Dosid, "allocate", "hull", 0, Now);
        var result = await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}");
        Assert.That(result.Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ExpiredKey_Returns409()
    {
        var key = LocalPilotSkillAction.CreateFormKey(_session.Dosid, "allocate", "hull", 0,
            Now - LocalPilotSkillAction.FormLifetime - TimeSpan.FromSeconds(1));
        var result = await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(409));
            Assert.That(result.Html, Does.Contain("expired"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task FutureKeyBeyondSkew_Returns400()
    {
        var key = LocalPilotSkillAction.CreateFormKey(_session.Dosid, "allocate", "hull", 0, Now + TimeSpan.FromMinutes(10));
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}")).Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task SignedUnknownSkill_Returns404()
    {
        var key = Key("allocate", "warp", 0);
        var result = await Post("internalPilotSkillAllocate", $"skill=warp&rev=0&key={Esc(key)}");
        Assert.That(result.Status, Is.EqualTo(404));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task WrongCaseSkillId_Returns404()
    {
        var key = Key("allocate", "Hull", 0);
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=Hull&rev=0&key={Esc(key)}")).Status, Is.EqualTo(404));
        await AssertUnchangedAsync();
    }

    [TestCase("skill=hull&rev=0")]
    [TestCase("skill=hull&key=K")]
    [TestCase("rev=0&key=K")]
    [TestCase("skill=hull&rev=0&key=K&extra=1")]
    [TestCase("skill=hull&skill=hull&rev=0&key=K")]
    [TestCase("skill=hull&rev=0&rev=0&key=K")]
    [TestCase("skill=%20&rev=0&key=K")]
    [TestCase("skill=hull&rev=-1&key=K")]
    [TestCase("skill=hull&rev=%2B0&key=K")]
    [TestCase("skill=hull&rev=1000001&key=K")]
    [TestCase("skill=hull&rev=99999999&key=K")]
    [TestCase("skill=hull&rev=0x1&key=K")]
    public async Task MalformedAllocateForm_Returns400(string body)
    {
        var result = await Post("internalPilotSkillAllocate", body.Replace("key=K", $"key={Esc(Key("allocate", "hull", 0))}"));
        Assert.That(result.Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [TestCase("rev=0")]
    [TestCase("key=K")]
    [TestCase("skill=hull&rev=0&key=K")]
    [TestCase("rev=0&key=K&key=K")]
    public async Task MalformedResetForm_Returns400(string body)
    {
        Assert.That((await Allocate("hull", 0)).Status, Is.EqualTo(303));
        var result = await Post("internalPilotSkillReset", body.Replace("key=K", $"key={Esc(Key("reset", "", 1))}").Replace("rev=0", "rev=1"));
        Assert.That(result.Status, Is.EqualTo(400));
        Assert.That((await State()).Ranks["hull"], Is.EqualTo(1));
    }

    [Test]
    public async Task OversizedKeyOrSkill_Returns400()
    {
        Assert.That((await Post("internalPilotSkillAllocate", $"skill=hull&rev=0&key={new string('a', 161)}")).Status, Is.EqualTo(400));
        var longSkill = new string('s', 65);
        Assert.That((await Post("internalPilotSkillAllocate", $"skill={longSkill}&rev=0&key={Esc(Key("allocate", longSkill, 0))}")).Status, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        var context = Build("internalPilotSkillAllocate", "{}");
        context.Request.ContentType = "application/json";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task SessionWithoutCompany_Returns403()
    {
        var session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "nocompany", null, 9), null, CancellationToken.None)).Session;
        var key = LocalPilotSkillAction.CreateFormKey(session.Dosid, "allocate", "hull", 0, Now);
        var context = Build("internalPilotSkillAllocate", $"skill=hull&rev=0&key={Esc(key)}", session.Dosid);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(403));
        Assert.That((await _skills.GetAsync(session.AccountId, CancellationToken.None)).Spent, Is.Zero);
    }

    [TestCase("internalPilotSkillallocate")]
    [TestCase("INTERNALPILOTSKILLRESET")]
    public async Task WrongCaseActionValue_Returns404(string action)
    {
        var context = Build(action, $"skill=hull&rev=0&key={Esc(Key("allocate", "hull", 0))}");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ErrorPage_EncodesNothingFromTheRequest()
    {
        var result = await Post("internalPilotSkillAllocate", $"skill=%3Cscript%3E&rev=0&key={Esc(Key("allocate", "<script>", 0))}");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(404));
            Assert.That(result.Html, Does.Not.Contain("<script>"));
            Assert.That(result.Html, Does.Contain("Return to Pilot Sheet"));
        });
    }

    [Test]
    public void RuleConflict_ReturnsNullForStaleRevisionOrAvailableSkill()
    {
        var state = new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 1 }, 4);
        var hull = LocalPilotSkillStore.Find("hull");
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSkillAction.RuleConflict(state, 3, hull), Is.Null);
            Assert.That(LocalPilotSkillAction.RuleConflict(state, 4, hull), Is.Null);
            Assert.That(LocalPilotSkillAction.RuleConflict(state, 4, null), Is.Null);
            Assert.That(LocalPilotSkillAction.RuleConflict(state with { Ranks = new() }, 4, null), Does.Contain("no spent"));
        });
    }

    private Task<(int Status, string Html, DefaultHttpContext Context)> Allocate(string skill, int rev) =>
        Post("internalPilotSkillAllocate", $"skill={skill}&rev={rev}&key={Esc(Key("allocate", skill, rev))}");

    private Task<(int Status, string Html, DefaultHttpContext Context)> Reset(int rev) =>
        Post("internalPilotSkillReset", $"rev={rev}&key={Esc(Key("reset", "", rev))}");

    private string Key(string kind, string skill, int rev) =>
        LocalPilotSkillAction.CreateFormKey(_session.Dosid, kind, skill, rev, Now);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private async Task<(int Status, string Html, DefaultHttpContext Context)> Post(string action, string body)
    {
        var context = Build(action, body);
        await Handle(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync(), context);
    }

    private Task<LocalPilotSkillState> State() => _skills.GetAsync(_session.AccountId, CancellationToken.None);

    private async Task AssertUnchangedAsync()
    {
        var state = await State();
        Assert.That(state.Spent, Is.Zero);
        Assert.That(state.Revision, Is.Zero);
    }

    private async Task<GameAuthSession> CreateSessionAsync(string company)
    {
        var session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(session.Dosid, company, CancellationToken.None), Is.True);
        return session;
    }

    private Task Handle(HttpContext context) =>
        LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, new FixedClock(Now), CancellationToken.None);

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
