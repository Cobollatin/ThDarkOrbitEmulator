using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using StackExchange.Redis;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class RedisOperationsExtensions
{
    public static IResourceBuilder<RedisResource> WithOperationsCommands(this IResourceBuilder<RedisResource> builder)
    {
        builder.WithCommand(
            "operations-redis-info",
            "Operations: Redis info",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Redis info generated.",
                OperationsReports.BuildRedisInfo())),
            new CommandOptions
            {
                Description = "Markdown report: cache role and maintenance notes.",
                IconName = "Info",
            });

        builder.WithCommand(
            "operations-clear-cache",
            "Operations: Clear cache",
            context => ClearCacheAsync(builder, context),
            new CommandOptions
            {
                Description = "Runs FLUSHALL on this Redis instance.",
                ConfirmationMessage = "Clear all keys in Redis (FLUSHALL)?",
                IconName = "Delete",
                IconVariant = IconVariant.Filled,
            });

        return builder;
    }

    private static async Task<ExecuteCommandResult> ClearCacheAsync(
        IResourceBuilder<RedisResource> builder,
        ExecuteCommandContext context)
    {
        var connectionString = await builder.Resource.GetConnectionStringAsync()
            ?? throw new InvalidOperationException(
                $"Unable to get the '{context.ResourceName}' connection string.");

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var database = connection.GetDatabase();
        await database.ExecuteAsync("FLUSHALL");

        return CommandResults.Success();
    }
}
