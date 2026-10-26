namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal sealed class ClientTestingPaths
{
    public ClientTestingPaths(string repoRoot, ClientTestingOptions options)
    {
        RepoRoot = repoRoot;
        Options = options;
        OutputDirectory = Path.Combine(repoRoot, options.OutputDirectory);
        MappingPath = Path.IsPathRooted(options.MappingPath)
            ? options.MappingPath
            : Path.Combine(repoRoot, options.MappingPath);
        ScriptsDirectory = Path.Combine(repoRoot, "scripts");
        ClientTestingScriptsDirectory = Path.Combine(repoRoot, "scripts", "client-testing");
        FindUrlsScript = Path.Combine(ScriptsDirectory, "Find-HardcodedUrls.ps1");
        ReplaceUrlsScript = Path.Combine(ScriptsDirectory, "Replace-HardcodedUrls.ps1");
        LaunchRedirectScript = Path.Combine(repoRoot, "_reversing", "Mitmproxy", "launch_redirect.py");
        TrafficLogPath = Path.Combine(OutputDirectory, "client-traffic.jsonl");
        PidLogPath = Path.Combine(OutputDirectory, "darkorbit_pids.log");
        MitmPidFile = Path.Combine(OutputDirectory, "mitmproxy.pid");
        MappingTemplatePath = Path.Combine(ClientTestingScriptsDirectory, "url-replacements.template.json");
        PatchClientUrisScript = Path.Combine(ClientTestingScriptsDirectory, "Patch-ClientUris.ps1");
        FeaturesDirectory = Path.GetFullPath(Path.Combine(repoRoot, options.ApiServiceFeaturesPath));
    }

    public string RepoRoot { get; }

    public ClientTestingOptions Options { get; }

    public string OutputDirectory { get; }

    public string MappingPath { get; }

    public string ScriptsDirectory { get; }

    public string ClientTestingScriptsDirectory { get; }

    public string FindUrlsScript { get; }

    public string ReplaceUrlsScript { get; }

    public string LaunchRedirectScript { get; }

    public string TrafficLogPath { get; }

    public string PidLogPath { get; }

    public string MitmPidFile { get; }

    public string MappingTemplatePath { get; }

    public string PatchClientUrisScript { get; }

    public string FeaturesDirectory { get; }
}

