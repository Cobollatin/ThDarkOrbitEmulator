using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class PostgresOperationsExtensions
{
    public static IResourceBuilder<PostgresServerResource> WithOperationsCommands(
        this IResourceBuilder<PostgresServerResource> builder)
    {
        builder.WithCommand(
            "operations-database",
            "Operations: Database",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Database report generated.",
                OperationsReports.BuildDatabase())),
            new CommandOptions
            {
                Description = "Markdown report: PostgreSQL layout, schemas, and tools.",
                IconName = "Database",
            });

        return builder;
    }
}
