using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAccountSettingsActionHardeningTests
{
    private const string ValidBody = "language=de&sound=false&notifications=false";
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalAccountPreferencesStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalAccountPreferencesStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\nde");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ValidMultipartBody_StillSaves()
    {
        var body = string.Concat(new[] { ("language", "de"), ("sound", "false"), ("notifications", "false") }
            .Select(field => $"--b\r\nContent-Disposition: form-data; name=\"{field.Item1}\"\r\n\r\n{field.Item2}\r\n")) + "--b--\r\n";
        var context = Build(body);
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That(await _store.GetAsync(_session.AccountId, CancellationToken.None),
            Is.EqualTo(new LocalAccountPreferences("de", false, false)));
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task WrongMethod_Returns405WithAllowHeader_BeforeSessionLookup(string method)
    {
        var context = Build(ValidBody, "missing-session");
        context.Request.Method = method;
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        await AssertUnchangedAsync();
    }

    [TestCase("?action=INTERNALACCOUNTSAVE")]
    [TestCase("?action=internalaccountsave")]
    [TestCase("?action=internalAccountSave&action=internalAccountSave")]
    [TestCase("?action=internalAccountSave&from=internalAccount")]
    [TestCase("?action=internalAccountSave&language=de")]
    [TestCase("?action=internalVoucherRedeem")]
    [TestCase("?language=de")]
    public async Task SaveQuery_WrongCaseRepeatedOrExtra_Returns400(string query)
    {
        var context = Build(ValidBody);
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Account center action."));
        await AssertUnchangedAsync();
    }

    [TestCase("?ACTION=internalAccountSave")]
    [TestCase("?Action=internalAccountSave")]
    [TestCase("?action=internalAccountSave&ACTION=internalAccountSave")]
    public async Task SaveQuery_NonCanonicalActionKey_Returns400BeforeSessionLookup(string query)
    {
        var context = Build(ValidBody, "missing-session");
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Account center action."));
        await AssertUnchangedAsync();
    }

    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(IOException))]
    [TestCase(typeof(InvalidDataException))]
    public async Task UnreadableForm_Returns400WithoutEchoAndSavesNothing(Type failure)
    {
        var context = Build("");
        context.Request.ContentLength = null;
        context.Request.Body = new FailingStream((Exception)Activator.CreateInstance(failure, "Body unavailable.")!);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var html = await Body(context);
        Assert.That(html, Does.Contain("could not be read").And.Not.Contain("Body unavailable"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task MissingSession_Returns401BeforeReadingForm()
    {
        var context = Build("", "missing-session");
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task EmptyQuery_StillSaves()
    {
        var context = Build(ValidBody);
        context.Request.QueryString = QueryString.Empty;
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task MissingSession_IsCheckedAfterMethodAndQuery()
    {
        var context = Build(ValidBody, "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(await Body(context), Does.Contain("session is missing or expired"));
    }

    [TestCase("language=<script>&sound=false&notifications=false")]
    [TestCase("language=en&sound=<b>yes</b>&notifications=false")]
    [TestCase("language=en&sound=false&notifications=false&rev=<i>1</i>")]
    public async Task RejectedValues_AreNotEchoedInErrorPage(string body)
    {
        var context = Build(body);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var html = await Body(context);
        Assert.That(html, Does.Not.Contain("<script>").And.Not.Contain("<b>").And.Not.Contain("<i>"));
        await AssertUnchangedAsync();
    }

    [TestCase("action=internalAccount", true)]
    [TestCase("action=internalAccount&from=internalStart&prc=1&lang=en", true)]
    [TestCase("action=internalAccount&LANG=en", false)]
    [TestCase("from=internalStart", false)]
    [TestCase("action=internalAccount&action=internalAccount", false)]
    [TestCase("action=internalAccount&lang=en&lang=de", false)]
    [TestCase("action=internalAccount&account=2", false)]
    [TestCase("action=internalAccount&language=de", false)]
    [TestCase("action=internalAccount&lang=e%0An", false)]
    public void PageQuery_AllowsOnlyShellNavigationParameters(string query, bool expected) =>
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsValueLength()
    {
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query("action=internalAccount&from=" + new string('a', 64))), Is.True);
        Assert.That(LocalAccountSettingsAction.ValidPageQuery(Query("action=internalAccount&from=" + new string('a', 65))), Is.False);
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private DefaultHttpContext Build(string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalAccountSave");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private Task Handle(HttpContext context) =>
        LocalAccountSettingsAction.HandleAsync(context, _sessions, _store, CancellationToken.None);

    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private async Task AssertUnchangedAsync() =>
        Assert.That(await _store.GetAsync(_session.AccountId, CancellationToken.None),
            Is.EqualTo(LocalAccountPreferencesStore.Default));

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }
}
