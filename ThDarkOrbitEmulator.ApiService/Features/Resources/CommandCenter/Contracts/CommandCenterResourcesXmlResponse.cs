using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter.Contracts;

// Captured GET /resources/resource_command_center.xml XML body.
[XmlRoot("filecollection")]
public sealed record CommandCenterResourcesXmlResponse
{
    [XmlElement("location")]
    [JsonPropertyName("location")]
    public List<CommandCenterLocation> Locations { get; set; } = [];

    [XmlElement("file")]
    [JsonPropertyName("file")]
    public List<CommandCenterFile> Files { get; set; } = [];
}

public sealed record CommandCenterLocation
{
    [XmlAttribute("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("path")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

public sealed record CommandCenterFile
{
    [XmlAttribute("debugView")]
    [JsonPropertyName("debugView")]
    public bool DebugView { get; set; }

    [XmlAttribute("hash")]
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [XmlAttribute("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("location")]
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [XmlAttribute("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("type")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [XmlAttribute("version")]
    [JsonPropertyName("version")]
    public int Version { get; set; }
}
