using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexEs;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLauncherDownloadValidationTests
{
    // Parsed through DefaultHttpContext so keys use the real case-insensitive lookup.
    private static IQueryCollection Query(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return context.Request.Query;
    }

    [TestCase("?action=externalDownload")]
    [TestCase("?action=externalDownload&from=internalStart")]
    [TestCase("?action=externalDownload&prc=100&lang=es")]
    [TestCase("?lang=en&action=externalDownload")]
    public void CanonicalLinksReturnThePage(string queryString)
    {
        var query = Query(queryString);
        Assert.That(LocalLauncherDownload.Targets(query), Is.True);
        var result = LocalLauncherDownload.Resolve(query);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Is.EqualTo(LocalLauncherDownload.Page));
    }

    [TestCase("")]
    [TestCase("?action=internalStart")]
    [TestCase("?action=externalDownloadX")]
    [TestCase("?download=externalDownload")]
    public void OtherIndexEsRequestsStayOnTheSignInCapture(string queryString) =>
        Assert.That(LocalLauncherDownload.Targets(Query(queryString)), Is.False);

    [TestCase("?ACTION=externalDownload")]
    [TestCase("?Action=externalDownload")]
    [TestCase("?action=EXTERNALDOWNLOAD")]
    [TestCase("?action=externaldownload")]
    [TestCase("?action=externalDownload&action=externalDownload")]
    [TestCase("?action=internalStart&action=externalDownload")]
    public void TamperedActionIsRejectedInsteadOfFallingThrough(string queryString)
    {
        var query = Query(queryString);
        Assert.That(LocalLauncherDownload.Targets(query), Is.True);
        var result = LocalLauncherDownload.Resolve(query);
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain("id=\"localLauncherDownloadError\""));
        Assert.That(result.Html, Does.Not.Contain("id=\"localLauncherDownload\""));
    }

    [TestCase("?action=externalDownload&installer=1", "Unknown parameter")]
    [TestCase("?action=externalDownload&FROM=internalStart", "Unknown parameter")]
    [TestCase("?action=externalDownload&from=a&from=b", "appear once")]
    [TestCase("?action=externalDownload&lang=%0d%0aX", "printable")]
    [TestCase("?action=externalDownload&lang=%00", "printable")]
    public void UnknownRepeatedOrControlParametersAreRejected(string queryString, string message)
    {
        var result = LocalLauncherDownload.Resolve(Query(queryString));
        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(result.Html, Does.Contain(message));
    }

    [Test]
    public void ValueLengthLimitIsInclusive()
    {
        var atLimit = new string('a', LocalLauncherDownload.MaxValueLength);
        Assert.That(LocalLauncherDownload.Resolve(Query($"?action=externalDownload&from={atLimit}")).Status,
            Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(LocalLauncherDownload.Resolve(Query($"?action=externalDownload&from={atLimit}a")).Status,
            Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public void ErrorPageEncodesTheMessageAndLinksBackToTheCanonicalPage()
    {
        var result = LocalLauncherDownload.Error(StatusCodes.Status400BadRequest, "<script>x</script>");
        Assert.That(result.Html, Does.Not.Contain("<script>"));
        Assert.That(result.Html, Does.Contain("&lt;script&gt;"));
        Assert.That(result.Html, Does.Contain("href=\"/index.es?action=externalDownload\""));
    }

    [TestCase("?action=externalDownload", 200)]
    [TestCase("?ACTION=externalDownload", 400)]
    public async Task WriteSetsNoStoreHtmlWithoutMintingASessionCookie(string queryString, int status)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        var body = new MemoryStream();
        context.Response.Body = body;

        await LocalLauncherDownload.WriteAsync(context.Response,
            LocalLauncherDownload.Resolve(context.Request.Query), CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
        Assert.That(body.Length, Is.GreaterThan(0));
    }
}
