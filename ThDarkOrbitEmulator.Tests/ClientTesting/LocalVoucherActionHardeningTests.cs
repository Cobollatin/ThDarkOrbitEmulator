using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalVoucherActionHardeningTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"code\"\r\n\r\nTRAIN");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ValidMultipartBody_StillRedeems()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"code\"\r\n\r\nTRAINING-500\r\n--b--\r\n");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10500));
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task WrongMethod_Returns405WithAllowHeader_BeforeSessionLookup(string method)
    {
        var context = Build("code=TRAINING-500", "missing-session");
        context.Request.Method = method;
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        await AssertUnchangedAsync();
    }

    [TestCase("?action=INTERNALVOUCHERREDEEM")]
    [TestCase("?action=internalvoucherredeem")]
    [TestCase("?action=internalVoucherRedeem&action=internalVoucherRedeem")]
    [TestCase("?action=internalVoucherRedeem&from=internalVouchers")]
    [TestCase("?action=internalVoucherRedeem&code=TRAINING-500")]
    [TestCase("?action=internalPaymentClaim")]
    [TestCase("?code=TRAINING-500")]
    public async Task RedeemQuery_WrongCaseRepeatedOrExtra_Returns400(string query)
    {
        var context = Build("code=TRAINING-500");
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid voucher action."));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task MissingSession_IsCheckedAfterMethodAndQuery()
    {
        var context = Build("code=TRAINING-500", "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ErrorPages_DoNotEchoInput()
    {
        var context = Build("code=%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var body = await Body(context);
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(body, Does.Not.Contain("alert"));
        await AssertUnchangedAsync();
    }

    [TestCase("code=TRAINING-500%00")]
    [TestCase("code=TRAINING%0A500")]
    [TestCase("code=TRAINING_500")]
    [TestCase("code=")]
    [TestCase("code=%20%20")]
    public async Task MalformedCode_Returns400_WithoutCredit(string body)
    {
        var context = Build(body);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task OverlongCode_Returns400()
    {
        var context = Build("code=" + new string('A', 33));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task LowercaseAndPaddedCode_RedeemsOnce()
    {
        var first = Build("code=%20training-500%20");
        await Handle(first);
        Assert.That(first.Response.StatusCode, Is.EqualTo(303));
        var second = Build("code=TRAINING-500");
        await Handle(second);
        Assert.That(second.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10500));
    }

    [TestCase("?action=internalVouchers", true)]
    [TestCase("?action=internalVouchers&from=internalStart&prc=100&lang=es", true)]
    [TestCase("?action=internalVouchers&LANG=es", false)]
    [TestCase("?action=internalVouchers&code=TRAINING-500", false)]
    [TestCase("?action=internalVouchers&from=a&from=b", false)]
    [TestCase("?action=internalVouchers&action=internalVouchers", false)]
    [TestCase("?from=internalStart", false)]
    [TestCase("?action=internalVouchers&lang=%0A", false)]
    public void PageQuery_AcceptsOnlyNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalVoucherAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValue()
    {
        Assert.That(LocalVoucherAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalVouchers&prc=" + new string('a', 64)))), Is.True);
        Assert.That(LocalVoucherAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalVouchers&prc=" + new string('a', 65)))), Is.False);
    }

    private Task Handle(DefaultHttpContext context) =>
        LocalVoucherAction.HandleAsync(context, _sessions, _store, CancellationToken.None);

    private async Task AssertUnchangedAsync()
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
        Assert.That(state.RedeemedVoucherCodes, Is.Empty);
    }

    private DefaultHttpContext Build(string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalVoucherRedeem");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
