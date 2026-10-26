using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-191: Dock page query keys and the action value match exactly, like Map overview and Command Center.
public class LocalHangarQueryKeyTests
{
    [TestCase("ACTION=internalDock")]
    [TestCase("Action=internalDock")]
    [TestCase("action=INTERNALDOCK")]
    [TestCase("action=internaldock")]
    [TestCase("action=internalDock&From=internalStart")]
    [TestCase("action=internalDock&PRC=100")]
    [TestCase("action=internalDock&Lang=en")]
    [TestCase("action=internalDock&LANG=en")]
    [TestCase("action=internalDock&lang=en&FROM=internalStart")]
    [TestCase("action=internalDock&action=INTERNALDOCK")]
    public void NonCanonicalKeyOrAction_IsRejected(string query) =>
        Assert.That(LocalHangars.ValidPageQuery(Query(query)), Is.False);

    [TestCase("from")]
    [TestCase("prc")]
    [TestCase("lang")]
    public void ControlCharacterInShellValue_IsRejected(string key)
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalHangars.ValidPageQuery(Query($"action=internalDock&{key}=a%0Db")), Is.False);
            Assert.That(LocalHangars.ValidPageQuery(Query($"action=internalDock&{key}=a%00")), Is.False);
            Assert.That(LocalHangars.ValidPageQuery(Query($"action=internalDock&{key}=a%09b")), Is.False);
        });
    }

    [TestCase("action=internalDock")]
    [TestCase("action=internalDock&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalDock&ship=2&activated=1")]
    [TestCase("action=internalDock&lang=pt%20BR")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalHangars.ValidPageQuery(Query(query)), Is.True);

    [Test]
    public void MissingAction_IsRejected() =>
        // The endpoint always routes the Dock with an action; a bare query is not a Dock page.
        Assert.That(LocalHangars.ValidPageQuery(Query("ship=1")), Is.False);

    [Test]
    public void EveryLinkTheHangarRendersForItself_IsAccepted()
    {
        foreach (var slot in new[] { 1, 2 })
        {
            var location = LocalHangars.ActivatedLocation(slot);
            Assert.That(LocalHangars.ValidPageQuery(Query(location[(location.IndexOf('?') + 1)..])), Is.True);
            Assert.That(LocalHangars.ValidPageQuery(Query($"action=internalDock&ship={slot}")), Is.True);
        }
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
