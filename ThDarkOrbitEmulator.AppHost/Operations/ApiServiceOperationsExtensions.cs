using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class ApiServiceOperationsExtensions
{
    public static IResourceBuilder<ProjectResource> WithEmulatorOperationsCommand(
        this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithCommand(
            "operations-emulator",
            "Operations: Emulator",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Emulator report generated.",
                OperationsReports.BuildEmulator())),
            new CommandOptions
            {
                Description = "Markdown report: cluster settings, features, and gateway routes.",
                IconName = "Settings",
            });

        return builder;
    }
}
