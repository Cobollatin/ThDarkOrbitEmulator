using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPaymentProxyResponseTests
{
    [Test]
    public void RecognizesOnlyCapturedGameHostRootAction()
    {
        var context = NewContext();
        context.Request.Host = new HostString("localhost", 60952);
        context.Request.Headers["X-Original-Host"] = "gbl6.darkorbit.com";
        context.Request.QueryString = new QueryString("?action=INTERNALPAYMENTPROXY&req=opaque");
        Assert.That(LocalPaymentProxyResponse.IsRequest(context.Request), Is.True);

        context.Request.QueryString = new QueryString("?action=internalPayment");
        Assert.That(LocalPaymentProxyResponse.IsRequest(context.Request), Is.False);
        context.Request.QueryString = new QueryString("?action=internalPaymentProxy");
        context.Request.Headers["X-Original-Host"] = "www.darkorbit.com";
        Assert.That(LocalPaymentProxyResponse.IsRequest(context.Request), Is.False);
        context.Request.Headers["X-Original-Host"] = "gbl6.darkorbit.com.evil.test";
        Assert.That(LocalPaymentProxyResponse.IsRequest(context.Request), Is.False);
        context.Request.Host = new HostString("gbl3.darkorbit.com");
        Assert.That(LocalPaymentProxyResponse.IsRequest(context.Request), Is.True);
    }

    [Test]
    public async Task MissingSession_IsUnauthorized()
    {
        var context = NewContext();
        await LocalPaymentProxyResponse.WriteAsync(context.Response, false, false, null, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(context.Response.Body.Length, Is.Zero);
    }

    [Test]
    public async Task SessionWithoutCompany_RedirectsToChoice()
    {
        var context = NewContext();
        await LocalPaymentProxyResponse.WriteAsync(context.Response, true, false, null, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(context.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
    }

    [Test]
    public async Task SelectedCompany_GetsLocalCreditPage()
    {
        var context = NewContext();
        await LocalPaymentProxyResponse.WriteAsync(context.Response, true, true,
            new LocalEquipmentState([], []), CancellationToken.None);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var html = await reader.ReadToEndAsync();
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(html, Does.Contain("id=\"paymentProxy\""));
        Assert.That(html, Does.Contain("action=internalWebGL&amp;from=internalStart"));
        Assert.That(html, Does.Contain("action=internalPaymentClaim"));
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
