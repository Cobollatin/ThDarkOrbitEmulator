using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAssemblyHardeningTests
{
    private GameAuthSessionStore _sessions = null!;
    private GameAuthSession _session = null!;
    private int _crafts;

    [SetUp]
    public async Task SetUp()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
        _crafts = 0;
    }

    [TestCase("?action=internalAssembly")]
    [TestCase("?action=internalAssembly&from=menu&prc=100&lang=en")]
    public void PageQuery_AcceptsShellNavigationParameters(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("")]
    [TestCase("?from=menu")]
    [TestCase("?action=internalAssembly&action=internalAssembly")]
    [TestCase("?action=internalAssembly&recipe=lf3")]
    [TestCase("?action=internalAssembly&key=abc")]
    [TestCase("?action=internalAssembly&from=a&from=b")]
    [TestCase("?action=internalAssembly&lang=%0A")]
    [TestCase("?action=internalAssembly&prc=%00")]
    [TestCase("?ACTION=internalAssembly&Lang=de")]
    public void PageQuery_RejectsUnknownRepeatedOrControlValues(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void PageQuery_RejectsOverlongValues()
    {
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&from={new string('a', 64)}")), Is.True);
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query($"?action=internalAssembly&from={new string('a', 65)}")), Is.False);
    }

    [TestCase("?action=internalAssemblyCraft", true)]
    [TestCase("?action=internalassemblycraft", false)]
    [TestCase("?ACTION=internalAssemblyCraft", false)]
    [TestCase("?action=internalAssembly", false)]
    [TestCase("", false)]
    [TestCase("?action=internalAssemblyCraft&from=menu", false)]
    [TestCase("?action=internalAssemblyCraft&action=internalAssemblyCraft", false)]
    public void ActionQuery_RequiresOneCanonicalAction(string query, bool valid) =>
        Assert.That(LocalAssemblyAction.ValidActionQuery(Query(query)), Is.EqualTo(valid));

    [Test]
    public async Task CraftWithNonCanonicalAction_Returns400WithoutCrafting()
    {
        var context = Build(SignedBody(), query: "?action=INTERNALASSEMBLYCRAFT");
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Invalid Assembly action"));
            Assert.That(_crafts, Is.Zero);
        });
    }

    [Test]
    public async Task CraftTruncatedMultipartBody_Returns400WithoutCrafting()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"recipe\"\r\n\r\nlf3");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("could not be read"));
            Assert.That(html, Does.Contain("Return to Assembly"));
            Assert.That(_crafts, Is.Zero);
        });
    }

    [Test]
    public async Task CraftBodyStreamFailure_Returns400WithoutCrafting()
    {
        var context = Build(SignedBody());
        context.Request.Body = new FailingStream();
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Contain("could not be read"));
            Assert.That(_crafts, Is.Zero);
        });
    }

    [Test]
    public async Task CraftWithoutSession_Returns401BeforeReadingTheForm()
    {
        var context = Build(SignedBody(), dosid: "missing-session");
        context.Request.Body = new FailingStream();
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(html, Does.Contain("Sign in again"));
            Assert.That(_crafts, Is.Zero);
        });
    }

    [Test]
    public async Task CraftWithBadQueryAndNoSession_Returns400First()
    {
        var context = Build(SignedBody(), query: "?action=internalAssemblyCraft&recipe=lf3", dosid: "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task CanonicalCraft_StillRedirectsToAssembly()
    {
        var context = Build(SignedBody());
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAssembly&crafted=lf3"));
            Assert.That(_crafts, Is.EqualTo(1));
        });
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private string SignedBody() => $"recipe=lf3&key={LocalAssemblyAction.CreateFormKey(_session.Dosid, "lf3")}";

    private DefaultHttpContext Build(string body, string query = "?action=internalAssemblyCraft", string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private Task Handle(HttpContext context) =>
        LocalAssemblyAction.HandleAsync(context, _sessions, (_, _, _, _) =>
        {
            _crafts++;
            return Task.FromResult(EconomyCraftResult.Applied);
        }, TimeProvider.System, CancellationToken.None);

    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class FailingStream : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("Client disconnected.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("Client disconnected.");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Client disconnected.");
    }
}
