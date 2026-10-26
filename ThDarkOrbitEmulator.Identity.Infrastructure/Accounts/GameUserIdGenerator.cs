using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class GameUserIdGenerator(
    IdentityWriteDbContext dbContext,
    IOptions<IdentityDatabaseOptions> options) : IGameUserIdGenerator
{
    public async Task<long> NextAsync(CancellationToken cancellationToken = default)
    {
        var schema = PostgresIdentifier.RequireSchema(options.Value.Schema);
#pragma warning disable EF1002 // schema name validated; cannot parameterize sequence identifier
        return await dbContext.Database
            .SqlQueryRaw<long>($"SELECT nextval('{schema}.game_user_id_seq') AS \"Value\"")
#pragma warning restore EF1002
            .SingleAsync(cancellationToken);
    }
}
