using System.Text;
using System.Xml.Linq;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;

// Provisional map catalogue: the installed client names MapsPattern.ID and
// MapsPattern.GameServerIP, but no response body has been captured.
public static class LocalMapsXml
{
    public const string ContentType = "application/xml; charset=utf-8";
    public const string CacheControl = "no-cache";

    public sealed record MapEntry(string Id, string GameServerIp);

    public static IReadOnlyList<MapEntry> Maps { get; } = CompanyCatalog.Companies
        .Select(company => company.StarterMap)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(map => int.Parse(map, System.Globalization.CultureInfo.InvariantCulture))
        .Select(id => new MapEntry(id, "127.0.0.1"))
        .ToArray();

    public static string Xml { get; } = Build();

    public static string XmlFor(WorldServerOptions options)
    {
        options.Validate();
        return Build(options.Enabled ? $"{options.Host}:{options.Port}" : "127.0.0.1");
    }

    public static string XmlFor(WorldServerOptions options, ServerMeshSnapshot snapshot)
    {
        options.Validate();
        var fallback = options.Enabled ? $"{options.Host}:{options.Port}" : "127.0.0.1";
        var now = DateTimeOffset.UtcNow;
        var addresses = Maps.ToDictionary(map => map.Id, map =>
        {
            var lease = snapshot.Leases.FirstOrDefault(item => item.MapId.ToString() == map.Id
                && item.ExpiresAtUtc > now);
            var owner = snapshot.Nodes.FirstOrDefault(node => node.NodeId == lease?.NodeId
                && node.Status == ServerMeshNodeStatus.Active && node.WorldTcpPort is > 0 and <= 65535);
            return owner is null ? fallback : $"{owner.WorldTcpHost}:{owner.WorldTcpPort}";
        });
        return Build(addresses);
    }

    private static readonly byte[] Body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Xml);

    // Query strings and cookies are ignored; the body is identical for every caller.
    public static Task WriteResponseAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.ContentLength = Body.Length;
        response.Headers.CacheControl = CacheControl;
        response.Headers.XContentTypeOptions = "nosniff";
        return response.Body.WriteAsync(Body, ct).AsTask();
    }

    public static Task WriteResponseAsync(HttpResponse response, WorldServerOptions options, CancellationToken ct)
    {
        var body = new UTF8Encoding(false).GetBytes(XmlFor(options));
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.ContentLength = body.Length;
        response.Headers.CacheControl = CacheControl;
        response.Headers.XContentTypeOptions = "nosniff";
        return response.Body.WriteAsync(body, ct).AsTask();
    }

    public static async Task WriteResponseAsync(HttpResponse response, WorldServerOptions options,
        IServerMeshRegistry registry, CancellationToken ct)
    {
        var body = new UTF8Encoding(false).GetBytes(XmlFor(options, await registry.GetSnapshotAsync(ct)));
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.ContentLength = body.Length;
        response.Headers.CacheControl = CacheControl;
        response.Headers.XContentTypeOptions = "nosniff";
        await response.Body.WriteAsync(body, ct);
    }

    private static string Build(string? server = null)
        => Build(Maps.ToDictionary(map => map.Id, map => server ?? map.GameServerIp));

    private static string Build(IReadOnlyDictionary<string, string> servers)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("maps", Maps.Select(map =>
                new XElement("map",
                    new XAttribute("ID", map.Id),
                    new XAttribute("GameServerIP", servers[map.Id])))));
        return document.Declaration + document.Root!.ToString(SaveOptions.DisableFormatting);
    }
}
