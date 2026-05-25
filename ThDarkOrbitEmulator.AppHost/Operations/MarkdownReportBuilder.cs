using System.Text;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class MarkdownReportBuilder
{
    public static string Build(string title, Action<StringBuilder> writeBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"*Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        sb.AppendLine();
        writeBody(sb);
        return sb.ToString();
    }

    public static void WriteTable(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        sb.AppendLine($"| {string.Join(" | ", headers)} |");
        sb.AppendLine($"| {string.Join(" | ", headers.Select(_ => "---"))} |");
        foreach (var row in rows)
        {
            sb.AppendLine($"| {string.Join(" | ", row)} |");
        }

        sb.AppendLine();
    }

    public static void WriteLinks(StringBuilder sb, string sectionTitle, IEnumerable<(string Label, string Url)> links)
    {
        sb.AppendLine($"## {sectionTitle}");
        sb.AppendLine();
        foreach (var (label, url) in links)
        {
            sb.AppendLine($"- [{label}]({url})");
        }

        sb.AppendLine();
    }
}
