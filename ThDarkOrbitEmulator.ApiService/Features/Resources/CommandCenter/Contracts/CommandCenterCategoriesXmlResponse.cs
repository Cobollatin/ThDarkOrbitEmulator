using System.Xml.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter.Contracts;

// Public GET /resources/command-center/categories.xml asset, fetched 2026-10-22.
[XmlRoot("root")]
public sealed class CommandCenterCategoriesXmlResponse
{
    [XmlElement("category")]
    public List<CommandCenterCategory> Categories { get; set; } = [];
}

public sealed class CommandCenterCategory
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("prio")]
    public int Priority { get; set; }

    [XmlAttribute("textKey")]
    public string TextKey { get; set; } = string.Empty;

    [XmlAttribute("isSpecial")]
    public bool IsSpecial { get; set; }

    [XmlIgnore]
    public bool IsSpecialSpecified { get; set; }
}
