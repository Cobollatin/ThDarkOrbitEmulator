using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSkillActionQueryTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalPilotSkillStore _skills = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _skills = new LocalPilotSkillStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("?action=internalPilotSkillAllocate", true)]
    [TestCase("?action=internalPilotSkillReset", true)]
    [TestCase("?ACTION=internalPilotSkillAllocate", false)]
    [TestCase("?Action=internalPilotSkillReset", false)]
    [TestCase("?action=internalPilotSkillAllocate&action=internalPilotSkillAllocate", false)]
    [TestCase("?action=internalPilotSkillAllocate&from=internalPilotSheet", false)]
    [TestCase("?action=internalPilotSkillAllocate&ACTION=internalPilotSkillAllocate", false)]
    [TestCase("", false)]
    public void ActionQuery_RequiresOneCanonicalActionKey(string query, bool expected) =>
        Assert.That(LocalPilotSkillAction.ValidActionQuery(
            new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query), Is.EqualTo(expected));

    [TestCase("ACTION", "allocate")]
    [TestCase("Action", "allocate")]
    [TestCase("ACTION", "reset")]
    [TestCase("aCTION", "reset")]
    public async Task NonCanonicalActionKey_WithValidForm_Returns400AndChangesNothing(string key, string kind)
    {
        var context = Build($"?{key}={ActionName(kind)}", ValidBody(kind));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("Invalid Pilot Sheet action."));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task NonCanonicalActionKey_Returns400BeforeSessionLookup()
    {
        var context = Build("?ACTION=internalPilotSkillAllocate", ValidBody("allocate"), "missing-session");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task NonCanonicalActionKey_WithGet_StillReturns405()
    {
        var context = Build("?ACTION=internalPilotSkillAllocate", "");
        context.Request.Method = "GET";
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
    }

    [TestCase("allocate")]
    [TestCase("reset")]
    public async Task BodyStreamIoFailure_Returns400AndChangesNothing(string kind)
    {
        var context = Build($"?action={ActionName(kind)}", ValidBody(kind));
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync();
    }

    [TestCase("allocate")]
    [TestCase("reset")]
    public async Task BodyStreamInvalidOperation_Returns400AndChangesNothing(string kind)
    {
        var context = Build($"?action={ActionName(kind)}", ValidBody(kind));
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task MissingSession_Returns401BeforeReadingTheForm()
    {
        var context = Build("?action=internalPilotSkillAllocate", ValidBody("allocate"), "missing-session");
        context.Request.Body = new FailingStream(new IOException("Client disconnected."));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await Body(context), Does.Contain("Sign in again"));
    }

    [Test]
    public async Task ErrorPage_ShowsStatusAndNoStore()
    {
        var context = Build("?ACTION=internalPilotSkillReset", ValidBody("reset"));
        await Handle(context);
        Assert.That(await Body(context), Does.Contain("Pilot Sheet request failed (400)"));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task CanonicalAllocate_StillApplies()
    {
        var context = Build("?action=internalPilotSkillAllocate", ValidBody("allocate"));
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalPilotSheet&trained=hull"));
        var state = await _skills.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Ranks["hull"], Is.EqualTo(1));
        Assert.That(state.Revision, Is.EqualTo(1));
    }

    private static string ActionName(string kind) =>
        kind == "allocate" ? "internalPilotSkillAllocate" : "internalPilotSkillReset";

    private string ValidBody(string kind)
    {
        var key = Uri.EscapeDataString(LocalPilotSkillAction.CreateFormKey(_session.Dosid, kind, kind == "allocate" ? "hull" : "", 0));
        return kind == "allocate" ? $"skill=hull&rev=0&key={key}" : $"rev=0&key={key}";
    }

    private async Task AssertUnchangedAsync()
    {
        var state = await _skills.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Spent, Is.Zero);
        Assert.That(state.Revision, Is.Zero);
    }

    private Task Handle(HttpContext context) =>
        LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);

    private DefaultHttpContext Build(string query, string body, string? dosid = null)
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

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }
}
