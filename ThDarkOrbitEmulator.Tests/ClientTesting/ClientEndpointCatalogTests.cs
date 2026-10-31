using System.Text.Json;
using ThDarkOrbitEmulator.AppHost.ClientTesting;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class ClientEndpointCatalogTests
{
    [Test]
    public void Discover_IncludesCoreDarkOrbitHosts()
    {
        var repoRoot = FindRepoRoot();
        var features = Path.Combine(repoRoot, "ThDarkOrbitEmulator.ApiService", "Features");
        var entries = ClientEndpointCatalog.Discover(features);

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.Any(e => e.OriginalHost.Contains("gbl3.darkorbit.com", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(entries.Any(e => e.Path.Contains("/unityApi/", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost.Contains("darkorbit-22.bpsecure.com", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost == "darkorbit-22.bpsecure.com"
                                     && e.Method == "GET"
                                     && e.Path == "/resources/command-center/categories.xml"), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                     && e.Method == "GET"
                                     && e.Path == "/xml/resource_command_center.xml"), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                     && e.Method == "GET"
                                     && e.Path == "/xml/Categories.xml"), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                     && e.Method == "GET"
                                     && e.Path == "/xml/CommandCenter.xml"), Is.True);
        foreach (var path in new[] { "/xml/maps.xml", "/xml/maps.php" })
        {
            Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                         && e.Method == "GET" && e.Path == path), Is.True, path);
        }
        Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                     && e.Method == "GET"
                                     && e.Path == "/xml/events/expedition/expedition-boxes.xml"), Is.True);
        Assert.That(entries.Any(e => e.OriginalHost == "gbl3.darkorbit.com"
                                     && e.Method == "GET"
                                     && e.Path == "/GameAPI.php"), Is.True);
    }

    [Test]
    public void CapturedForumLink_ReachesApiServiceOnlyForGet()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes")
            .GetProperty("game-api-forum");
        Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
        Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo("/GameAPI.php"));
        Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
            .Select(method => method.GetString()), Is.EqualTo(new[] { "GET" }));
    }

    [Test]
    public void Discover_EntriesAreClientReplaceable()
    {
        var repoRoot = FindRepoRoot();
        var features = Path.Combine(repoRoot, "ThDarkOrbitEmulator.ApiService", "Features");
        var entries = ClientEndpointCatalog.Discover(features);

        Assert.That(entries.All(e => e.ClientReplaceable), Is.True);
        Assert.That(entries.Select(e => $"{e.OriginalHost}|{e.Method}|{e.Path}").Distinct().Count(), Is.EqualTo(entries.Count));
    }

    [Test]
    public void LegacyCommandCenterResourceRoute_ReachesApiServiceOnlyForGet()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes")
            .GetProperty("game-xml");
        Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
        Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(),
            Is.EqualTo("/xml/resource_command_center.xml"));
        Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
            .Select(method => method.GetString()), Is.EqualTo(new[] { "GET" }));
        Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
    }

    [Test]
    public void LegacyCategoriesRoute_ReachesApiServiceOnlyForGet()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes")
            .GetProperty("game-xml-categories");
        Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
        Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(),
            Is.EqualTo("/xml/Categories.xml"));
        Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
            .Select(method => method.GetString()), Is.EqualTo(new[] { "GET" }));
        Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
    }

    [Test]
    public void LegacyCommandCenterRoute_ReachesApiServiceOnlyForGet()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes")
            .GetProperty("game-xml-command-center");
        Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
        Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(),
            Is.EqualTo("/xml/CommandCenter.xml"));
        Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
            .Select(method => method.GetString()), Is.EqualTo(new[] { "GET" }));
        Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
    }

    [TestCase("game-xml-maps-xml", "/xml/maps.xml")]
    [TestCase("game-xml-maps-php", "/xml/maps.php")]
    [TestCase("game-xml-spacemap-maps-xml", "/spacemap/xml/maps.xml")]
    [TestCase("game-xml-spacemap-maps-php", "/spacemap/xml/maps.php")]
    [TestCase("game-xml-expedition-boxes", "/xml/events/expedition/expedition-boxes.xml")]
    public void LegacyMapsRoute_ReachesApiServiceOnlyForGet(string routeName, string path)
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes")
            .GetProperty(routeName);
        Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
        Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo(path));
        Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
            .Select(method => method.GetString()), Is.EqualTo(new[] { "GET" }));
        Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ThDarkOrbitEmulator.ApiService", "Features")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
