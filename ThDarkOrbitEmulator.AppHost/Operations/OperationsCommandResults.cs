using Aspire.Hosting.ApplicationModel;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class OperationsCommandResults
{
    public static ExecuteCommandResult MarkdownReport(string statusMessage, string markdown, bool displayImmediately = true) =>
        CommandResults.Success(
            message: statusMessage,
            value: new CommandResultData
            {
                Value = markdown,
                Format = CommandResultFormat.Markdown,
                DisplayImmediately = displayImmediately
            });
}
