using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Npgsql;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class ApiServiceOperationsExtensions
{
    public static IResourceBuilder<ProjectResource> WithEmulatorOperationsCommand(
        this IResourceBuilder<ProjectResource> builder, int desiredReplicas,
        IResourceBuilder<PostgresServerResource> postgres)
    {
        builder.WithCommand(
            "operations-emulator",
            "Operations: Emulator",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Emulator report generated.",
                OperationsReports.BuildEmulator(desiredReplicas))),
            new CommandOptions
            {
                Description = "Markdown report: cluster settings, features, and gateway routes.",
                IconName = "Settings",
            });

        builder.WithCommand(
            "operations-mesh-topology",
            "Operations: Mesh topology",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Configured mesh topology generated.",
                OperationsReports.BuildMeshTopology(desiredReplicas))),
            new CommandOptions
            {
                Description = "Configured replica count, transport, and current coordination limits.",
                IconName = "Network",
            });

        builder.WithCommand("admin-vars-report", "Admin: Runtime variables",
            context => LiveVariablesAsync(postgres, context),
            new CommandOptions { Description = "Read live runtime variable values from PostgreSQL.", IconName = "Settings" });

        builder.WithCommand("admin-var-history", "Admin: Variable history",
            context => LiveHistoryAsync(postgres, context),
            new CommandOptions { Description = "Read the latest attributed variable changes.", IconName = "History" });

        builder.WithCommand("admin-command-help", "Admin: Command help",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Admin command guide generated.", OperationsReports.BuildAdminCommandHelp())),
            new CommandOptions { Description = "Unified admin command syntax and management CLI usage.", IconName = "Terminal" });

        return builder;
    }

    private static async Task<NpgsqlConnection> OpenAsync(IResourceBuilder<PostgresServerResource> postgres,
        ExecuteCommandContext context)
    {
        var connectionString = await postgres.Resource.GetConnectionStringAsync(context.CancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL connection is unavailable.");
        var options = new NpgsqlConnectionStringBuilder(connectionString) { Database = "thdarkorbit" };
        var db = new NpgsqlConnection(options.ConnectionString);
        await db.OpenAsync(context.CancellationToken);
        return db;
    }

    private static async Task<ExecuteCommandResult> LiveVariablesAsync(
        IResourceBuilder<PostgresServerResource> postgres, ExecuteCommandContext context)
    {
        try
        {
            await using var db = await OpenAsync(postgres, context);
            await using var command = new NpgsqlCommand(
                "SELECT variable_key,category,current_value,default_value FROM identity.runtime_variables ORDER BY category,variable_key", db);
            await using var reader = await command.ExecuteReaderAsync(context.CancellationToken);
            var rows = new List<IReadOnlyList<string>>();
            while (await reader.ReadAsync(context.CancellationToken))
                rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)]);
            return OperationsCommandResults.MarkdownReport("Runtime variables loaded.",
                OperationsReports.BuildAdminVariables(rows));
        }
        catch (Exception) { return CommandResults.Failure("Runtime variables are unavailable; check api-game database migrations."); }
    }

    private static async Task<ExecuteCommandResult> LiveHistoryAsync(
        IResourceBuilder<PostgresServerResource> postgres, ExecuteCommandContext context)
    {
        try
        {
            await using var db = await OpenAsync(postgres, context);
            await using var command = new NpgsqlCommand(
                "SELECT change_id,variable_key,previous_value,new_value,surface,changed_by,node_id,reason FROM identity.runtime_variable_history ORDER BY change_id DESC LIMIT 50", db);
            await using var reader = await command.ExecuteReaderAsync(context.CancellationToken);
            var rows = new List<IReadOnlyList<string>>();
            while (await reader.ReadAsync(context.CancellationToken))
                rows.Add([reader.GetInt64(0).ToString(), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6), reader.GetString(7)]);
            return OperationsCommandResults.MarkdownReport("Runtime variable history loaded.",
                OperationsReports.BuildAdminHistory(rows));
        }
        catch (Exception) { return CommandResults.Failure("Runtime variable history is unavailable; check api-game database migrations."); }
    }
}
