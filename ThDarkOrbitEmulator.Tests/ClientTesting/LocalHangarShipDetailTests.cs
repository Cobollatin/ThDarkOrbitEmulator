using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarShipDetailTests
{
    private static readonly GameAuthSession Session =
        new() { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "dock-dosid", Company = "mmo" };

    [TestCase("&ship=1", true)]
    [TestCase("&ship=2", true)]
    [TestCase("&from=internalStart&ship=2&lang=es", true)]
    [TestCase("&ship=1&activated=1", true)]
    [TestCase("&ship=", false)]
    [TestCase("&ship=0", false)]
    [TestCase("&ship=3", false)]
    [TestCase("&ship=01", false)]
    [TestCase("&ship=+1", false)]
    [TestCase("&ship=%201", false)]
    [TestCase("&ship=-1", false)]
    [TestCase("&ship=%D9%A1", false)]
    [TestCase("&ship=pusat", false)]
    [TestCase("&ship=1&ship=1", false)]
    [TestCase("&ship=1&ship=2", false)]
    [TestCase("&SHIP=1", false)]
    [TestCase("&Ship=1", false)]
    [TestCase("&ship", false)]
    [TestCase("&shipSlot=1", false)]
    public void PageQuery_AcceptsOnlyOneCanonicalLowercaseShipSlot(string extra, bool valid) =>
        Assert.That(LocalHangars.ValidPageQuery(Query("?action=internalDock" + extra)), Is.EqualTo(valid));

    [TestCase("?action=internalDock", null)]
    [TestCase("?action=internalDock&ship=1", 1)]
    [TestCase("?action=internalDock&ship=2&activated=1", 2)]
    public void ShipSlot_ReadsTheValidatedSlot(string query, int? expected) =>
        Assert.That(LocalHangars.ShipSlot(Query(query)), Is.EqualTo(expected));

    [Test]
    public void ListPage_LinksEachCardToItsDetailView()
    {
        var page = LocalHangars.Page(Session, 1);
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalDock&amp;ship=1\">Details</a>"));
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalDock&amp;ship=2\">Details</a>"));
        Assert.That(page, Does.Not.Contain("dock-ship"));
        Assert.That(Regex.Matches(page, "<article").Count, Is.EqualTo(2));
    }

    [TestCase(1, "Pusat Plus", "128,000", "370", "1,500", "5", "12", "2")]
    [TestCase(2, "Phoenix", "4,000", "320", "100", "1", "1", "1")]
    public void Detail_ShowsShipSpecs(int slot, string name, string hp, string speed, string cargo,
        string lasers, string generators, string extras)
    {
        var page = LocalHangars.Page(Session, 1, ship: slot);
        Assert.That(page, Does.Contain($"<section id=\"dock-ship\" data-slot=\"{slot}\""));
        Assert.That(page, Does.Contain($"<h2>{name} (slot {slot})</h2>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"hit-points\"><th>Hit points</th><td>{hp}</td></tr>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"speed\"><th>Speed</th><td>{speed}</td></tr>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"cargo\"><th>Cargo</th><td>{cargo}</td></tr>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"lasers\"><th>Laser slots</th><td>{lasers}</td></tr>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"generators\"><th>Generator slots</th><td>{generators}</td></tr>"));
        Assert.That(page, Does.Contain($"<tr data-spec=\"extras\"><th>Extra slots</th><td>{extras}</td></tr>"));
        // The detail view replaces the card list.
        Assert.That(page, Does.Not.Contain("<article"));
    }

    [Test]
    public void Detail_ReusesTheLoginHangarData()
    {
        var hangars = LocalHangars.For(Session, 1);
        foreach (var hangar in hangars)
        {
            var page = LocalHangars.Page(Session, 1, ship: hangar.SlotNumber);
            Assert.That(page, Does.Contain($"<td>{hangar.HangarId}</td>"));
            Assert.That(page, Does.Contain($"<td>{hangar.ShipId} (model {hangar.ModelId})</td>"));
            Assert.That(page, Does.Contain($"src=\"{HtmlEncoder.Default.Encode(hangar.TooltipIcon)}\""));
        }
    }

    [TestCase("mmo", "1-1 (map 1)")]
    [TestCase("eic", "2-1 (map 5)")]
    [TestCase("vru", "3-1 (map 9)")]
    public void Detail_ShowsTheCompanyHomeMap(string company, string map)
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "d", Company = company };
        Assert.That(LocalHangars.Page(session, 1, ship: 1),
            Does.Contain($"<tr data-spec=\"map\"><th>Home map</th><td>{map}</td></tr>"));
    }

    [Test]
    public void Detail_ActiveShipHasNoForm_InactiveShipHasTheSignedActivateForm()
    {
        var active = LocalHangars.Page(Session, 1, ship: 1);
        Assert.That(active, Does.Contain("data-slot=\"1\" data-active=\"true\""));
        Assert.That(active, Does.Contain("<p data-status=\"active\"><strong>Active ship</strong></p>"));
        Assert.That(active, Does.Not.Contain("<form"));

        var issued = DateTimeOffset.UtcNow;
        var inactive = LocalHangars.Page(Session, 1, issued, ship: 2);
        Assert.That(inactive, Does.Contain("data-slot=\"2\" data-active=\"false\""));
        Assert.That(inactive, Does.Contain("<p data-status=\"inactive\">Slot 1 is active. Activate this ship to fly it.</p>"));
        Assert.That(Regex.Matches(inactive, "<form ").Count, Is.EqualTo(1));
        Assert.That(inactive, Does.Contain("name=\"slot\" value=\"2\""));
        Assert.That(inactive, Does.Contain("name=\"current\" value=\"1\""));
        var key = Regex.Match(inactive, "name=\"key\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.That(LocalHangars.KeyIssued(Session.Dosid, 2, 1, key), Is.Not.Null);
    }

    [TestCase(1, 2)]
    [TestCase(2, 1)]
    public void Detail_LinksToEquipmentTheOtherSlotAndTheList(int slot, int other)
    {
        var page = LocalHangars.Page(Session, 1, ship: slot);
        Assert.That(page, Does.Contain($"action=internalDockEquipment&amp;slot={slot}&amp;configuration=1"));
        Assert.That(page, Does.Contain($"href=\"/indexInternal.es?action=internalDock&amp;ship={other}\">Slot {other}</a>"));
        Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalDock\">All ships</a>"));
    }

    [Test]
    public void Detail_KeepsTheActivatedNoticeAboveTheDetail()
    {
        var page = LocalHangars.Page(Session, 2, activated: 2, ship: 2);
        Assert.That(page, Does.Contain("Phoenix is now the active ship (slot 2)."));
        Assert.That(page.IndexOf("dock-notice", StringComparison.Ordinal),
            Is.LessThan(page.IndexOf("dock-ship", StringComparison.Ordinal)));
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(-1)]
    public void Detail_InvalidSlotFallsBackToTheList(int ship)
    {
        var page = LocalHangars.Page(Session, 1, ship: ship);
        Assert.That(page, Does.Not.Contain("dock-ship"));
        Assert.That(Regex.Matches(page, "<article").Count, Is.EqualTo(2));
    }

    [Test]
    public void Detail_CorruptActiveSlotStillMarksExactlyOneActiveShip()
    {
        Assert.That(LocalHangars.Page(Session, 9, ship: 1), Does.Contain("data-active=\"true\""));
        Assert.That(LocalHangars.Page(Session, 9, ship: 2), Does.Contain("data-active=\"false\""));
    }

    [Test]
    public void Detail_UnknownCompanyFallsBackToTheHangarMapId()
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "d", Company = "zzz" };
        Assert.That(LocalHangars.Page(session, 1, ship: 1), Does.Contain("<th>Home map</th><td>map 1</td>"));
    }

    private static IQueryCollection Query(string queryString) =>
        new QueryCollection(QueryHelpers.ParseQuery(queryString));
}
