using Microsoft.EntityFrameworkCore;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;

namespace ThDarkOrbitEmulator.Identity.Infrastructure.Backoffice;

internal sealed class BackofficeIdentityService(
    IdentityReadDbContext readDbContext,
    IdentityWriteDbContext writeDbContext) : IBackofficeIdentityService
{
    public async Task<IReadOnlyList<BackofficeAccountRow>> ListAccountsAsync(int limit, CancellationToken cancellationToken)
    {
        return await readDbContext.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Username)
            .Take(limit)
            .Select(account => new BackofficeAccountRow(
                account.Id,
                account.Username,
                account.Email ?? string.Empty,
                account.GameUserId))
            .ToListAsync(cancellationToken);
    }

    public Task<long> CountAccountsAsync(CancellationToken cancellationToken) =>
        readDbContext.Accounts.LongCountAsync(cancellationToken);

    public async Task<int> DeleteAllAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await writeDbContext.Accounts.ToListAsync(cancellationToken);
        writeDbContext.Accounts.RemoveRange(accounts);
        await writeDbContext.SaveChangesAsync(cancellationToken);
        return accounts.Count;
    }
}
