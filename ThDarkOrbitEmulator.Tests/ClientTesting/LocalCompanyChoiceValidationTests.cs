using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCompanyChoiceValidationTests
{
    [TestCase("?action=internalCompanyConfirm")]
    [TestCase("?action=internalCompanyConfirm&company=")]
    [TestCase("?action=internalCompanyConfirm&company=MMO")]
    [TestCase("?action=internalCompanyConfirm&company=-1")]
    [TestCase("?action=internalCompanyConfirm&company=99999999999999999999")]
    [TestCase("?action=internalCompanyConfirm&company=mmo&company=eic")]
    [TestCase("?action=internalCompanyConfirm&company=mmo&quantity=1")]
    [TestCase("?action=internalCompanyConfirm&company=mmo&slot=2")]
    [TestCase("?action=internalCompanyConfirm&company=%3Cscript%3E")]
    public async Task Confirm_RejectsMalformedOrTamperedQuery_WithHtml400(string query)
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request(query);
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, true, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var body = ReadBody(context);
        Assert.That(body, Does.Contain("id=\"companyError\""));
        Assert.That(body, Does.Not.Contain("<script>"));
        Assert.That(identity.SetCount, Is.Zero);
        Assert.That((await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!.Company, Is.Null);
    }

    [Test]
    public async Task Confirm_RejectsHugeValue()
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request("?action=internalCompanyConfirm&company=mmo&from=" + new string('a', 5000));
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, true, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(identity.SetCount, Is.Zero);
    }

    [TestCase("?action=internalCompanyChoose&company=mmo")]
    [TestCase("?action=internalCompanyChoose&from=a&from=b")]
    public async Task Choose_RejectsUnknownOrRepeatedParameters(string query)
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request(query);
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(ReadBody(context), Does.Contain("Company selection failed (400)"));
    }

    [Test]
    public async Task Choose_AcceptsNavigationParameters_AndListsAllFourCompanies()
    {
        var (sessions, session, identity) = await CreateAsync();
        var context = Request("?action=internalCompanyChoose&from=internalStart&prc=100&lang=es");
        await LocalCompanyChoiceAction.HandleAsync(context, session, sessions, identity, false, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        var body = ReadBody(context);
        foreach (var company in CompanyCatalog.Companies)
        {
            Assert.That(body, Does.Contain($"id=\"confirm_{company.Code}\""));
            Assert.That(body, Does.Contain($"Faction {company.NumericFactionId} &middot; starter map {company.StarterMap}"));
        }
        Assert.That(body, Does.Contain("local test company"));
        Assert.That(body, Does.Contain("id=\"companyNotice\""));
    }

    [Test]
    public async Task MissingOrExpiredSession_ReturnsHtml401()
    {
        var (sessions, session, identity) = await CreateAsync();
        var anonymous = Request("?action=internalCompanyConfirm&company=mmo");
        await LocalCompanyChoiceAction.HandleAsync(anonymous, null, sessions, identity, true, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        Assert.That(ReadBody(anonymous), Does.Contain("externalHome"));

        await sessions.InvalidateAsync(session.Dosid, CancellationToken.None);
        var expired = Request("?action=internalCompanyConfirm&company=mmo");
        await LocalCompanyChoiceAction.HandleAsync(expired, session, sessions, identity, true, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        Assert.That(identity.SetCount, Is.Zero);
    }

    [Test]
    public async Task ConcurrentConfirmations_PersistExactlyOneCompany()
    {
        var (sessions, session, identity) = await CreateAsync();
        string[] companies = ["mmo", "eic", "vru", "tst", "mmo", "eic", "vru", "tst"];
        var contexts = companies.Select(code => Request($"?action=internalCompanyConfirm&company={code}")).ToArray();

        await Task.WhenAll(contexts.Select(context => Task.Run(() => LocalCompanyChoiceAction.HandleAsync(
            context, session, sessions, identity, true, CancellationToken.None))));

        var stored = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!.Company;
        Assert.That(identity.SetCount, Is.EqualTo(1));
        for (var i = 0; i < companies.Length; i++)
            Assert.That(contexts[i].Response.StatusCode, Is.EqualTo(companies[i] == stored ? 302 : 409));
    }

    [Test]
    public async Task StaleSecondSession_CannotSwitchPersistedAccountCompany()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var accountId = Guid.NewGuid();
        var writeOptions = new DbContextOptionsBuilder<IdentityWriteDbContext>().UseInMemoryDatabase(databaseName).Options;
        await using var write = new IdentityWriteDbContext(writeOptions, Options.Create(new IdentityDatabaseOptions()));
        write.Accounts.Add(new Account { Id = accountId, Username = "twotabs", GameUserId = 9 });
        await write.SaveChangesAsync();
        var identity = new IdentityCommandService(write, null!);

        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var sessions = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var summary = new AccountSummary(accountId, "twotabs", null, 9);
        var first = (await sessions.CreateSessionAsync(summary, null, CancellationToken.None)).Session;
        var second = (await sessions.CreateSessionAsync(summary, null, CancellationToken.None)).Session;

        var choose = Request("?action=internalCompanyConfirm&company=tst");
        await LocalCompanyChoiceAction.HandleAsync(choose, first, sessions, identity, true, CancellationToken.None);
        var stale = Request("?action=internalCompanyConfirm&company=eic");
        await LocalCompanyChoiceAction.HandleAsync(stale, second, sessions, identity, true, CancellationToken.None);
        var sameFromStale = Request("?action=internalCompanyConfirm&company=tst");
        await LocalCompanyChoiceAction.HandleAsync(sameFromStale, second, sessions, identity, true, CancellationToken.None);

        Assert.That(choose.Response.StatusCode, Is.EqualTo(302));
        Assert.That(stale.Response.StatusCode, Is.EqualTo(409));
        Assert.That(sameFromStale.Response.StatusCode, Is.EqualTo(302));
        Assert.That((await write.Accounts.FindAsync(accountId))!.Company, Is.EqualTo("tst"));
        Assert.That((await sessions.TryGetByDosidAsync(second.Dosid, CancellationToken.None))!.Company, Is.EqualTo("tst"));
    }

    [TestCase("mmo", "1")]
    [TestCase("eic", "5")]
    [TestCase("vru", "9")]
    [TestCase("tst", "13")]
    public void LoginHangars_HaveExactlyOneActiveSlot_AndCompanyMap_EvenForCorruptSlot(string company, string map)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        var session = new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session", Company = company };

        foreach (var (slot, expectedActive) in new[] { (1, 1), (2, 2), (0, 1), (-5, 1), (int.MaxValue, 1) })
        {
            var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request, slot))!;
            var hangars = payload["hangars"]!["data"]!["hangars"]!.AsArray();
            Assert.That(hangars.Count(h => h!["hangarActive"]!.GetValue<bool>()), Is.EqualTo(1));
            Assert.That(hangars.Single(h => h!["hangarActive"]!.GetValue<bool>())!["slotNumber"]!.GetValue<int>(),
                Is.EqualTo(expectedActive));
            Assert.That(hangars.All(h => h!["mapId"]!.GetValue<string>() == map), Is.True);
            Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo(company));
        }
    }

    [Test]
    public async Task IdentitySetCompany_IsFirstChoiceWins()
    {
        var writeOptions = new DbContextOptionsBuilder<IdentityWriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var write = new IdentityWriteDbContext(writeOptions, Options.Create(new IdentityDatabaseOptions()));
        var accountId = Guid.NewGuid();
        write.Accounts.Add(new Account { Id = accountId, Username = "firstwins", GameUserId = 3 });
        await write.SaveChangesAsync();
        var commands = new IdentityCommandService(write, null!);

        Assert.That(await commands.SetCompanyAsync(accountId, "vru", CancellationToken.None), Is.True);
        Assert.That(await commands.SetCompanyAsync(accountId, "vru", CancellationToken.None), Is.True);
        Assert.That(await commands.SetCompanyAsync(accountId, "mmo", CancellationToken.None), Is.False);
        Assert.That((await write.Accounts.FindAsync(accountId))!.Company, Is.EqualTo("vru"));
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
