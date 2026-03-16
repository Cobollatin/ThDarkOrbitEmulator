using Microsoft.EntityFrameworkCore;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityQueryService(IdentityReadDbContext dbContext) : IIdentityQueryService
{
    public async Task<bool> ExecuteAsync(ValidateCredentialsQuery query, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Username == query.Username.Trim(), cancellationToken);

        return account is not null;
    }
}
