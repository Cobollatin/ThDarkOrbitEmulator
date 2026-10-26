using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-199: Payment page query keys and the action value match exactly, like Dock, Map overview and Events.
public class LocalPaymentQueryKeyTests
{
    [TestCase("ACTION=internalPayment")]
    [TestCase("Action=internalPayment")]
    [TestCase("action=INTERNALPAYMENT")]
    [TestCase("action=internalpayment")]
    [TestCase("action=InternalPayment")]
    [TestCase("action=internalPayment&From=internalStart")]
    [TestCase("action=internalPayment&PRC=100")]
    [TestCase("action=internalPayment&Lang=en")]
    [TestCase("action=internalPayment&LANG=es")]
    [TestCase("action=internalPayment&lang=en&FROM=internalStart")]
    [TestCase("action=internalPayment&action=INTERNALPAYMENT")]
    [TestCase("action=internalPayment&Detail=starter")]
    [TestCase("action=internalPayment&CLAIMED=starter")]
    [TestCase("action=internalPayment&Filter=claimed")]
    [TestCase("action=internalPayment&Q=fleet")]
    public void NonCanonicalKeyOrAction_IsRejected(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Query(query)), Is.False);

    [TestCase("from")]
    [TestCase("prc")]
    [TestCase("lang")]
    public void ControlCharacterInShellValue_IsRejected(string key)
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalPaymentAction.ValidPageQuery(Query($"action=internalPayment&{key}=a%0Db")), Is.False);
            Assert.That(LocalPaymentAction.ValidPageQuery(Query($"action=internalPayment&{key}=a%00")), Is.False);
            Assert.That(LocalPaymentAction.ValidPageQuery(Query($"action=internalPayment&{key}=a%09b")), Is.False);
        });
    }

    [TestCase("action=internalPayment")]
    [TestCase("action=internalPayment&from=internalStart&prc=100&lang=en")]
    [TestCase("action=internalPayment&detail=starter&filter=claimed")]
    [TestCase("action=internalPayment&claimed=starter")]
    [TestCase("action=internalPayment&q=fleet&lang=pt%20BR")]
    public void CanonicalQuery_IsAccepted(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("detail=starter")]
    [TestCase("lang=en")]
    public void MissingAction_IsRejected(string query) =>
        Assert.That(LocalPaymentAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void EveryClaimedLocation_IsAccepted()
    {
        foreach (var package in LocalPayment.Catalog)
        {
            var location = LocalPaymentAction.ClaimedLocation(package.Id);
            Assert.That(LocalPaymentAction.ValidPageQuery(Query(location[(location.IndexOf('?') + 1)..])), Is.True, package.Id);
        }
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString("?" + query) } }.Request.Query;
}
