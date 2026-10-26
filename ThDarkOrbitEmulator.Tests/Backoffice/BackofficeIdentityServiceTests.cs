using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure.Backoffice;

namespace ThDarkOrbitEmulator.Tests.Backoffice;

public class BackofficeIdentityServiceTests
{
    [Test]
    public async Task ListAccounts_OrdersBeforeLimitingAndProjectsNullableEmail()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var write = scope.ServiceProvider.GetRequiredService<IdentityWriteDbContext>();
        var zulu = NewAccount("zulu", "z@test", 1003);
        var alpha = NewAccount("alpha", null, 1001);
        var bravo = NewAccount("bravo", "b@test", 1002);
        write.Accounts.AddRange(zulu, alpha, bravo);
        await write.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IBackofficeIdentityService>();
        var rows = await service.ListAccountsAsync(2, CancellationToken.None);

        Assert.That(rows.Select(row => row.Username), Is.EqualTo(new[] { "alpha", "bravo" }));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0], Is.EqualTo(new BackofficeAccountRow(alpha.Id, "alpha", "", 1001)));
            Assert.That(rows[1], Is.EqualTo(new BackofficeAccountRow(bravo.Id, "bravo", "b@test", 1002)));
        });
        Assert.That(await service.CountAccountsAsync(CancellationToken.None), Is.EqualTo(3),
            "The list limit does not change the total account count.");
        Assert.That(await service.ListAccountsAsync(0, CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task DeleteAllAccounts_RemovesEveryAccountAndRepeatedDeleteReturnsZero()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var write = scope.ServiceProvider.GetRequiredService<IdentityWriteDbContext>();
        write.Accounts.AddRange(NewAccount("alpha", null, 1001), NewAccount("bravo", "b@test", 1002));
        await write.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IBackofficeIdentityService>();
        Assert.That(await service.DeleteAllAccountsAsync(CancellationToken.None), Is.EqualTo(2));
        Assert.That(await service.DeleteAllAccountsAsync(CancellationToken.None), Is.Zero);
        Assert.That(await service.CountAccountsAsync(CancellationToken.None), Is.Zero);
        Assert.That(await service.ListAccountsAsync(100, CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task ListCountAndDelete_WorkAgainstInMemoryDatabase()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new IdentityDatabaseOptions { Schema = "identity" }));
        services.AddDbContext<IdentityReadDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddDbContext<IdentityWriteDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IBackofficeIdentityService, BackofficeIdentityService>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityWriteDbContext>();
        writeContext.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            Username = "alpha",
            Email = "a@test",
            GameUserId = 1001
        });
        await writeContext.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IBackofficeIdentityService>();
        Assert.That(await service.CountAccountsAsync(CancellationToken.None), Is.EqualTo(1));

        var list = await service.ListAccountsAsync(10, CancellationToken.None);
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Username, Is.EqualTo("alpha"));

        var deleted = await service.DeleteAllAccountsAsync(CancellationToken.None);
        Assert.That(deleted, Is.EqualTo(1));
        Assert.That(await service.CountAccountsAsync(CancellationToken.None), Is.EqualTo(0));
    }

    private static ServiceProvider CreateProvider()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new IdentityDatabaseOptions { Schema = "identity" }));
        services.AddDbContext<IdentityReadDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddDbContext<IdentityWriteDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped<IBackofficeIdentityService, BackofficeIdentityService>();
        return services.BuildServiceProvider();
    }

    private static Account NewAccount(string username, string? email, long gameUserId) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        GameUserId = gameUserId
    };
}
