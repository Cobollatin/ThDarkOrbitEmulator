using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using System.Text.Json;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalMapsXmlResponseTests
{
    [Test]
    public void MeshCatalogue_UsesLiveLeaseOwnerAndFallsBackForUnleasedMaps()
    {
        var now = DateTimeOffset.UtcNow;
        var node = new ServerMeshNode("owner", "api-game", "http://owner", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0);
        var snapshot = new ServerMeshSnapshot([node], [new ServerMapLease(1, "owner", 1, now.AddMinutes(1))]);
        var xml = XDocument.Parse(LocalMapsXml.XmlFor(new WorldServerOptions
            { Enabled = true, Port = 60954 }, snapshot));
        Assert.That((string?)xml.Root!.Elements("map").Single(map => (string?)map.Attribute("ID") == "1")
            .Attribute("GameServerIP"), Is.EqualTo("127.0.0.1:61001"));
        Assert.That((string?)xml.Root!.Elements("map").Single(map => (string?)map.Attribute("ID") == "5")
            .Attribute("GameServerIP"), Is.EqualTo("127.0.0.1:60954"));
    }

    [Test]
    public void LoginMeshMetadata_UsesSelectedMapOwnerWhileKeepingWebHost()
    {
        var now = DateTimeOffset.UtcNow;
        var node = new ServerMeshNode("owner", "api-game", "http://owner", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0);
        var snapshot = new ServerMeshSnapshot([node], [new ServerMapLease(1, "owner", 1, now.AddMinutes(1))]);
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), Username = "pilot",
            GameUserId = 15, Company = "mmo", Dosid = "opaque" };
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("example.local");
        using var json = JsonDocument.Parse(UnityLoginPayloadBuilder.Build(session, request, mesh: snapshot,
            world: new WorldServerOptions { Enabled = true, Port = 60954 }));
        Assert.That(json.RootElement.GetProperty("gameServerIP").GetString(), Is.EqualTo("127.0.0.1:61001"));
        using var baseline = JsonDocument.Parse(UnityLoginPayloadBuilder.Build(session, request));
        Assert.That(json.RootElement.GetProperty("host").GetString(),
            Is.EqualTo(baseline.RootElement.GetProperty("host").GetString()));
    }

    [Test]
    public async Task Response_HasSingleHeadersNoCookieAndUtf8BodyWithoutBom()
    {
        var (context, bytes) = await WriteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml; charset=utf-8"));
            Assert.That(context.Response.ContentLength, Is.EqualTo(bytes.Length));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo(LocalMapsXml.CacheControl));
            Assert.That(context.Response.Headers.CacheControl.Count, Is.EqualTo(1));
            Assert.That(context.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(LocalMapsXml.Xml));
        });
    }

    [Test]
    public async Task Response_IgnoresQueryAndCookies()
    {
        var (_, plain) = await WriteAsync();
        var (noisyContext, noisy) = await WriteAsync(context =>
        {
            context.Request.QueryString = new QueryString("?lang=de&map=999&<x>=1");
            context.Request.Headers.Cookie = "dosid=abc; lang=fr";
        });

        Assert.That(noisy, Is.EqualTo(plain));
        Assert.That(noisyContext.Response.Headers.SetCookie.Count, Is.Zero);
    }

    [Test]
    public void Xml_StartsWithUtf8DeclarationAndHasMapsRoot()
    {
        Assert.That(LocalMapsXml.Xml, Does.StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?><maps>"));
        var document = XDocument.Parse(LocalMapsXml.Xml);
        Assert.That(document.Declaration!.Encoding, Is.EqualTo("utf-8"));
        Assert.That(document.Root!.Name.LocalName, Is.EqualTo("maps"));
        Assert.That(document.Root.Attributes(), Is.Empty);
        Assert.That(document.Root.Elements().Select(e => e.Name.LocalName), Is.All.EqualTo("map"));
    }

    [Test]
    public void Xml_MapsCarryOnlyClientPatternAttributes()
    {
        foreach (var map in XDocument.Parse(LocalMapsXml.Xml).Root!.Elements("map"))
        {
            Assert.That(map.Attributes().Select(a => a.Name.LocalName), Is.EqualTo(new[] { "ID", "GameServerIP" }));
            Assert.That(map.Nodes(), Is.Empty);
            Assert.That(IPAddress.TryParse((string)map.Attribute("GameServerIP")!, out var ip), Is.True);
            Assert.That(IPAddress.IsLoopback(ip!), Is.True);
            Assert.That((string)map.Attribute("ID")!, Does.Match("^[1-9][0-9]{0,3}$"));
        }
    }

    [Test]
    public void Catalogue_IsUniqueNumericallyOrderedAndCoversEveryStarterMap()
    {
        var ids = XDocument.Parse(LocalMapsXml.Xml).Root!.Elements("map")
            .Select(map => (string)map.Attribute("ID")!).ToArray();

        Assert.That(ids, Is.Unique);
        Assert.That(ids.Select(int.Parse), Is.Ordered);
        Assert.That(ids, Is.EquivalentTo(CompanyCatalog.Companies.Select(c => c.StarterMap).Distinct()));
        Assert.That(ids, Is.EqualTo(LocalMapsXml.Maps.Select(map => map.Id)));
    }

    [Test]
    public void Catalogue_EveryCompanyResolvesToOneServer()
    {
        foreach (var company in CompanyCatalog.Companies)
        {
            var matches = LocalMapsXml.Maps.Where(map => map.Id == company.StarterMap).ToArray();
            Assert.That(matches, Has.Length.EqualTo(1), company.Code);
            Assert.That(matches[0].GameServerIp, Is.Not.Empty, company.Code);
        }
    }

    private static async Task<(DefaultHttpContext Context, byte[] Body)> WriteAsync(
        Action<DefaultHttpContext>? arrange = null)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        arrange?.Invoke(context);
        await LocalMapsXml.WriteResponseAsync(context.Response, CancellationToken.None);
        return (context, body.ToArray());
    }
}
