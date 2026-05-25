using ThDarkOrbitEmulator.AppHost.Operations;

namespace ThDarkOrbitEmulator.Tests.Operations;

public class MarkdownReportBuilderTests
{
    [Test]
    public void Build_WritesTitleAndTable()
    {
        var markdown = MarkdownReportBuilder.Build("Test report", sb =>
        {
            MarkdownReportBuilder.WriteTable(
                sb,
                ["A", "B"],
                [["1", "2"]]);
        });

        Assert.That(markdown, Does.Contain("# Test report"));
        Assert.That(markdown, Does.Contain("| A | B |"));
        Assert.That(markdown, Does.Contain("| 1 | 2 |"));
    }

    [Test]
    public void OperationsReports_Overview_ContainsGrafanaLink()
    {
        var markdown = OperationsReports.BuildOverview();
        Assert.That(markdown, Does.Contain("Grafana"));
        Assert.That(markdown, Does.Contain("RedisInsight"));
    }
}
