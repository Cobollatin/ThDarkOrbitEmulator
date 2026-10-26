using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class CompanySessionTests
{
    [Test]
    public void FourthCompany_HasNextNumericFactionIdAndOwnHomeMap()
    {
        var companies = CompanyCatalog.Companies;
        Assert.That(companies, Has.Count.EqualTo(4));
        Assert.That(companies.Select(company => company.NumericFactionId), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(CompanyCatalog.Find("tst")?.StarterMap, Is.EqualTo("13"));
        Assert.That(CompanyCatalog.Find("TST"), Is.Null);
    }

    [Test]
    public async Task Selection_RequiresLiveSession_AndChangesOnlyItsLoginFaction()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var first = (await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "first", null, 1), null, CancellationToken.None)).Session;
        var second = (await store.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "second", null, 2), null, CancellationToken.None)).Session;

        Assert.That(await store.SelectCompanyAsync("unknown", "vru", CancellationToken.None), Is.False);
        Assert.That(await store.SelectCompanyAsync(first.Dosid, "invalid", CancellationToken.None), Is.False);
        Assert.That(await store.SelectCompanyAsync(first.Dosid, "vru", CancellationToken.None), Is.True);

        var selected = await store.TryGetByDosidAsync(first.Dosid, CancellationToken.None);
        var other = await store.TryGetByDosidAsync(second.Dosid, CancellationToken.None);
        Assert.That(selected!.Company, Is.EqualTo("vru"));
        Assert.That(other!.Company, Is.Null);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        var selectedPayload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(selected, context.Request))!;
        var otherPayload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(other, context.Request))!;
        Assert.That(selectedPayload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo("vru"));
        Assert.That(selectedPayload["mapID"]!.GetValue<string>(), Is.EqualTo("9"));
        Assert.That(selectedPayload["hangars"]!["data"]!["hangars"]![0]!["mapId"]!.GetValue<string>(), Is.EqualTo("9"));
        Assert.That(otherPayload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo("0"));
        Assert.That(otherPayload["mapID"]!.GetValue<string>(), Is.EqualTo("1"));
    }

    [TestCase("mmo", "mmo")]
    [TestCase("eic", "eic")]
    [TestCase("vru", "vru")]
    [TestCase("tst", "tst")]
    public void LocalCompanyIds_MapToLocalFactionValues(string company, string faction)
        => Assert.That(LocalCompanySelection.Faction(company), Is.EqualTo(faction));

    [TestCase("mmo", "mmo", "1")]
    [TestCase("eic", "eic", "5")]
    [TestCase("vru", "vru", "9")]
    [TestCase("tst", "tst", "13")]
    public void LoginPayload_UsesCompanyStarterMapForPlayerAndHangar(string company, string faction, string map)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        var session = new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session", Company = company };
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;

        Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo(faction));
        Assert.That(payload["mapID"]!.GetValue<string>(), Is.EqualTo(map));
        Assert.That(payload["hangars"]!["data"]!["hangars"]![0]!["mapId"]!.GetValue<string>(), Is.EqualTo(map));
    }

    [Test]
    public async Task AccountCompany_SurvivesFreshIdentityContextAndGameSession()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var accountId = Guid.NewGuid();
        var databaseOptions = Options.Create(new IdentityDatabaseOptions());
        var writeOptions = new DbContextOptionsBuilder<IdentityWriteDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        var readOptions = new DbContextOptionsBuilder<IdentityReadDbContext>()
            .UseInMemoryDatabase(databaseName).Options;

        await using (var write = new IdentityWriteDbContext(writeOptions, databaseOptions))
        {
            write.Accounts.Add(new Account { Id = accountId, Username = "relogin", GameUserId = 7 });
            await write.SaveChangesAsync();
            var commands = new IdentityCommandService(write, null!);
            Assert.That(await commands.SetCompanyAsync(accountId, "invalid", CancellationToken.None), Is.False);
            Assert.That(await commands.SetCompanyAsync(Guid.NewGuid(), "vru", CancellationToken.None), Is.False);
            Assert.That(await commands.SetCompanyAsync(accountId, "tst", CancellationToken.None), Is.True);
        }

        await using var read = new IdentityReadDbContext(readOptions, databaseOptions);
        var account = await new IdentityQueryService(read).ExecuteAsync(
            new GetAccountByUsernameQuery("relogin"), CancellationToken.None);
        Assert.That(account?.Company, Is.EqualTo("tst"));

        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new GameAuthSessionStore(services.GetRequiredService<IDistributedCache>());
        var freshSession = (await store.CreateSessionAsync(account!, null, CancellationToken.None)).Session;
        Assert.That(freshSession.Company, Is.EqualTo("tst"));

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(freshSession, context.Request))!;
        Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo("tst"));
        Assert.That(payload["mapID"]!.GetValue<string>(), Is.EqualTo("13"));
    }
}
