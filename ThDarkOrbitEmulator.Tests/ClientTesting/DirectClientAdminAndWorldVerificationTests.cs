using System.Diagnostics;
using System.Text.Json;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class DirectClientAdminAndWorldVerificationTests
{
    [Test]
    public async Task RealSpacemapScript_RendersAndAcceptsMovementAdminSetAndRollback_ForFourCompanies()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "scripts", "client-testing", "verify-spacemap-client.mjs");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(script);
        Assert.That(process.Start(), Is.True);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.That(process.ExitCode, Is.Zero, await stderr);
        using var result = JsonDocument.Parse(await stdout);
        var rows = result.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.That(rows.Select(row => row.GetProperty("company").GetString()),
            Is.EquivalentTo(new[] { "mmo", "eic", "vru", "tst" }));
        Assert.Multiple(() =>
        {
            foreach (var row in rows)
            {
                Assert.That(row.GetProperty("position").GetString(), Is.EqualTo("3400 / 2100"));
                Assert.That(row.GetProperty("speedAfterSet").GetInt32(), Is.EqualTo(600));
                Assert.That(row.GetProperty("speedAfterRollback").GetInt32(), Is.EqualTo(300));
            }
        });
    }

    private static string FindRepositoryRoot()
    {
        for (var path = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "AGENTS.md"))) return path.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
