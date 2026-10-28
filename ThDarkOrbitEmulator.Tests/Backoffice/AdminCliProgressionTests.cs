using System.Diagnostics;
using System.Text.Json;

namespace ThDarkOrbitEmulator.Tests.Backoffice;

public sealed class AdminCliProgressionTests
{
    [Test]
    public void CliCatalog_ParsesProgressionRoutesAndCompletesCommands()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "REPO_INDEX.md"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Repository root missing.");
        var script = Path.Combine(root, "scripts", "admin", "admin-cli.mjs").Replace('\\', '/');
        const string account = "11111111-2222-3333-4444-555555555555";
        var code = $$"""
            import { buildProgressionRequest, getCompletions } from 'file:///{{script}}';
            const account = '{{account}}';
            const lines = [
              `/hangar ${account}`, `/hangar ${account} 2`, `/skylab ${account}`,
              `/skylab ${account} collector prometium ready`, `/skylab ${account} refinery ready`,
              `/gate ${account}`, `/gate ${account} alpha reset`, `/gate ${account} beta complete`,
              '/clan mmo-guard station', '/clan mmo-guard station hull 2',
              '/clan mmo-guard relations', '/clan mmo-guard relations mmo-orbit allied mmo-guard'
            ];
            console.log(JSON.stringify({ requests: lines.map(buildProgressionRequest),
              roots: getCompletions('/g').hits, gates: getCompletions(`/gate ${account} a`).hits,
              actions: getCompletions(`/gate ${account} alpha c`).hits }));
            """;
        var start = new ProcessStartInfo("node")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(code);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, error);
        using var json = JsonDocument.Parse(output);
        var requests = json.RootElement.GetProperty("requests");
        Assert.That(requests.GetArrayLength(), Is.EqualTo(12));
        Assert.Multiple(() =>
        {
            Assert.That(requests[1].GetProperty("method").GetString(), Is.EqualTo("PUT"));
            Assert.That(requests[3].GetProperty("path").GetString(), Does.EndWith("/complete-timer"));
            Assert.That(requests[6].GetProperty("path").GetString(), Does.EndWith("/reset-wave"));
            Assert.That(requests[7].GetProperty("path").GetString(), Does.EndWith("/complete-wave"));
            Assert.That(requests[9].GetProperty("body").GetProperty("level").GetInt32(), Is.EqualTo(2));
            Assert.That(requests[11].GetProperty("body").GetProperty("status").GetString(), Is.EqualTo("allied"));
            Assert.That(json.RootElement.GetProperty("roots").EnumerateArray().Any(x => x.GetString() == "/gate "), Is.True);
            Assert.That(json.RootElement.GetProperty("gates")[0].GetString(), Does.Contain("alpha"));
            Assert.That(json.RootElement.GetProperty("actions")[0].GetString(), Does.EndWith("complete"));
        });
    }
}
