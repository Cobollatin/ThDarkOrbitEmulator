using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-127: Pilot skill training/reset confirmations. Emulator-defined; no Live Pilot Sheet form was captured.
public class LocalPilotSkillNoticeTests
{
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
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
        _session = (await _sessions.TryGetByDosidAsync(_session.Dosid, CancellationToken.None))!;
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalPilotSheet&trained=hull", true)]
    [TestCase("?action=internalPilotSheet&trained=shieldEngineering", true)]
    [TestCase("?action=internalPilotSheet&trained=unknownSkill9", true)]
    [TestCase("?action=internalPilotSheet&reset=1", true)]
    [TestCase("?action=internalPilotSheet&trained=hull&detail=evasion&from=internalStart", true)]
    [TestCase("?action=internalPilotSheet&reset=1&detail=hull", true)]
    [TestCase("?action=internalPilotSheet&trained=hull&reset=1", false)]
    [TestCase("?action=internalPilotSheet&trained=hull&trained=lasers", false)]
    [TestCase("?action=internalPilotSheet&reset=1&reset=1", false)]
    [TestCase("?action=internalPilotSheet&Trained=hull", false)]
    [TestCase("?action=internalPilotSheet&RESET=1", false)]
    [TestCase("?action=internalPilotSheet&trained=", false)]
    [TestCase("?action=internalPilotSheet&trained=Hull", false)]
    [TestCase("?action=internalPilotSheet&trained=9hull", false)]
    [TestCase("?action=internalPilotSheet&trained=shield-engineering", false)]
    [TestCase("?action=internalPilotSheet&trained=%3Cscript%3E", false)]
    [TestCase("?action=internalPilotSheet&trained=hull%0A", false)]
    [TestCase("?action=internalPilotSheet&reset=", false)]
    [TestCase("?action=internalPilotSheet&reset=0", false)]
    [TestCase("?action=internalPilotSheet&reset=true", false)]
    public void PageQuery_ValidatesNoticeParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalPilotSkillAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongTrainedValue()
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery($"?action=internalPilotSheet&trained={new string('a', 65)}"));
        Assert.That(LocalPilotSkillAction.ValidPageQuery(parsed), Is.False);
    }

    [Test]
    public void SuccessLocation_NamesTrainedSkillOrReset()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSkillAction.SuccessLocation("lasers"),
                Is.EqualTo("/indexInternal.es?action=internalPilotSheet&trained=lasers"));
            Assert.That(LocalPilotSkillAction.SuccessLocation(null),
                Is.EqualTo("/indexInternal.es?action=internalPilotSheet&reset=1"));
        });
    }

    [Test]
    public void SuccessLocations_PassPageValidation()
    {
        foreach (var location in LocalPilotSkillStore.Skills.Select(skill => LocalPilotSkillAction.SuccessLocation(skill.Id))
                     .Append(LocalPilotSkillAction.SuccessLocation(null)))
        {
            var query = new QueryCollection(QueryHelpers.ParseQuery(new Uri("http://localhost" + location).Query));
            Assert.That(LocalPilotSkillAction.ValidPageQuery(query), Is.True, location);
        }
    }

    [Test]
    public void TrainedNotice_ReportsRankBonusAndPointsLeft()
    {
        var state = State(("hull", 2));
        Assert.That(LocalPilotSheet.Notice(state, "hull", false), Is.EqualTo(
            "<p id=\"pilotSkillTrained\" role=\"status\" data-skill=\"hull\">Hull Training trained to rank 2 / 5. " +
            "Bonus: +4% hull points. Points left: 8.</p>"));
    }

    [Test]
    public void TrainedNotice_NamesUnlockAtRequiredRank()
    {
        var notice = LocalPilotSheet.Notice(State(("shields", 3)), "shields", false);
        Assert.That(notice, Does.Contain(" Unlocked: Shield Engineering."));
        Assert.That(LocalPilotSheet.Notice(State(("shields", 4)), "shields", false), Does.Not.Contain("Unlocked"));
    }

    [Test]
    public void TrainedNotice_MarksMaximumRank()
    {
        var notice = LocalPilotSheet.Notice(State(("lasers", 3), ("rocketFusion", 3)), "rocketFusion", false);
        Assert.That(notice, Does.Contain("Rocket Fusion trained to rank 3 / 3. Bonus: +6% rocket damage. Points left: 4. Maximum rank reached."));
    }

    [Test]
    public void TrainedNotice_HiddenWithoutRankOrForUnknownSkill()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSheet.Notice(State(), "hull", false), Is.Empty);
            Assert.That(LocalPilotSheet.Notice(State(("hull", 1)), "lasers", false), Is.Empty);
            Assert.That(LocalPilotSheet.Notice(State(("hull", 1)), "unknownSkill", false), Is.Empty);
            Assert.That(LocalPilotSheet.Notice(State(("hull", 1)), null, false), Is.Empty);
        });
    }

    [Test]
    public void ResetNotice_ShowsOnlyWhileNothingIsSpent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSheet.Notice(State(), null, true), Is.EqualTo(
                "<p id=\"pilotSkillReset\" role=\"status\">Skills reset. Available points: 10 / 10.</p>"));
            Assert.That(LocalPilotSheet.Notice(State(("hull", 1)), null, true), Is.Empty);
        });
    }

    [Test]
    public void Page_RendersNoticeOnlyWhenRequested()
    {
        var state = State(("hull", 1));
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSheet.Page(_session, state, trained: "hull"), Does.Contain("id=\"pilotSkillTrained\""));
            Assert.That(LocalPilotSheet.Page(_session, state), Does.Not.Contain("pilotSkillTrained"));
            Assert.That(LocalPilotSheet.Page(_session, state), Does.Not.Contain("pilotSkillReset\""));
        });
    }

    [Test]
    public async Task AllocateThenReset_RedirectsAndNoticesMatchSavedState()
    {
        var allocate = Build("internalPilotSkillAllocate", Body("allocate", "hull", 0));
        await Handle(allocate);
        Assert.That(allocate.Response.StatusCode, Is.EqualTo(303));
        Assert.That(allocate.Response.Headers.Location.ToString(), Is.EqualTo(LocalPilotSkillAction.SuccessLocation("hull")));
        var state = await _skills.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(LocalPilotSheet.Notice(state, "hull", false), Does.Contain("rank 1 / 5"));

        var reset = Build("internalPilotSkillReset", Body("reset", "", 1));
        await Handle(reset);
        Assert.That(reset.Response.StatusCode, Is.EqualTo(303));
        Assert.That(reset.Response.Headers.Location.ToString(), Is.EqualTo(LocalPilotSkillAction.SuccessLocation(null)));
        state = await _skills.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSheet.Notice(state, null, true), Does.Contain("Skills reset."));
            Assert.That(LocalPilotSheet.Notice(state, "hull", false), Is.Empty);
        });
    }

    [Test]
    public async Task RejectedAllocate_DoesNotRedirect()
    {
        var context = Build("internalPilotSkillAllocate", Body("allocate", "evasion", 0));
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task RejectedReset_DoesNotRedirect()
    {
        var context = Build("internalPilotSkillReset", Body("reset", "", 0));
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    private static LocalPilotSkillState State(params (string Skill, int Rank)[] ranks) =>
        new(ranks.ToDictionary(rank => rank.Skill, rank => rank.Rank));

    private string Body(string kind, string skill, int revision)
    {
        var key = Uri.EscapeDataString(LocalPilotSkillAction.CreateFormKey(_session.Dosid, kind, skill, revision));
        return kind == "allocate" ? $"skill={skill}&rev={revision}&key={key}" : $"rev={revision}&key={key}";
    }

    private Task Handle(HttpContext context) =>
        LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);

    private DefaultHttpContext Build(string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={_session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
