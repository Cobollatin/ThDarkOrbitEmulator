using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-07: both provisional map-resource aliases share one mesh-aware response contract.
public sealed class MapsResourceSpecificationTests
{
    [Test]
    public void EnabledWorld_AdvertisesConfiguredListenerForEveryStarterMap()
    {
        var xml = LocalMapsXml.XmlFor(new WorldServerOptions { Enabled = true, Host = "127.0.0.1", Port = 61002 });
        Assert.That(Addresses(xml).Values, Is.All.EqualTo("127.0.0.1:61002"));
        Assert.That(Addresses(xml).Keys, Is.EquivalentTo(LocalMapsXml.Maps.Select(map => map.Id)));
    }

    [Test]
    public void DisabledWorld_AdvertisesBareLoopbackInsteadOfConfiguredListener()
    {
        var xml = LocalMapsXml.XmlFor(new WorldServerOptions { Enabled = false, Host = "127.0.0.1", Port = 61002 });
        Assert.That(Addresses(xml).Values, Is.All.EqualTo("127.0.0.1"));
    }

    [Test]
    public void Lease_ChangesOnlyItsOwnMapAndLeavesOtherMapsOnFallback()
    {
        var xml = LocalMapsXml.XmlFor(World(), Snapshot(Node(ServerMeshNodeStatus.Active, 61003), Lease(1, TimeSpan.FromMinutes(10))));
        var addresses = Addresses(xml);
        Assert.That(addresses["1"], Is.EqualTo("127.0.0.3:61003"));
        Assert.That(addresses.Where(pair => pair.Key != "1").Select(pair => pair.Value),
            Is.All.EqualTo("127.0.0.1:60954"));
    }

    [TestCase(ServerMeshNodeStatus.Draining, 61003)]
    [TestCase(ServerMeshNodeStatus.Offline, 61003)]
    [TestCase(ServerMeshNodeStatus.Active, 0)]
    [TestCase(ServerMeshNodeStatus.Active, 65536)]
    public void UnavailableOwner_UsesConfiguredFallback(ServerMeshNodeStatus status, int port)
    {
        var xml = LocalMapsXml.XmlFor(World(), Snapshot(Node(status, port), Lease(1, TimeSpan.FromMinutes(10))));
        Assert.That(Addresses(xml)["1"], Is.EqualTo("127.0.0.1:60954"));
    }

    [Test]
    public void ExpiredLeaseAndMissingOwner_UseConfiguredFallback()
    {
        var expired = LocalMapsXml.XmlFor(World(), Snapshot(Node(ServerMeshNodeStatus.Active, 61003), Lease(1, TimeSpan.FromMinutes(-10))));
        var missing = LocalMapsXml.XmlFor(World(), Snapshot(null, Lease(1, TimeSpan.FromMinutes(10))));
        Assert.That(Addresses(expired)["1"], Is.EqualTo("127.0.0.1:60954"));
        Assert.That(Addresses(missing)["1"], Is.EqualTo("127.0.0.1:60954"));
    }

    [Test]
    public async Task ConfiguredResponse_HasByteAccurateHeadersAndNoCookie()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.QueryString = new QueryString("?map=999");
        context.Request.Headers.Cookie = "dosid=opaque";

        await LocalMapsXml.WriteResponseAsync(context.Response, World(), CancellationToken.None);

        var bytes = body.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo(LocalMapsXml.ContentType));
            Assert.That(context.Response.ContentLength, Is.EqualTo(bytes.Length));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-cache"));
            Assert.That(context.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(LocalMapsXml.XmlFor(World()))));
        });
    }

    private static WorldServerOptions World() => new() { Enabled = true, Host = "127.0.0.1", Port = 60954 };

    private static ServerMeshNode Node(ServerMeshNodeStatus status, int port)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServerMeshNode("owner", "api-game", "http://owner", "127.0.0.3", port,
            status, now, now, 0, [1], 0, 0);
    }

    private static ServerMapLease Lease(int mapId, TimeSpan lifetime)
        => new(mapId, "owner", 1, DateTimeOffset.UtcNow.Add(lifetime));

    private static ServerMeshSnapshot Snapshot(ServerMeshNode? node, ServerMapLease lease)
        => new(node is null ? [] : [node], [lease]);

    private static Dictionary<string, string> Addresses(string xml)
        => XDocument.Parse(xml).Root!.Elements("map").ToDictionary(
            element => (string)element.Attribute("id")!, element => (string)element.Element("gameserverIP")!);
}
