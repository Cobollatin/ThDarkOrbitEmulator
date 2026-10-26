using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews.Contracts;

// Captured GET /flashAPI/brNews.php XML body (decompressed from gzip in the HAR).
[XmlRoot("root")]
public sealed record BreakingNewsXmlResponse
{
    [XmlElement("layer")]
    [JsonPropertyName("layer")]
    public List<BreakingNewsLayer> Layers { get; set; } = [];
}

public sealed record BreakingNewsLayer
{
    [XmlAttribute("name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("prio")]
    [JsonPropertyName("prio")]
    public int Priority { get; set; }

    [XmlAttribute("countdown")]
    [JsonPropertyName("countdown")]
    public int Countdown { get; set; }

    [XmlAttribute("category")]
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [XmlAttribute("categoryOverrideKey")]
    [JsonPropertyName("categoryOverrideKey")]
    public string? CategoryOverrideKey { get; set; }

    [XmlElement("config")]
    [JsonPropertyName("config")]
    public BreakingNewsConfig Config { get; set; } = new();

    [XmlElement("assignments")]
    [JsonPropertyName("assignments")]
    public BreakingNewsAssignments Assignments { get; set; } = new();

    [XmlElement("actions")]
    [JsonPropertyName("actions")]
    public BreakingNewsActions Actions { get; set; } = new();
}

public sealed record BreakingNewsConfig
{
    [XmlElement("event")]
    [JsonPropertyName("event")]
    public List<BreakingNewsEvent> Events { get; set; } = [];
}

public sealed record BreakingNewsEvent
{
    [XmlAttribute("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed record BreakingNewsAssignments
{
    [XmlElement("background")]
    [JsonPropertyName("background")]
    public BreakingNewsAsset Background { get; set; } = new();

    [XmlElement("label")]
    [JsonPropertyName("label")]
    public List<BreakingNewsLabel> Labels { get; set; } = [];

    [XmlElement("overlay")]
    [JsonPropertyName("overlay")]
    public List<BreakingNewsAsset> Overlays { get; set; } = [];
}

public sealed record BreakingNewsAsset
{
    [XmlAttribute("assetID")]
    [JsonPropertyName("assetID")]
    public string AssetId { get; set; } = string.Empty;
}

public sealed record BreakingNewsLabel
{
    [XmlAttribute("text")]
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [XmlAttribute("position")]
    [JsonPropertyName("position")]
    public string Position { get; set; } = string.Empty;

    [XmlAttribute("style")]
    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;
}

public sealed record BreakingNewsActions
{
    [XmlElement("button")]
    [JsonPropertyName("button")]
    public List<BreakingNewsButton> Buttons { get; set; } = [];
}

public sealed record BreakingNewsButton
{
    [XmlAttribute("style")]
    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;

    [XmlAttribute("text")]
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [XmlAttribute("position")]
    [JsonPropertyName("position")]
    public string Position { get; set; } = string.Empty;

    [XmlElement("onclick")]
    [JsonPropertyName("onclick")]
    public List<BreakingNewsOnClick> OnClick { get; set; } = [];
}

public sealed record BreakingNewsOnClick
{
    [XmlElement("payment")]
    [JsonPropertyName("payment")]
    public List<BreakingNewsPayment> Payments { get; set; } = [];

    [XmlElement("link")]
    [JsonPropertyName("link")]
    public List<BreakingNewsLink> Links { get; set; } = [];
}

public sealed record BreakingNewsPayment
{
    [XmlAttribute("category")]
    [JsonPropertyName("category")]
    public int Category { get; set; }

    [XmlAttribute("package")]
    [JsonPropertyName("package")]
    public string Package { get; set; } = string.Empty;
}

public sealed record BreakingNewsLink
{
    [XmlAttribute("href")]
    [JsonPropertyName("href")]
    public string Href { get; set; } = string.Empty;
}
