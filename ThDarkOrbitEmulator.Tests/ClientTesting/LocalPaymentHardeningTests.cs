using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPaymentHardeningTests
{
    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("HEAD")]
    public async Task ClaimNonPost_Returns405_WithAllowHeader_AndGrantsNothing(string method)
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, "?action=internalPaymentClaim");
        context.Request.Method = method;
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await BodyAsync(context), Does.Contain("Payment request failed (405)"));
        await AssertUnchangedAsync(store, session);
    }

    [TestCase("?action=internalPaymentClaim")]
    [TestCase("")]
    public async Task ClaimPost_AcceptsExactActionOrRoutedRequest(string query)
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, query);
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(11000));
    }

    [TestCase("?action=INTERNALPAYMENTCLAIM")]
    [TestCase("?action=internalpaymentclaim")]
    [TestCase("?action=internalPaymentClaim&action=internalPaymentClaim")]
    [TestCase("?action=internalPaymentClaim&from=internalPayment")]
    [TestCase("?action=internalPaymentClaim&package=armada")]
    [TestCase("?action=internalVoucherRedeem")]
    [TestCase("?package=starter")]
    public async Task ClaimPost_RejectsWrongCaseRepeatedOrExtraQuery(string query)
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, query);
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await BodyAsync(context), Does.Contain("Invalid payment action."));
        await AssertUnchangedAsync(store, session);
    }

    [Test]
    public async Task ClaimPost_TruncatedMultipartBody_Returns400_InsteadOfThrowing()
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, "?action=internalPaymentClaim",
            "--b\r\nContent-Disposition: form-data; name=\"package\"\r\n\r\nstar");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await BodyAsync(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync(store, session);
    }

    [TestCase("package=%00starter")]
    [TestCase("package=starter%0A")]
    [TestCase("package=%20starter")]
    [TestCase("package=st%C3%A4rter")]
    [TestCase("package=%3Cscript%3E")]
    public async Task ClaimPost_RejectsControlNonAsciiOrMarkupPackages_WithoutEcho(string body)
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, "?action=internalPaymentClaim", body);
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var html = await BodyAsync(context);
        Assert.That(html, Does.Not.Contain("<script>"));
        Assert.That(html, Does.Not.Contain("starter"));
        await AssertUnchangedAsync(store, session);
    }

    [Test]
    public async Task ClaimPost_NonFormContentType_Returns400()
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, "?action=internalPaymentClaim", "{\"package\":\"starter\"}");
        context.Request.ContentType = "application/json";
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync(store, session);
    }

    [Test]
    public async Task ClaimPost_MethodCheckRunsBeforeSessionLookup()
    {
        var (sessions, store, _) = await CompanySessionAsync();
        var context = Request(null, "?action=internalPaymentClaim");
        context.Request.Method = "GET";
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
    }

    [Test]
    public async Task ClaimPost_EveryCatalogPackage_GrantsOnce_AndTotalsMatchSummary()
    {
        var (sessions, store, session) = await CompanySessionAsync();
        foreach (var package in LocalPayment.Catalog)
        {
            var context = Request(session.Dosid, "?action=internalPaymentClaim", $"package={package.Id}");
            await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
            Assert.That(context.Response.StatusCode, Is.EqualTo(303), package.Id);
        }
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        var total = LocalPayment.Catalog.Sum(package => package.Credits);
        Assert.That(state.Credits, Is.EqualTo(10000 + total));
        Assert.That(LocalPayment.Page(state), Does.Contain($"Claimed 5 of 5 packages ({total:N0} credits)"));
        Assert.That(LocalPayment.Page(state), Does.Not.Contain("method=\"post\""));
    }

    [TestCase("action=internalPayment", true)]
    [TestCase("action=internalPayment&from=internalStart", true)]
    [TestCase("action=internalPayment&from=internalStart&prc=1&lang=es", true)]
    // Menu-199: keys are exact lowercase.
    [TestCase("ACTION=internalPayment&LANG=es", false)]
    [TestCase("", false)]
    [TestCase("from=internalStart", false)]
    [TestCase("action=internalPayment&action=internalPayment", false)]
    [TestCase("action=internalPayment&lang=es&lang=en", false)]
    [TestCase("action=internalPayment&package=armada", false)]
    [TestCase("action=internalPayment&from=%0A", false)]
    public void PageQuery_AcceptsOnlyShellNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
        Assert.That(LocalPaymentAction.ValidPageQuery(parsed), Is.EqualTo(expected));
    }

    [Test]
    public void PageQuery_RejectsOverlongValues()
    {
        var ok = new QueryCollection(new Dictionary<string, StringValues>
            { ["action"] = "internalPayment", ["from"] = new string('a', 64) });
        var tooLong = new QueryCollection(new Dictionary<string, StringValues>
            { ["action"] = "internalPayment", ["from"] = new string('a', 65) });
        Assert.That(LocalPaymentAction.ValidPageQuery(ok), Is.True);
        Assert.That(LocalPaymentAction.ValidPageQuery(tooLong), Is.False);
    }

    private static async Task AssertUnchangedAsync(LocalHangarEquipmentStore store, GameAuthSession session)
    {
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
        Assert.That(state.ClaimedPaymentPackages, Is.Empty);
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Store, GameAuthSession Session)> CompanySessionAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session);
    }

    private static async Task<string> BodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static DefaultHttpContext Request(string? dosid, string query, string body = "package=starter")
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query.Length == 0 ? null : query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
