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
}
