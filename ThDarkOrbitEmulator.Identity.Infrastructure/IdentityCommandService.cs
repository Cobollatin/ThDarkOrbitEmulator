using Microsoft.EntityFrameworkCore;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityCommandService(
    IdentityWriteDbContext dbContext,
    IGameUserIdGenerator gameUserIdGenerator) : IIdentityCommandService
{
    public async Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken)
    {
        var normalizedUsername = command.Username.Trim();

        var alreadyExists = await dbContext.Accounts.AnyAsync(
            account => account.Username == normalizedUsername,
            cancellationToken);

        if (alreadyExists)
        {
            return false;
        }

        var gameUserId = await gameUserIdGenerator.NextAsync(cancellationToken);

        dbContext.Accounts.Add(new Account
        {
            Username = normalizedUsername,
            Email = command.Email,
            GameUserId = gameUserId
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
