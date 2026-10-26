using ThDarkOrbitEmulator.AppHost.ClientTesting;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-02: discovery reads endpoint metadata from feature source; synthetic fixtures stay in test output.
public class ClientEndpointCatalogSpecificationTests
{
    private string _features = null!;

    [SetUp]
    public void SetUp()
    {
        _features = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "spec02-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_features);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_features))
            Directory.Delete(_features, recursive: true);
    }

    [Test]
    public void MissingFeaturesDirectory_ReportsTheRequestedPath()
    {
        Directory.Delete(_features);
        var error = Assert.Throws<DirectoryNotFoundException>(() => ClientEndpointCatalog.Discover(_features));
        Assert.That(error!.Message, Does.Contain(_features));
    }

    [Test]
    public void DnsComments_ExpandHostsAndNormalizeMethodWhilePreservingPath()
    {
        Write("Nested/Endpoint.cs", "// DNS: One.Test,DNS:Two.Test | post /unityApi/login.php?mode=a");

        var discovered = FromFixture();
        Assert.That(discovered.Select(entry => (entry.OriginalHost, entry.Method, entry.Path)),
            Is.EquivalentTo(new[]
            {
                ("One.Test", "POST", "/unityApi/login.php?mode=a"),
                ("Two.Test", "POST", "/unityApi/login.php?mode=a")
            }));
        Assert.That(discovered.All(entry => entry.EndpointType == "game" && entry.ClientReplaceable), Is.True);
    }

    [Test]
    public void TaggedRoute_UsesFirstRouteAndMarksInference()
    {
        Write("Tagged.cs", """
            // DNS:api.example.test
            Get("/api/first").WithTags("DNS:api.example.test");
            Post("/api/second");
            """);

        var entry = FromFixture().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Method, Is.EqualTo("GET"));
            Assert.That(entry.Path, Is.EqualTo("/api/first"));
            Assert.That(entry.ApiRoute, Is.EqualTo(entry.Path));
            Assert.That(entry.EndpointType, Is.EqualTo("api"));
            Assert.That(entry.Notes, Is.EqualTo("Inferred from WithTags DNS"));
        });
    }

    [Test]
    public void ExplicitDnsComment_SuppressesTaggedRouteInference()
    {
        Write("Endpoint.cs", """
            // DNS: explicit.test | GET /index.es
            app.Post("/api/inferred", Handler).WithTags("DNS:tagged.test");
            """);

        var entry = FromFixture().Single();
        Assert.That((entry.OriginalHost, entry.Method, entry.Path, entry.Notes),
            Is.EqualTo(("explicit.test", "GET", "/index.es", "")));
    }

    [Test]
    public void DuplicateKeys_AreCaseInsensitiveAndResultsAreSorted()
    {
        Write("Endpoint.cs", "// DNS: z.test | GET /z\n// DNS: A.TEST | GET /a\n// DNS: a.test | get /A");

        var discovered = FromFixture();
        Assert.That(discovered.Select(entry => (entry.OriginalHost, entry.Path)),
            Is.EqualTo(new[] { ("A.TEST", "/a"), ("z.test", "/z") }));
    }

    [Test]
    public void ManualEntries_ArePresentEvenWithNoFeatureFiles()
    {
        var entries = ClientEndpointCatalog.Discover(_features);

        Assert.That(entries.Select(entry => (entry.OriginalHost, entry.Method, entry.Path)),
            Does.Contain(("gbl3.darkorbit.com", "GET", "/unityApi/login.php")));
        Assert.That(entries.Count(entry => entry.SourceFile == "manual"), Is.EqualTo(4));
    }

    [Test]
    public void FileWithoutDnsMetadata_ContributesNoInferredEntries()
    {
        Write("NoDns.cs", "Get(\"/api/first\"); Post(\"/api/second\");");

        Assert.That(FromFixture(), Is.Empty);
        Assert.That(ClientEndpointCatalog.Discover(_features).Count(entry => entry.SourceFile == "manual"), Is.EqualTo(4));
    }

    [Test]
    public void ExplicitComments_ProduceEachMethodAndPathWithoutRouteCalls()
    {
        Write("CommentsOnly.cs", "// DNS: alpha.test | get /unityApi/login.php?mode=One\n// DNS: alpha.test | POST /api/login");

        var entries = FromFixture();
        Assert.That(entries.Select(entry => (entry.Method, entry.Path, entry.EndpointType)),
            Is.EquivalentTo(new[]
            {
                ("GET", "/unityApi/login.php?mode=One", "game"),
                ("POST", "/api/login", "api")
            }));
        Assert.That(entries.All(entry => entry.ApiRoute == entry.Path && entry.Notes == ""), Is.True);
    }

    [Test]
    public void ExplicitEntry_CollidingWithManualEntryKeepsSourceMetadata()
    {
        Write("Login.cs", "// DNS: GBL3.DARKORBIT.COM | get /unityApi/login.php");

        var entries = ClientEndpointCatalog.Discover(_features);
        var login = entries.Single(entry => entry.OriginalHost.Equals("gbl3.darkorbit.com", StringComparison.OrdinalIgnoreCase)
            && entry.Method == "GET" && entry.Path == "/unityApi/login.php");
        Assert.Multiple(() =>
        {
            Assert.That(login.SourceFile, Does.EndWith("Login.cs"));
            Assert.That(login.OriginalHost, Is.EqualTo("GBL3.DARKORBIT.COM"));
            Assert.That(login.Notes, Is.Empty);
            Assert.That(entries.Count(entry => entry.SourceFile == "manual"), Is.EqualTo(3));
        });
    }

    [Test]
    public void SameHostAndPath_WithDifferentMethodsRemainDistinct()
    {
        Write("Methods.cs", "// DNS: api.test | GET /api/items\n// DNS: API.TEST | post /API/ITEMS");

        Assert.That(FromFixture().Select(entry => entry.Method), Is.EquivalentTo(new[] { "GET", "POST" }));
    }

    [TestCase("/unityApiX", "game")]
    [TestCase("/resources", "mock")]
    [TestCase("/api", "mock")]
    [TestCase("/api/v1", "api")]
    [TestCase("/Sas/login", "game")]
    public void PathClassification_UsesTheDocumentedLiteralPrefixes(string route, string type)
    {
        Write("Classify.cs", $"// DNS: host.test | GET {route}");

        Assert.That(FromFixture().Single().EndpointType, Is.EqualTo(type));
    }

    private ClientEndpointEntry[] FromFixture() =>
        ClientEndpointCatalog.Discover(_features).Where(entry => entry.SourceFile != "manual").ToArray();

    private void Write(string relativePath, string source)
    {
        var path = Path.Combine(_features, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }
}
