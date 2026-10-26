using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPilotSheetHardeningTests
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

    [TestCase("internalPilotSkillAllocate", "skill")]
    [TestCase("internalPilotSkillReset", "rev")]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing(string action, string field)
    {
        var context = Build(action, $"--b\r\nContent-Disposition: form-data; name=\"{field}\"\r\n\r\nhu");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That(await Body(context), Does.Contain("could not be read"));
        var state = await _skills.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Spent, Is.Zero);
        Assert.That(state.Revision, Is.Zero);
    }

    [Test]
    public async Task ValidMultipartBody_StillAllocates()
    {
        var key = LocalPilotSkillAction.CreateFormKey(_session.Dosid, "allocate", "hull", 0);
        var body = string.Concat(
            Part("skill", "hull"), Part("rev", "0"), Part("key", key), "--b--\r\n");
        var context = Build("internalPilotSkillAllocate", body);
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        Assert.That((await _skills.GetAsync(_session.AccountId, CancellationToken.None)).Ranks["hull"], Is.EqualTo(1));
    }

    [TestCase("internalPilotSkillAllocate", "GET")]
    [TestCase("internalPilotSkillReset", "PUT")]
    public async Task WrongMethod_Returns405WithAllowHeader_BeforeSessionLookup(string action, string method)
    {
        var context = Build(action, "", "missing-session");
        context.Request.Method = method;
        await LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [TestCase("?action=InternalPilotSkillAllocate")]
    [TestCase("?action=internalPilotSkillAllocate&action=internalPilotSkillAllocate")]
    public async Task ActionQuery_WrongCaseOrRepeated_Returns404(string query)
    {
        var context = Build("internalPilotSkillAllocate", "skill=hull");
        context.Request.QueryString = new QueryString(query);
        await LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task ErrorPages_DoNotEchoSkillInput()
    {
        var context = Build("internalPilotSkillAllocate", "skill=%3Cscript%3Ealert(1)%3C%2Fscript%3E&rev=0&key=x");
        await LocalPilotSkillAction.HandleAsync(context, _sessions, _skills, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var body = await Body(context);
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(body, Does.Not.Contain("alert"));
    }

    [TestCase("?action=internalPilotSheet", true)]
    [TestCase("?action=internalPilotSheet&from=internalStart", true)]
    [TestCase("?action=internalPilotSheet&from=internalStart&prc=100&lang=es", true)]
    [TestCase("?action=internalPilotSheet&LANG=es", false)]
    [TestCase("?action=internalPilotSheet&skill=hull", false)]
    [TestCase("?action=internalPilotSheet&from=a&from=b", false)]
    [TestCase("?action=internalPilotSheet&action=internalPilotSheet", false)]
    [TestCase("?from=internalStart", false)]
    [TestCase("?action=internalPilotSheet&lang=%0A", false)]
    public void PageQuery_AcceptsOnlyNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalPilotSkillAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValue()
    {
        Assert.That(LocalPilotSkillAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalPilotSheet&from=" + new string('a', 64)))), Is.True);
        Assert.That(LocalPilotSkillAction.ValidPageQuery(new QueryCollection(
            QueryHelpers.ParseQuery("?action=internalPilotSheet&from=" + new string('a', 65)))), Is.False);
    }

    private static string Part(string name, string value) =>
        $"--b\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n";

    private DefaultHttpContext Build(string action, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
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
