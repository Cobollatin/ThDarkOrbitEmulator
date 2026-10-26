using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalClanDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static GameAuthSession Session(string company = "mmo") =>
        new() { Dosid = "d", Username = "pilot", Company = company };

    private static bool ValidQuery(string query) => LocalClanAction.ValidPageQuery(
        new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)));

    [TestCase("?action=internalClan&detail=mmo-guard")]
    [TestCase("?action=internalClan&detail=eic-orbit")]
    [TestCase("?action=internalClan&detail=retired-clan-9")]
    [TestCase("?action=internalClan&filter=all")]
    [TestCase("?action=internalClan&filter=recruiting")]
    [TestCase("?action=internalClan&filter=closed")]
    [TestCase("?action=internalClan&filter=")]
    [TestCase("?action=internalClan&filter=closed&detail=mmo-elite&from=internalStart")]
    public void PageQuery_AcceptsDetailAndFilter(string query) => Assert.That(ValidQuery(query), Is.True, query);

    [TestCase("?action=internalClan&detail=")]
    [TestCase("?action=internalClan&detail=MMO-GUARD")]
    [TestCase("?action=internalClan&detail=mmo_guard")]
    [TestCase("?action=internalClan&detail=-mmo")]
    [TestCase("?action=internalClan&detail=mmo-")]
    [TestCase("?action=internalClan&detail=mmo--guard")]
    [TestCase("?action=internalClan&detail=mmo%20guard")]
    [TestCase("?action=internalClan&detail=%3Cscript%3E")]
    [TestCase("?action=internalClan&detail=mmo-guard&detail=mmo-orbit")]
    [TestCase("?action=internalClan&DETAIL=mmo-guard")]
    [TestCase("?action=internalClan&Detail=mmo-guard")]
    [TestCase("?action=internalClan&filter=open")]
    [TestCase("?action=internalClan&filter=ALL")]
    [TestCase("?action=internalClan&filter=Recruiting")]
    [TestCase("?action=internalClan&filter=all&filter=closed")]
    [TestCase("?action=internalClan&FILTER=closed")]
    [TestCase("?action=internalClan&filter=closed%0A")]
    public void PageQuery_RejectsMalformedDetailOrFilter(string query) => Assert.That(ValidQuery(query), Is.False, query);

    [Test]
    public void PageQuery_RejectsOverlongDetail() =>
        Assert.That(ValidQuery($"?action=internalClan&detail={new string('a', 65)}"), Is.False);

    [TestCase("mmo", "mmo-guard", true)]
    [TestCase("mmo", "mmo-elite", true)]
    [TestCase("eic", "eic-vanguard", true)]
    [TestCase("tst", "tst-vanguard", true)]
    [TestCase("mmo", "eic-vanguard", false)]
    [TestCase("eic", "mmo-guard", false)]
    [TestCase("mmo", "mmo-unknown", false)]
    [TestCase(null, "mmo-guard", false)]
    public void HasDetail_OnlyForClansOfTheCurrentCompany(string? company, string clan, bool expected) =>
        Assert.That(LocalClan.HasDetail(company, clan), Is.EqualTo(expected));

    [Test]
    public void Page_WithoutDetail_HasNoDetailPanelAndLinksEachClan()
    {
        var page = LocalClan.Page(Session(), null, Now);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Not.Contain("id=\"clan-detail\""));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;detail=mmo-guard\">[MG] Mars Guard</a>"));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;detail=mmo-elite\">[ME] Mars Elite</a>"));
            Assert.That(page, Does.Contain("data-clan=\"mmo-traders\""));
            Assert.That(page, Does.Not.Contain("data-clan=\"eic-vanguard\""));
        });
    }

    [Test]
    public void Page_FilterTabs_ShowCountsAndMarkCurrent()
    {
        var page = LocalClan.Page(Session(), null, Now);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<strong aria-current=\"page\" data-filter=\"all\">All (4)</strong>"));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;filter=recruiting\" data-filter=\"recruiting\">Recruiting (3)</a>"));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;filter=closed\" data-filter=\"closed\">Closed (1)</a>"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("all")]
    public void Page_DefaultFilter_ListsEveryCompanyClan(string? filter)
    {
        var page = LocalClan.Page(Session(), null, Now, filter: filter);
        Assert.Multiple(() =>
        {
            foreach (var id in new[] { "mmo-guard", "mmo-orbit", "mmo-traders", "mmo-elite" })
                Assert.That(page, Does.Contain($"<tr data-clan=\"{id}\">"));
            Assert.That(page, Does.Contain("aria-current=\"page\" data-filter=\"all\""));
        });
    }

    [Test]
    public void Page_RecruitingFilter_HidesClosedClans()
    {
        var page = LocalClan.Page(Session(), null, Now, filter: "recruiting");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-clan=\"mmo-guard\">"));
            Assert.That(page, Does.Not.Contain("<tr data-clan=\"mmo-elite\">"));
            Assert.That(page, Does.Contain("aria-current=\"page\" data-filter=\"recruiting\">Recruiting (3)"));
            Assert.That(page, Does.Contain("&amp;filter=recruiting&amp;detail=mmo-orbit"));
            Assert.That(page, Does.Contain("<p id=\"clan-summary\">3 of 4 clans recruiting</p>"));
        });
    }

    [Test]
    public void Page_ClosedFilter_ShowsOnlyClosedClansWithoutJoinForms()
    {
        var page = LocalClan.Page(Session(), null, Now, filter: "closed");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<tr data-clan=\"mmo-elite\">"));
            Assert.That(page, Does.Not.Contain("<tr data-clan=\"mmo-guard\">"));
            Assert.That(page, Does.Not.Contain("action=internalClanJoin"));
            Assert.That(page, Does.Contain("Not recruiting"));
        });
    }

    [Test]
    public void Page_ClosedFilterWithoutClosedClans_ShowsEmptyRow()
    {
        var page = LocalClan.Page(Session("tst"), null, Now, filter: "closed");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"clan-empty\">No clans match this filter."));
            Assert.That(page, Does.Contain("Closed (0)"));
            Assert.That(page, Does.Not.Contain("<tr data-clan="));
        });
    }

    [Test]
    public void Detail_ShowsClanFactsForNonMember()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-guard");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<section id=\"clan-detail\" data-clan=\"mmo-guard\"><h2>[MG] Mars Guard</h2>"));
            Assert.That(page, Does.Contain("<p id=\"clan-detail-position\">Clan 1 of 4</p>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-company\">Mars Mining Operations TEST</dd>"));
            Assert.That(page, Does.Contain("<dd>Patrols and shared missions</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-members\">42</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-share\">36%</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-rank\">2 of 4</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-recruiting\">Yes</dd>"));
            Assert.That(page, Does.Contain("<p id=\"clan-detail-membership\">Joining makes you member 43.</p>"));
        });
    }

    [Test]
    public void Detail_ForNonMember_HasEnabledJoinFormSignedForThatClan()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-orbit");
        var section = page[page.IndexOf("<section id=\"clan-detail\"", StringComparison.Ordinal)..page.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("action=internalClanJoin"));
            Assert.That(section, Does.Contain("name=\"clan\" value=\"mmo-orbit\""));
            Assert.That(section, Does.Not.Contain(" disabled"));
            Assert.That(section, Does.Not.Contain("action=internalClanLeave"));
        });
    }

    [Test]
    public void Detail_ForMember_CountsPilotAndOffersLeave()
    {
        var page = LocalClan.Page(Session(), "mmo-guard", Now, "mmo-guard");
        var section = page[page.IndexOf("<section id=\"clan-detail\"", StringComparison.Ordinal)..page.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-members\">43</dd>"));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-share\">36%</dd>"));
            Assert.That(section, Does.Contain("You are a member of this clan."));
            Assert.That(section, Does.Contain("action=internalClanLeave"));
            Assert.That(section, Does.Not.Contain("action=internalClanJoin"));
        });
    }

    [Test]
    public void Detail_ForMemberOfAnotherClan_AsksToLeaveFirst()
    {
        var page = LocalClan.Page(Session(), "mmo-guard", Now, "mmo-orbit");
        var section = page[page.IndexOf("<section id=\"clan-detail\"", StringComparison.Ordinal)..page.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("Leave your current clan before joining this one."));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-members\">17</dd>"));
            Assert.That(section, Does.Not.Contain("<form"));
        });
    }

    [Test]
    public void Detail_ForClosedClan_HasNoJoinForm()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-elite");
        var section = page[page.IndexOf("<section id=\"clan-detail\"", StringComparison.Ordinal)..page.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("This clan is not recruiting."));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-recruiting\">No</dd>"));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-rank\">1 of 4</dd>"));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-share\">43%</dd>"));
            Assert.That(section, Does.Not.Contain("<form"));
        });
    }

    [Test]
    public void Detail_SmallestClan_RanksLast()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-traders");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-rank\">4 of 4</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-share\">7%</dd>"));
        });
    }

    [Test]
    public void Detail_OnlyClan_IsFullShareWithoutNeighbours()
    {
        var page = LocalClan.Page(Session("tst"), null, Now, "tst-vanguard");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<p id=\"clan-detail-position\">Clan 1 of 1</p>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-share\">100%</dd>"));
            Assert.That(page, Does.Contain("<dd id=\"clan-detail-company\">Test Company TEST</dd>"));
            Assert.That(page, Does.Not.Contain("rel=\"prev\""));
            Assert.That(page, Does.Not.Contain("rel=\"next\""));
        });
    }

    [Test]
    public void Detail_FirstAndLast_LinkOnlyToExistingNeighbours()
    {
        var first = LocalClan.Page(Session(), null, Now, "mmo-guard");
        var last = LocalClan.Page(Session(), null, Now, "mmo-elite");
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Not.Contain("rel=\"prev\""));
            Assert.That(first, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;detail=mmo-orbit\" rel=\"next\">Next</a>"));
            Assert.That(last, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;detail=mmo-traders\" rel=\"prev\">Previous</a>"));
            Assert.That(last, Does.Not.Contain("rel=\"next\""));
            Assert.That(last, Does.Contain("<p id=\"clan-detail-position\">Clan 4 of 4</p>"));
        });
    }

    [Test]
    public void Detail_KeepsFilterInNavigationAndClose()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-orbit", "recruiting");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("&amp;filter=recruiting&amp;detail=mmo-guard\" rel=\"prev\""));
            Assert.That(page, Does.Contain("&amp;filter=recruiting&amp;detail=mmo-traders\" rel=\"next\""));
            Assert.That(page, Does.Contain("<a href=\"/indexInternal.es?action=internalClan&amp;filter=recruiting\">Close</a>"));
        });
    }

    [Test]
    public void Detail_ClosedClanStaysOpenUnderRecruitingFilter()
    {
        var page = LocalClan.Page(Session(), null, Now, "mmo-elite", "recruiting");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<section id=\"clan-detail\" data-clan=\"mmo-elite\">"));
            Assert.That(page, Does.Not.Contain("<tr data-clan=\"mmo-elite\">"));
        });
    }

    [Test]
    public void Detail_ForOtherCompanyClan_RendersNoPanel()
    {
        var page = LocalClan.Page(Session(), null, Now, "eic-vanguard");
        Assert.That(page, Does.Not.Contain("id=\"clan-detail\""));
    }

    [Test]
    public void Detail_RetiredMembership_StillShowsTopLevelLeaveForm()
    {
        var page = LocalClan.Page(Session("eic"), "mmo-guard", Now, "eic-orbit");
        var section = page[page.IndexOf("<section id=\"clan-detail\"", StringComparison.Ordinal)..page.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("is not available for your company"));
            Assert.That(page, Does.Contain("action=internalClanLeave"));
            Assert.That(section, Does.Contain("Leave your current clan before joining this one."));
            Assert.That(section, Does.Contain("<dd id=\"clan-detail-members\">21</dd>"));
        });
    }

    [Test]
    public void Detail_DoesNotChangeTableMemberCounts()
    {
        var page = LocalClan.Page(Session(), "mmo-guard", Now, "mmo-orbit");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<td>43</td>"));
            Assert.That(page, Does.Contain("<td>17</td>"));
        });
    }
}
