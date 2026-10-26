using System.Reflection;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter.Contracts;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-05 "Command center resource manifest": captured GET /resources/resource_command_center.xml.
public class CommandCenterResourceManifestSpecificationTests
{
    [Test]
    public void Xml_IsTheEmbeddedCapturedSnapshot()
    {
        var path = Path.Combine(RepositoryRoot(), "ThDarkOrbitEmulator.ApiService", "Features", "Resources",
            "CommandCenter", "command-center-resources.20261021.xml");
        Assert.That(CapturedCommandCenterResources.Xml, Is.EqualTo(File.ReadAllText(path)));
        Assert.That(CapturedCommandCenterResources.Xml, Does.StartWith("<filecollection>"));
        Assert.That(Encoding.UTF8.GetBytes(CapturedCommandCenterResources.Xml).Take(3),
            Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Test]
    public void Xml_HasThreeSafeLocationsAnd609UniqueResolvableFiles()
    {
        var root = XDocument.Parse(CapturedCommandCenterResources.Xml).Root!;
        var locations = root.Elements("location").ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("path")!);
        var files = root.Elements("file").ToArray();
        var paths = files.Select(file => "/" + locations[(string)file.Attribute("location")!] +
            (string)file.Attribute("name")! + "." + (string)file.Attribute("type")!).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(root.Name.LocalName, Is.EqualTo("filecollection"));
            Assert.That(root.Attributes(), Is.Empty);
            Assert.That(root.Elements().Count(), Is.EqualTo(612));
            Assert.That(locations, Has.Count.EqualTo(3));
            Assert.That(locations.Keys, Is.EquivalentTo(new[] { "do_img_new_bg", "do_img_new_overlay", "resources_command-center" }));
            Assert.That(locations.Values, Is.All.Matches<string>(path => path.Length > 0 && !path.StartsWith('/') && path.EndsWith('/') && !path.Contains("..", StringComparison.Ordinal)));
            Assert.That(files, Has.Length.EqualTo(609));
            Assert.That(paths, Is.Unique);
            Assert.That(paths, Is.All.Matches<string>(path => path.Length <= CommandCenterVisualAssets.MaxPathLength && !path.Contains("..", StringComparison.Ordinal)));
            Assert.That(files.Select(file => (string)file.Attribute("type")!).GroupBy(type => type).ToDictionary(group => group.Key, group => group.Count()),
                Is.EquivalentTo(new Dictionary<string, int> { ["jpg"] = 577, ["png"] = 30, ["xml"] = 2 }));
            Assert.That(files.Select(file => (string)file.Attribute("version")!), Is.All.EqualTo("1"));
            Assert.That(files.Select(file => (string)file.Attribute("hash")!), Is.All.Matches<string>(hash =>
                System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{32}$")));
        });
        foreach (var path in paths)
            Assert.That(CommandCenterVisualAssets.Find(path), Is.Not.Null, path);
    }

    [Test]
    public void Xml_ContractDeserializesWithoutLosingFileAttributes()
    {
        var serializer = new XmlSerializer(typeof(CommandCenterResourcesXmlResponse));
        using var reader = new StringReader(CapturedCommandCenterResources.Xml);
        var contract = (CommandCenterResourcesXmlResponse)serializer.Deserialize(reader)!;
        Assert.Multiple(() =>
        {
            Assert.That(contract.Locations, Has.Count.EqualTo(3));
            Assert.That(contract.Files, Has.Count.EqualTo(609));
            Assert.That(contract.Files, Is.All.Matches<CommandCenterFile>(file =>
                !file.DebugView && file.Hash.Length == 32 && file.Id.Length > 0 && file.Name.Length > 0 &&
                file.Location.Length > 0 && file.Type.Length > 0 && file.Version == 1));
        });
    }

    [Test]
    public async Task Writer_EmitsExactUtf8BodyAndFixedSecurityHeaders()
    {
        var (context, body) = NewContext();
        await CapturedCommandCenterResources.WriteResponseAsync(context.Response, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
            Assert.That(body.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(CapturedCommandCenterResources.Xml)));
            Assert.That(context.Response.Headers["Content-Security-Policy"].ToString(), Is.EqualTo("frame-ancestors https://*.darkorbit.com/;"));
            Assert.That(context.Response.Headers["Strict-Transport-Security"].ToString(), Is.EqualTo("max-age=31536000; includeSubDomains"));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
        });
    }

    [Test]
    public async Task Writer_IgnoresPathQueryCookiesAndConditionalHeaders()
    {
        var (plain, plainBody) = NewContext();
        var (other, otherBody) = NewContext();
        other.Request.Path = "/xml/resource_command_center.xml";
        other.Request.QueryString = new QueryString("?v=other");
        other.Request.Headers.Cookie = "dosid=example";
        other.Request.Headers.IfNoneMatch = "*";

        await CapturedCommandCenterResources.WriteResponseAsync(plain.Response, CancellationToken.None);
        await CapturedCommandCenterResources.WriteResponseAsync(other.Response, CancellationToken.None);

        Assert.That(other.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(otherBody.ToArray(), Is.EqualTo(plainBody.ToArray()));
        Assert.That(other.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
    }

    [Test]
    public void Writer_CancelledRequestPropagatesWithoutBody()
    {
        var (context, body) = NewContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await CapturedCommandCenterResources.WriteResponseAsync(context.Response, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(body.Length, Is.Zero);
    }

    [TestCase(typeof(ResourceCommandCenterEndpoint))]
    [TestCase(typeof(LegacyXmlCommandCenterResourcesEndpoint))]
    public void Endpoints_AreSealedRequestlessResources(Type endpoint)
    {
        Assert.That(endpoint.IsSealed, Is.True);
        Assert.That(endpoint.BaseType, Is.EqualTo(typeof(EndpointWithoutRequest)));
        Assert.That(endpoint.GetMethod(nameof(EndpointWithoutRequest.HandleAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(CancellationToken)])?.DeclaringType,
            Is.EqualTo(endpoint));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
