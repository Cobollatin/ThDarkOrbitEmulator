using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Npgsql;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class IdentityBackofficeExtensions
{
    public static IResourceBuilder<PostgresServerResource> WithIdentityBackofficeCommands(
        this IResourceBuilder<PostgresServerResource> builder,
        string databaseName = "thdarkorbit")
    {
        builder.WithCommand(
            "bo-identity-users",
            "BO: List identity users",
            context => ListUsersAsync(builder, databaseName, context, limit: 50),
            new CommandOptions
            {
                Description = "Reads identity.accounts (username, email, game user id).",
                IconName = "People",
            });

        builder.WithCommand(
            "bo-identity-user-count",
            "BO: Identity user count",
            context => UserCountAsync(builder, databaseName, context),
            new CommandOptions
            {
                Description = "COUNT(*) on identity.accounts.",
                IconName = "NumberSymbol",
            });

        builder.WithCommand(
            "bo-identity-delete-all",
            "BO: Delete all identity users",
            context => DeleteAllUsersAsync(builder, databaseName, context),
            new CommandOptions
            {
                Description = "DELETE FROM identity.accounts (dev only).",
                ConfirmationMessage = "Delete ALL rows in identity.accounts? This cannot be undone.",
                IconName = "Delete",
                IconVariant = IconVariant.Filled,
            });

        return builder;
    }

    private static async Task<ExecuteCommandResult> ListUsersAsync(
        IResourceBuilder<PostgresServerResource> builder,
        string databaseName,
        ExecuteCommandContext context,
        int limit)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(builder, databaseName, context);
            await using var command = new NpgsqlCommand(
                """
                SELECT id::text, username, COALESCE(email, ''), game_user_id
                FROM identity.accounts
                ORDER BY username
                LIMIT @limit
                """,
                connection);
            command.Parameters.AddWithValue("limit", limit);

            var rows = new List<IReadOnlyList<string>>();
            await using var reader = await command.ExecuteReaderAsync(context.CancellationToken);
            while (await reader.ReadAsync(context.CancellationToken))
            {
                rows.Add(
                [
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3).ToString()
                ]);
            }

            var markdown = MarkdownReportBuilder.Build("Identity users (identity.accounts)", sb =>
            {
                sb.AppendLine($"Database **{databaseName}**, schema **identity**.");
                sb.AppendLine();
                if (rows.Count == 0)
                {
                    sb.AppendLine("*No users found.*");
                }
                else
                {
                    MarkdownReportBuilder.WriteTable(
                        sb,
                        ["Id", "Username", "Email", "Game user id"],
                        rows);
                }
            });

            return OperationsCommandResults.MarkdownReport("Identity user list generated.", markdown);
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(
                $"Failed to list identity users: {ex.Message}",
                ex.ToString(),
                CommandResultFormat.Text);
        }
    }

    private static async Task<ExecuteCommandResult> UserCountAsync(
        IResourceBuilder<PostgresServerResource> builder,
        string databaseName,
        ExecuteCommandContext context)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(builder, databaseName, context);
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM identity.accounts",
                connection);
            var count = (long)(await command.ExecuteScalarAsync(context.CancellationToken) ?? 0L);

            var markdown = MarkdownReportBuilder.Build("Identity user count", sb =>
            {
                MarkdownReportBuilder.WriteTable(
                    sb,
                    ["Metric", "Value"],
                    [["Total accounts", count.ToString()]]);
            });

            return OperationsCommandResults.MarkdownReport("Identity user count generated.", markdown);
        }
        catch (Exception ex)
        {
            return CommandResults.Failure($"Failed to count users: {ex.Message}");
        }
    }

    private static async Task<ExecuteCommandResult> DeleteAllUsersAsync(
        IResourceBuilder<PostgresServerResource> builder,
        string databaseName,
        ExecuteCommandContext context)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(builder, databaseName, context);
            await using var command = new NpgsqlCommand("DELETE FROM identity.accounts", connection);
            var deleted = await command.ExecuteNonQueryAsync(context.CancellationToken);

            var markdown = MarkdownReportBuilder.Build("Identity users deleted", sb =>
            {
                sb.AppendLine($"Removed **{deleted}** row(s) from `identity.accounts`.");
                sb.AppendLine();
                sb.AppendLine("Keycloak users are **not** removed by this command.");
            });

            return OperationsCommandResults.MarkdownReport($"Deleted {deleted} identity account(s).", markdown);
        }
        catch (Exception ex)
        {
            return CommandResults.Failure($"Failed to delete identity users: {ex.Message}");
        }
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync(
        IResourceBuilder<PostgresServerResource> builder,
        string databaseName,
        ExecuteCommandContext context)
    {
        var connectionString = await builder.Resource.GetConnectionStringAsync(context.CancellationToken)
            ?? throw new InvalidOperationException(
                $"Unable to get connection string for '{context.ResourceName}'.");

        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName
        };

        var connection = new NpgsqlConnection(csb.ConnectionString);
        await connection.OpenAsync(context.CancellationToken);
        return connection;
    }
}
