using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSheetTests
{
    [Test]
    public async Task Allocation_IsAccountScopedAndBounded_AndResetRestoresPoints()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalPilotSkillStore(services.GetRequiredService<IDistributedCache>());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.That(await store.AllocateAsync(first, "unknown", CancellationToken.None), Is.False);
        for (var i = 0; i < 5; i++)
            Assert.That(await store.AllocateAsync(first, "hull", CancellationToken.None), Is.True);
        Assert.That(await store.AllocateAsync(first, "hull", CancellationToken.None), Is.False);
        for (var i = 0; i < 5; i++)
            Assert.That(await store.AllocateAsync(first, "shields", CancellationToken.None), Is.True);
        Assert.That(await store.AllocateAsync(first, "lasers", CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(first, CancellationToken.None)).Spent, Is.EqualTo(10));
        Assert.That((await store.GetAsync(second, CancellationToken.None)).Spent, Is.Zero);
        Assert.That(await store.ResetAsync(first, CancellationToken.None), Is.True);
        Assert.That(await store.ResetAsync(first, CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(first, CancellationToken.None)).Spent, Is.Zero);
    }

    [Test]
    public async Task Actions_RequireSessionCompanyAndValidSkill()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var skills = new LocalPilotSkillStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalPilotSkillAllocate", "skill=hull");
        await LocalPilotSkillAction.HandleAsync(anonymous, sessions, skills, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalPilotSkillAllocate", "skill=hull");
        await LocalPilotSkillAction.HandleAsync(noCompany, sessions, skills, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var invalid = Request(session.Dosid, "internalPilotSkillAllocate", "skill=unknown");
        await LocalPilotSkillAction.HandleAsync(invalid, sessions, skills, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        var allocate = Request(session.Dosid, "internalPilotSkillAllocate", AllocateBody(session.Dosid, "hull", 0));
        await LocalPilotSkillAction.HandleAsync(allocate, sessions, skills, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(allocate.Response.StatusCode, Is.EqualTo(303));
            Assert.That(allocate.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalPilotSheet&trained=hull"));
        });
        var reset = Request(session.Dosid, "internalPilotSkillReset", ResetBody(session.Dosid, 1));
        await LocalPilotSkillAction.HandleAsync(reset, sessions, skills, CancellationToken.None);
        Assert.That(reset.Response.StatusCode, Is.EqualTo(303));
        Assert.That(reset.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalPilotSheet&reset=1"));
        Assert.That((await skills.GetAsync(session.AccountId, CancellationToken.None)).Spent, Is.Zero);
    }

    [Test]
    public void Page_ShowsIdentitySkillsAndEscapesPilotName()
    {
        var page = LocalPilotSheet.Page(new GameAuthSession
        { Username = "<pilot>", GameUserId = 7, Company = "mmo" },
            new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 1 }));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"pilotSheet\""));
            Assert.That(page, Does.Contain("Available points: 9 / 10"));
            Assert.That(page, Does.Contain("Hull Training"));
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(page, Does.Not.Contain("<pilot>"));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalPilotSheet"));
        });
    }

    [Test]
    public async Task Actions_RejectTamperedMissingExtraAndOversizedFields()
    {
        var (sessions, skills, session) = await SetupAsync();
        var key = LocalPilotSkillAction.CreateFormKey(session.Dosid, "allocate", "hull", 0);
        string[] bodies =
        [
            "skill=hull&rev=0",
            $"skill=hull&key={key}",
            $"skill=hull&rev=0&key={key}&points=-5",
            $"skill=hull&skill=hull&rev=0&key={key}",
            $"skill=lasers&rev=0&key={key}",
            $"skill=hull&rev=-1&key={key}",
            $"skill=hull&rev=99999999999&key={key}",
            $"skill=hull&rev=0&key={key[..^1]}{(key[^1] == '0' ? '1' : '0')}",
            $"skill=hull&rev=0&key=abc",
            $"skill={new string('a', 65)}&rev=0&key={key}",
            $"skill=hull&rev=0&key={new string('a', 161)}",
            $"rev=0&key={LocalPilotSkillAction.CreateFormKey(session.Dosid, "reset", "", 0)}"
        ];
        foreach (var body in bodies)
        {
            var request = Request(session.Dosid, "internalPilotSkillAllocate", body);
            await LocalPilotSkillAction.HandleAsync(request, sessions, skills, CancellationToken.None);
            Assert.That(request.Response.StatusCode, Is.EqualTo(400), body);
        }
        var foreignKey = LocalPilotSkillAction.CreateFormKey("other-dosid", "allocate", "hull", 0);
        var foreign = Request(session.Dosid, "internalPilotSkillAllocate", $"skill=hull&rev=0&key={foreignKey}");
        await LocalPilotSkillAction.HandleAsync(foreign, sessions, skills, CancellationToken.None);
        var extraQuery = Request(session.Dosid, "internalPilotSkillAllocate&skill=hull", AllocateBody(session.Dosid, "hull", 0));
        await LocalPilotSkillAction.HandleAsync(extraQuery, sessions, skills, CancellationToken.None);
        var unknown = Request(session.Dosid, "internalPilotSkillAllocate", AllocateBody(session.Dosid, "warp", 0));
        await LocalPilotSkillAction.HandleAsync(unknown, sessions, skills, CancellationToken.None);
        var spentAfterRejects = (await skills.GetAsync(session.AccountId, CancellationToken.None)).Spent;
        Assert.Multiple(() =>
        {
            Assert.That(foreign.Response.StatusCode, Is.EqualTo(400));
            Assert.That(extraQuery.Response.StatusCode, Is.EqualTo(400));
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
            Assert.That(foreign.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(spentAfterRejects, Is.Zero);
        });
    }

    [Test]
    public async Task Actions_RejectReplayStaleRevisionAndExpiredForms()
    {
        var (sessions, skills, session) = await SetupAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var body = AllocateBody(session.Dosid, "hull", 0, clock.Now);
        var first = Request(session.Dosid, "internalPilotSkillAllocate", body);
        await LocalPilotSkillAction.HandleAsync(first, sessions, skills, clock, CancellationToken.None);
        var replay = Request(session.Dosid, "internalPilotSkillAllocate", body);
        await LocalPilotSkillAction.HandleAsync(replay, sessions, skills, clock, CancellationToken.None);
        var stale = Request(session.Dosid, "internalPilotSkillAllocate", AllocateBody(session.Dosid, "shields", 0, clock.Now));
        await LocalPilotSkillAction.HandleAsync(stale, sessions, skills, clock, CancellationToken.None);
        var expired = Request(session.Dosid, "internalPilotSkillAllocate",
            AllocateBody(session.Dosid, "hull", 1, clock.Now - LocalPilotSkillAction.FormLifetime - TimeSpan.FromSeconds(1)));
        await LocalPilotSkillAction.HandleAsync(expired, sessions, skills, clock, CancellationToken.None);
        var future = Request(session.Dosid, "internalPilotSkillAllocate",
            AllocateBody(session.Dosid, "hull", 1, clock.Now + TimeSpan.FromHours(1)));
        await LocalPilotSkillAction.HandleAsync(future, sessions, skills, clock, CancellationToken.None);
        var emptyReset = Request(session.Dosid, "internalPilotSkillReset", ResetBody(session.Dosid, 1, clock.Now));
        await LocalPilotSkillAction.HandleAsync(emptyReset, sessions, skills, clock, CancellationToken.None);
        var state = await skills.GetAsync(session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(stale.Response.StatusCode, Is.EqualTo(409));
            Assert.That(expired.Response.StatusCode, Is.EqualTo(409));
            Assert.That(future.Response.StatusCode, Is.EqualTo(400));
            Assert.That(emptyReset.Response.StatusCode, Is.EqualTo(303));
            Assert.That(state.Spent, Is.Zero);
            Assert.That(state.Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Actions_RequireValidCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var skills = new LocalPilotSkillStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        await sessions.SelectCompanyAsync(session.Dosid, "pirates", CancellationToken.None);
        var request = Request(session.Dosid, "internalPilotSkillAllocate", AllocateBody(session.Dosid, "hull", 0));
        await LocalPilotSkillAction.HandleAsync(request, sessions, skills, CancellationToken.None);
        var expiredSession = Request("missing-dosid", "internalPilotSkillAllocate", AllocateBody("missing-dosid", "hull", 0));
        await LocalPilotSkillAction.HandleAsync(expiredSession, sessions, skills, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(request.Response.StatusCode, Is.EqualTo(403));
            Assert.That(expiredSession.Response.StatusCode, Is.EqualTo(401));
        });
    }

    [Test]
    public async Task Prerequisites_GateAdvancedSkills()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalPilotSkillStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.AllocateAsync(account, "evasion", CancellationToken.None), Is.False);
        for (var i = 0; i < 3; i++) Assert.That(await store.AllocateAsync(account, "hull", CancellationToken.None), Is.True);
        for (var i = 0; i < 3; i++) Assert.That(await store.AllocateAsync(account, "evasion", CancellationToken.None), Is.True);
        Assert.That(await store.AllocateAsync(account, "evasion", CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Ranks["evasion"], Is.EqualTo(3));
    }

    [Test]
    public async Task ConcurrentAllocations_NeverExceedPointBudget()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalPilotSkillStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            store.AllocateAsync(account, LocalPilotSkillStore.Skills[i % 3].Id, CancellationToken.None)));
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(results.Count(applied => applied), Is.EqualTo(LocalPilotSkillState.TotalPoints));
            Assert.That(state.Spent, Is.EqualTo(LocalPilotSkillState.TotalPoints));
            Assert.That(state.Available, Is.Zero);
        });
    }

    [Test]
    public async Task CorruptOrExpiredCache_YieldsSafeState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalPilotSkillStore(cache);
        var account = Guid.NewGuid();
        await cache.SetStringAsync($"localpilotskills:{account:N}",
            "{\"Ranks\":{\"hull\":-4,\"shields\":99,\"unknown\":3},\"Revision\":-2}");
        var clamped = await store.GetAsync(account, CancellationToken.None);
        await cache.SetStringAsync($"localpilotskills:{account:N}", "not json");
        var corrupt = await store.GetAsync(account, CancellationToken.None);
        await cache.RemoveAsync($"localpilotskills:{account:N}");
        var expired = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(clamped.Ranks, Is.EquivalentTo(new Dictionary<string, int> { ["shields"] = 5 }));
            Assert.That(clamped.Revision, Is.Zero);
            Assert.That(corrupt.Spent, Is.Zero);
            Assert.That(expired.Available, Is.EqualTo(LocalPilotSkillState.TotalPoints));
        });
    }

    [Test]
    public void Page_ShowsPrerequisitesSignedFormsAndNoEconomyCost()
    {
        var page = LocalPilotSheet.Page(new GameAuthSession { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" },
            new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 3 }, 4));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Requires Hull Training rank 3"));
            Assert.That(page, Does.Contain("name=\"rev\" value=\"4\""));
            Assert.That(page, Does.Contain("name=\"key\""));
            Assert.That(page, Does.Contain("Rocket Fusion"));
            Assert.That(page, Does.Contain("change no Credits"));
            Assert.That(page, Does.Contain("internalPilotSkillReset"));
        });
    }

    private static async Task<(GameAuthSessionStore, LocalPilotSkillStore, GameAuthSession)> SetupAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None);
        return (sessions, new LocalPilotSkillStore(cache), session);
    }

    private static string AllocateBody(string dosid, string skill, int rev, DateTimeOffset? issued = null) =>
        $"skill={skill}&rev={rev}&key={LocalPilotSkillAction.CreateFormKey(dosid, "allocate", skill, rev, issued)}";

    private static string ResetBody(string dosid, int rev, DateTimeOffset? issued = null) =>
        $"rev={rev}&key={LocalPilotSkillAction.CreateFormKey(dosid, "reset", "", rev, issued)}";

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
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
