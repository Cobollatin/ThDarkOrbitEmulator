using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSheetDetailTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "d" };

    private static LocalPilotSkillState State(int revision = 0, params (string Skill, int Rank)[] ranks) =>
        new(ranks.ToDictionary(r => r.Skill, r => r.Rank), revision);

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("?action=internalPilotSheet&detail=hull", true)]
    [TestCase("?action=internalPilotSheet&from=internalStart&detail=rocketFusion", true)]
    [TestCase("?action=internalPilotSheet&detail=unknownSkill", true)]
    [TestCase("?action=internalPilotSheet&detail=", false)]
    [TestCase("?action=internalPilotSheet&detail", false)]
    [TestCase("?action=internalPilotSheet&detail=hull&detail=hull", false)]
    [TestCase("?action=internalPilotSheet&detail=hull&Detail=lasers", false)]
    [TestCase("?action=internalPilotSheet&Detail=hull", false)]
    [TestCase("?action=internalPilotSheet&DETAIL=hull", false)]
    [TestCase("?action=internalPilotSheet&detail=hu%0All", false)]
    [TestCase("?action=internalPilotSheet&detail=hull&skill=hull", false)]
    public void PageQuery_DetailRules(string query, bool expected) =>
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query(query)), Is.EqualTo(expected), query);

    [Test]
    public void PageQuery_DetailLengthIsBounded()
    {
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query("?action=internalPilotSheet&detail=" + new string('a', 64))), Is.True);
        Assert.That(LocalPilotSkillAction.ValidPageQuery(Query("?action=internalPilotSheet&detail=" + new string('a', 65))), Is.False);
    }

    [TestCase("?action=internalPilotSheet", true, null)]
    [TestCase("?action=internalPilotSheet&detail=hull", true, "hull")]
    [TestCase("?action=internalPilotSheet&detail=shieldEngineering", true, "shieldEngineering")]
    [TestCase("?action=internalPilotSheet&detail=Hull", false, null)]
    [TestCase("?action=internalPilotSheet&detail=shieldengineering", false, null)]
    [TestCase("?action=internalPilotSheet&detail=warp", false, null)]
    public void TryFindDetail_IsExactCase(string query, bool found, string? skillId)
    {
        var ok = LocalPilotSkillAction.TryFindDetail(Query(query), out var skill);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.EqualTo(found));
            Assert.That(skill?.Id, Is.EqualTo(skillId));
        });
    }

    [Test]
    public void Status_FollowsMaxedLockedNoPointsAvailableOrder()
    {
        var skills = LocalPilotSkillStore.Skills.ToDictionary(s => s.Id);
        var fresh = State();
        var full = State(0, ("hull", 5), ("shields", 5));
        Assert.Multiple(() =>
        {
            Assert.That(LocalPilotSkillStore.Status(skills["hull"], fresh), Is.EqualTo("available"));
            Assert.That(LocalPilotSkillStore.Status(skills["evasion"], fresh), Is.EqualTo("locked"));
            Assert.That(LocalPilotSkillStore.Status(skills["hull"], full), Is.EqualTo("maxed"));
            Assert.That(LocalPilotSkillStore.Status(skills["lasers"], full), Is.EqualTo("no-points"));
            Assert.That(LocalPilotSkillStore.Status(skills["rocketFusion"], full), Is.EqualTo("locked"));
            Assert.That(LocalPilotSkillStore.Status(skills["evasion"], full), Is.EqualTo("no-points"));
            Assert.That(LocalPilotSkillStore.Status(skills["evasion"], State(0, ("hull", 3), ("evasion", 3))), Is.EqualTo("maxed"));
        });
    }

    [Test]
    public void Bonus_ScalesPerRankAndIsClamped()
    {
        var hull = LocalPilotSkillStore.Find("hull")!;
        var engineering = LocalPilotSkillStore.Find("shieldEngineering")!;
        Assert.Multiple(() =>
        {
            Assert.That(hull.Bonus(0), Is.Zero);
            Assert.That(hull.Bonus(3), Is.EqualTo(6));
            Assert.That(hull.Bonus(99), Is.EqualTo(10));
            Assert.That(hull.Bonus(-4), Is.Zero);
            Assert.That(engineering.Bonus(3), Is.EqualTo(9));
            Assert.That(LocalPilotSkillStore.Skills.All(s => s.EffectPerRank > 0 && s.EffectUnit.Length > 0), Is.True);
            Assert.That(LocalPilotSkillStore.UnlockedBy(hull).Select(s => s.Id), Is.EqualTo(new[] { "evasion" }));
            Assert.That(LocalPilotSkillStore.UnlockedBy(engineering), Is.Empty);
        });
    }

    [Test]
    public void Page_MarksRowsWithStatusAndBonus_AndLinksDetails()
    {
        var page = LocalPilotSheet.Page(Session, State(2, ("hull", 3)));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-skill=\"hull\" data-status=\"available\">"));
            Assert.That(page, Does.Contain("<tr data-skill=\"evasion\" data-status=\"available\">"));
            Assert.That(page, Does.Contain("<tr data-skill=\"rocketFusion\" data-status=\"locked\">"));
            Assert.That(page, Does.Contain("<td>3 / 5</td><td>+6% hull points</td>"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalPilotSheet&amp;detail=shieldEngineering\""));
            Assert.That(page, Does.Contain("<ul id=\"pilotSkillBonuses\"><li data-skill=\"hull\">Hull Training: +6% hull points</li></ul>"));
            Assert.That(page, Does.Contain("Spent points: 3"));
            Assert.That(page, Does.Not.Contain("id=\"pilotSkillDetail\""));
            Assert.That(page, Does.Not.Contain("aria-current=\"true\""));
            Assert.That(Regex.Matches(page, "<tr data-skill=").Count, Is.EqualTo(LocalPilotSkillStore.Skills.Count));
        });
    }

    [Test]
    public void Page_WithoutTraining_ShowsNoBonuses()
    {
        var page = LocalPilotSheet.Page(Session, State());
        Assert.That(page, Does.Contain("<p id=\"pilotSkillBonuses\">No skill bonuses yet.</p>"));
        Assert.That(page, Does.Not.Contain("internalPilotSkillReset"));
    }

    [TestCase("mmo", "Company: Mars Mining Operations TEST</p>")]
    [TestCase("eic", "Company: Earth Industries Corporation TEST</p>")]
    [TestCase("vru", "Company: Venus Resources Unlimited TEST</p>")]
    public void Page_ShowsCompanyDisplayName(string code, string expected)
    {
        var page = LocalPilotSheet.Page(new GameAuthSession { Username = "p", GameUserId = 1, Company = code, Dosid = "d" }, State());
        Assert.That(page, Does.Contain(expected));
    }

    [Test]
    public void Detail_ShowsRankBonusNextRequirementsAndSignedForm()
    {
        var evasion = LocalPilotSkillStore.Find("evasion")!;
        var page = LocalPilotSheet.Page(Session, State(5, ("hull", 3), ("evasion", 1)), detail: evasion);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<section id=\"pilotSkillDetail\" data-skill=\"evasion\" data-status=\"available\">"));
            Assert.That(page, Does.Contain("<tr data-skill=\"evasion\" data-status=\"available\" aria-current=\"true\">"));
            Assert.That(page, Does.Contain("<dd id=\"detailRank\">1 / 3</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"detailPerRank\">+1% evasion chance</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"detailBonus\">+1% evasion chance</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"detailNext\">+2% evasion chance</dd>"));
            Assert.That(page, Does.Contain("detail=hull\">Hull Training</a> rank 3 (met)"));
            Assert.That(page, Does.Contain("<dd id=\"detailUnlocks\">None</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"detailStatus\">Can train</dd>"));
            Assert.That(page, Does.Contain("<button type=\"submit\" id=\"detailAllocate\">"));
            Assert.That(page, Does.Contain("id=\"detailClose\" href=\"/indexInternal.es?action=internalPilotSheet\""));
            Assert.That(Regex.Matches(page, "name=\"skill\" value=\"evasion\"><input type=\"hidden\" name=\"rev\" value=\"5\">").Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void Detail_LockedBaseRequirementAndUnlocksList()
    {
        var page = LocalPilotSheet.Page(Session, State(), detail: LocalPilotSkillStore.Find("rocketFusion")!);
        var hull = LocalPilotSheet.Page(Session, State(), detail: LocalPilotSkillStore.Find("hull")!);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-status=\"locked\"><h2>Rocket Fusion</h2>"));
            Assert.That(page, Does.Contain("Laser Training</a> rank 3 (not met)"));
            Assert.That(page, Does.Contain("<dd id=\"detailStatus\">Locked</dd>"));
            Assert.That(page, Does.Contain("id=\"detailAllocate\" disabled>"));
            Assert.That(hull, Does.Contain("<dd id=\"detailRequires\">None</dd>"));
            Assert.That(hull, Does.Contain("detail=evasion\">Evasion</a> at rank 3</dd>"));
            Assert.That(hull, Does.Contain("<dd id=\"detailBonus\">+0% hull points</dd>"));
        });
    }

    [Test]
    public void Detail_MaxedAndNoPointsStates()
    {
        var full = State(9, ("hull", 5), ("shields", 5));
        var maxed = LocalPilotSheet.Page(Session, full, detail: LocalPilotSkillStore.Find("hull")!);
        var noPoints = LocalPilotSheet.Page(Session, full, detail: LocalPilotSkillStore.Find("lasers")!);
        Assert.Multiple(() =>
        {
            Assert.That(maxed, Does.Contain("<dd id=\"detailNext\">Maximum rank reached</dd>"));
            Assert.That(maxed, Does.Contain("<dd id=\"detailStatus\">Maximum rank</dd>"));
            Assert.That(maxed, Does.Contain("<dd id=\"detailBonus\">+10% hull points</dd>"));
            Assert.That(maxed, Does.Contain("id=\"detailAllocate\" disabled>"));
            Assert.That(noPoints, Does.Contain("<dd id=\"detailStatus\">No points left</dd>"));
            Assert.That(noPoints, Does.Contain("Available points: 0 / 10"));
            Assert.That(noPoints, Does.Contain("id=\"detailAllocate\" disabled>"));
        });
    }
}
