using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class GatewayOperationsExtensions
{
    public static IResourceBuilder<ProjectResource> WithOperationsCommands(this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithCommand(
            "operations-overview",
            "Operations: Overview",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Operations overview generated.",
                OperationsReports.BuildOverview())),
            new CommandOptions
            {
                Description = "Markdown report: stack summary and quick links.",
                IconName = "Home",
            });

        builder.WithCommand(
            "operations-deployments",
            "Operations: Deployments",
            _ => Task.FromResult(OperationsCommandResults.MarkdownReport(
                "Deployments report generated.",
                OperationsReports.BuildDeployments())),
            new CommandOptions
            {
                Description = "Markdown report: AppHost resources and stack info.",
                IconName = "Layer",
            });

        return builder;
    }
}
