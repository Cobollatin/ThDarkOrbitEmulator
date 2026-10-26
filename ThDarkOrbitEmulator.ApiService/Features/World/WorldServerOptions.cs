using System.Globalization;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Provisional emulator wire contract. The Unity client's byte layout is not yet verified.
public sealed class WorldServerOptions
{
    public const string SectionName = "WorldServer";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 60954;
    public bool DynamicTcpPort { get; set; }
    public string? DynamicTcpPortRange { get; set; }
    public string Version { get; set; } = "local-1";
    public int MaximumPacketBytes { get; set; } = 65536;

    public void Validate()
    {
        if (Host != "127.0.0.1" || Port is < 0 or > 65535 || (!DynamicTcpPort && Port == 0)
            || (DynamicTcpPortRange is not null && !TryGetPortRange(out _, out _))
            || (!DynamicTcpPort && DynamicTcpPortRange is not null)
            || MaximumPacketBytes is < 64 or > 1048576
            || string.IsNullOrWhiteSpace(Version) || Version.Length > 64)
            throw new InvalidOperationException("Invalid loopback WorldServer configuration.");
    }

    public bool TryGetPortRange(out int first, out int last)
    {
        first = last = 0;
        if (string.IsNullOrWhiteSpace(DynamicTcpPortRange)) return false;
        var parts = DynamicTcpPortRange.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedFirst)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedLast)
            || parsedFirst < 1024 || parsedLast > 65535
            || parsedLast < parsedFirst || parsedLast - parsedFirst > 1023)
            return false;

        first = parsedFirst;
        last = parsedLast;
        return true;
    }
}
