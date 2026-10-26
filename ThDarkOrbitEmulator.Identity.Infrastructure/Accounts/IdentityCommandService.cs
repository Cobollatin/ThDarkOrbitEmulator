using Microsoft.EntityFrameworkCore;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityCommandService(
    IdentityWriteDbContext dbContext,
    IGameUserIdGenerator gameUserIdGenerator) : IIdentityCommandService
{
    public async Task<bool> SetCompanyAsync(Guid accountId, string company, CancellationToken cancellationToken)
    {
        if (CompanyCatalog.Find(company) is null)
            return false;

        if (dbContext.Database.IsRelational())
        {
            // One conditional UPDATE: concurrent API instances re-check the row after the
            // first writer commits, so exactly one first choice is stored across processes.
            var codes = CompanyCatalog.Companies.Select(definition => definition.Code).ToArray();
            var updated = await dbContext.Accounts
                .Where(a => a.Id == accountId && (a.Company == null || !codes.Contains(a.Company)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Company, company), cancellationToken);
            if (updated == 1)
                return true;

            var stored = await dbContext.Accounts.AsNoTracking()
                .Where(a => a.Id == accountId)
                .Select(a => a.Company)
                .SingleOrDefaultAsync(cancellationToken);
            return string.Equals(stored, company, StringComparison.Ordinal);
        }

        var account = await dbContext.Accounts.FindAsync([accountId], cancellationToken);
        if (account is null)
            return false;

        // First valid choice wins: a stale session must not switch an account's faction.
        if (CompanyCatalog.Find(account.Company) is not null)
            return string.Equals(account.Company, company, StringComparison.Ordinal);

        account.Company = company;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

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
