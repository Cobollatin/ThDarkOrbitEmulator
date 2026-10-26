using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCompanyPreviewTests
{
    [Test]
    public async Task DefaultChoicePage_HasDetailsLinks_AndNoPreviewPanel()
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request("?action=internalCompanyChoose");
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        var body = ReadBody(context);
        foreach (var company in CompanyCatalog.Companies)
            Assert.That(body, Does.Contain($"id=\"preview_{company.Code}\" href=\"/indexInternal.es?action=internalCompanyChoose&amp;preview={company.Code}\""));
        Assert.That(body, Does.Not.Contain("id=\"companyPreview\""));
        Assert.That(body, Does.Not.Contain("aria-current"));
        Assert.That(LocalCompanySelection.ChoicePage, Is.EqualTo(LocalCompanySelection.ChoicePageFor(null)));
    }

    [TestCase("mmo", 1, "1", "none")]
    [TestCase("eic", 2, "5", "none")]
    [TestCase("vru", 3, "9", "none")]
    [TestCase("tst", 4, "13", "none")]
    public async Task Preview_ShowsCompanyFacts_AndMarksTheCard(string code, int faction, string map, string shared)
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request($"?action=internalCompanyChoose&preview={code}&from=internalStart&lang=es");
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var body = ReadBody(context);
        var server = LocalMapsXml.Maps.Single(entry => entry.Id == map).GameServerIp;
        Assert.That(body, Does.Contain($"<section id=\"companyPreview\" data-company=\"{code}\">"));
        Assert.That(body, Does.Contain($"<dt>Faction</dt><dd>{faction}</dd><dt>Starter map</dt><dd>{map}</dd>"));
        Assert.That(body, Does.Contain($"<dt>Game server</dt><dd><code>{server}</code></dd>"));
        Assert.That(body, Does.Contain($"<dt>Shares starter map with</dt><dd>{shared}</dd>"));
        Assert.That(body, Does.Contain($"class=\"company selected\" data-company=\"{code}\" aria-current=\"true\""));
        Assert.That(body.Split("aria-current").Length - 1, Is.EqualTo(1));
        Assert.That(body, Does.Contain($"value=\"{code}\"><button class=\"confirm\" id=\"previewConfirm\""));
        Assert.That(body, Does.Contain("id=\"previewClose\""));
        Assert.That(body, Does.Contain("Previewing does not save anything."));
    }

    [Test]
    public async Task Preview_DoesNotPersistAnyChoice()
    {
        var (sessions, session, identity) = await CreateAsync();
        foreach (var company in CompanyCatalog.Companies)
        {
            var context = Request($"?action=internalCompanyChoose&preview={company.Code}");
            await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        }

        Assert.That(identity.SetCount, Is.Zero);
        Assert.That((await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!.Company, Is.Null);
    }

    [TestCase("?action=internalCompanyChoose&preview=MMO")]
    [TestCase("?action=internalCompanyChoose&preview=xyz")]
    [TestCase("?action=internalCompanyChoose&preview=%3Cscript%3E")]
    [TestCase("?action=internalCompanyChoose&preview=-1")]
    public async Task UnknownPreview_Returns404_WithoutEchoingTheValue(string query)
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request(query);
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var body = ReadBody(context);
        Assert.That(body, Does.Contain("Company selection failed (404)"));
        Assert.That(body, Does.Contain("internalCompanyChoose"));
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(body, Does.Not.Contain("xyz"));
    }

    [TestCase("?action=internalCompanyChoose&preview=")]
    [TestCase("?action=internalCompanyChoose&preview=mmo%0A")]
    [TestCase("?action=internalCompanyChoose&preview=mmo&preview=eic")]
    [TestCase("?action=internalCompanyChoose&Preview=mmo")]
    public async Task MalformedPreview_Returns400(string query)
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request(query);
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(ReadBody(context), Does.Contain("Company selection failed (400)"));
    }

    [Test]
    public async Task Preview_IsRejectedOnConfirm()
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request("?action=internalCompanyConfirm&company=mmo&preview=mmo");
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, true, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(identity.SetCount, Is.Zero);
    }

    [Test]
    public async Task Preview_WithoutSession_Returns401()
    {
        var (sessions, _, identity) = await CreateAsync();
        var context = Request("?action=internalCompanyChoose&preview=eic");
        await LocalCompanyChoiceAction.HandleAsync(context, null, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
        Assert.That(ReadBody(context), Does.Not.Contain("companyPreview"));
    }

    [Test]
    public async Task Preview_AfterChoice_RedirectsToGame()
    {
        var (sessions, session, identity) = await CreateAsync();
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "vru", CancellationToken.None), Is.True);
        var current = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!;
        var context = Request("?action=internalCompanyChoose&preview=eic");
        await LocalCompanyChoiceAction.HandleAsync(context, current, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(302));
        Assert.That(ReadBody(context), Does.Not.Contain("companyPreview"));
    }

    [Test]
    public void ValidatePreview_AcceptsOnlyCatalogCodes()
    {
        Assert.That(LocalCompanyChoiceAction.ValidatePreview(null), Is.Null);
        foreach (var company in CompanyCatalog.Companies)
            Assert.That(LocalCompanyChoiceAction.ValidatePreview(company.Code), Is.Null);
        Assert.That(LocalCompanyChoiceAction.ValidatePreview("")!.Value.Status, Is.EqualTo(400));
        Assert.That(LocalCompanyChoiceAction.ValidatePreview("eic\t")!.Value.Status, Is.EqualTo(400));
        Assert.That(LocalCompanyChoiceAction.ValidatePreview("Eic")!.Value.Status, Is.EqualTo(404));
    }

    private static async Task<(GameAuthSessionStore, GameAuthSession, CountingIdentity)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        return (sessions, session, new CountingIdentity());
    }

    private static DefaultHttpContext Request(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class CountingIdentity : IIdentityCommandService
    {
        private int setCount;
        public int SetCount => setCount;

        public Task<bool> SetCompanyAsync(Guid accountId, string company, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref setCount);
            return Task.FromResult(true);
        }

        public Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
