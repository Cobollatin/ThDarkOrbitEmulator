using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCompanyChoiceActionTests
{
    [Test]
    public async Task Choice_RequiresSession_AndHidesFormAfterSelection()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var identity = new IdentityChoiceStub();
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;

        var anonymous = Request("internalCompanyChoose");
        await LocalCompanyChoiceAction.HandleAsync(anonymous, null, sessions, identity, false, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));

        var choice = Request("internalCompanyChoose");
        await LocalCompanyChoiceAction.HandleAsync(choice, session, sessions, identity, false, CancellationToken.None);
        Assert.That(choice.Response.StatusCode, Is.EqualTo(200));
        Assert.That(choice.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(ReadBody(choice), Does.Contain("id=\"companySelection\""));

        var selected = Request("internalCompanyConfirm", "vru");
        await LocalCompanyChoiceAction.HandleAsync(selected, session, sessions, identity, true, CancellationToken.None);
        Assert.That(selected.Response.StatusCode, Is.EqualTo(302));
        Assert.That(selected.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalWebGL&from=internalCompanyChoose"));
        Assert.That(identity.SetCount, Is.EqualTo(1));

        var freshSession = await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None);
        Assert.That(freshSession!.Company, Is.EqualTo("vru"));
        var repeatChoice = Request("internalCompanyChoose");
        await LocalCompanyChoiceAction.HandleAsync(repeatChoice, freshSession, sessions, identity, false, CancellationToken.None);
        Assert.That(repeatChoice.Response.StatusCode, Is.EqualTo(302));
        Assert.That(repeatChoice.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalWebGL&from=internalStart"));

        var unityRepeatChoice = Request("internalCompanyChoose");
        unityRepeatChoice.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        await LocalCompanyChoiceAction.HandleAsync(unityRepeatChoice, freshSession, sessions, identity, false, CancellationToken.None);
        Assert.That(unityRepeatChoice.Response.StatusCode, Is.EqualTo(302));
        Assert.That(unityRepeatChoice.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalMenu&from=internalStart"));
    }

    [Test]
    public async Task Confirm_IsIdempotent_AndRejectsChangingFaction()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var identity = new IdentityChoiceStub();
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;

        var anonymous = Request("internalCompanyConfirm", "mmo");
        await LocalCompanyChoiceAction.HandleAsync(anonymous, null, sessions, identity, true, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var invalid = Request("internalCompanyConfirm", "unknown");
        await LocalCompanyChoiceAction.HandleAsync(invalid, session, sessions, identity, true, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));

        var first = Request("internalCompanyConfirm", "mmo");
        await LocalCompanyChoiceAction.HandleAsync(first, session, sessions, identity, true, CancellationToken.None);
        var selected = await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None);
        var retry = Request("internalCompanyConfirm", "mmo");
        await LocalCompanyChoiceAction.HandleAsync(retry, selected, sessions, identity, true, CancellationToken.None);
        var change = Request("internalCompanyConfirm", "eic");
        await LocalCompanyChoiceAction.HandleAsync(change, selected, sessions, identity, true, CancellationToken.None);

        Assert.That(first.Response.StatusCode, Is.EqualTo(302));
        Assert.That(retry.Response.StatusCode, Is.EqualTo(302));
        Assert.That(change.Response.StatusCode, Is.EqualTo(409));
        Assert.That(identity.SetCount, Is.EqualTo(1));
        Assert.That((await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!.Company,
            Is.EqualTo("mmo"));
    }

    private static DefaultHttpContext Request(string action, string? company = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?action={action}"
            + (company is null ? "" : $"&company={company}"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class IdentityChoiceStub : IIdentityCommandService
    {
        public int SetCount { get; private set; }

        public Task<bool> SetCompanyAsync(Guid accountId, string company, CancellationToken cancellationToken)
        {
            SetCount++;
            return Task.FromResult(true);
        }

        public Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
