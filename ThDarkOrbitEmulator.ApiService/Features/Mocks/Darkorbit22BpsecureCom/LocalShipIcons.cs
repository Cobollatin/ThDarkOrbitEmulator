using System.IO.Compression;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Darkorbit22BpsecureCom;

// Emulator artwork for the two locally owned ships. The captured login response
// establishes the first URL's shape, but no ship image bytes were retained.
public static class LocalShipIcons
{
    private const int Size = 100;
    public const string PusatPath = "/do_img/global/items/ship/pusat-plus_100x100.png";
    public const string PhoenixPath = "/do_img/global/items/ship/phoenix_100x100.png";

    public static readonly byte[] Pusat = Draw(20, 153, 224);
    public static readonly byte[] Phoenix = Draw(232, 155, 46);

    private static byte[] Draw(byte red, byte green, byte blue)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        WriteUInt32(header, 0, Size);
        WriteUInt32(header, 4, Size);
        header[8] = 8; // 8-bit RGBA
        header[9] = 6;
        Chunk(output, "IHDR", header);

        using var pixels = new MemoryStream();
        for (var y = 0; y < Size; y++)
        {
            pixels.WriteByte(0); // no row filter
            for (var x = 0; x < Size; x++)
            {
                var dx = Math.Abs(x - 49.5);
                var dy = Math.Abs(y - 49.5);
                var hull = dy < 30 && dx < 7 + (30 - dy) * .65;
                var wing = dy < 15 && dx < 34 - dy * .8;
                var cockpit = dx < 5 && y is > 29 and < 48;
                var engine = y is > 69 and < 84 && (dx is > 9 and < 16 || dx < 5);
                if (cockpit) pixels.Write([210, 244, 255, 255]);
                else if (hull || wing) pixels.Write([red, green, blue, 255]);
                else if (engine) pixels.Write([255, 232, 132, 255]);
                else pixels.Write([5, 11, 18, 255]);
            }
        }
        using var compressed = new MemoryStream();
        pixels.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            pixels.CopyTo(zlib);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(kind);
        Span<byte> length = stackalloc byte[4];
        WriteUInt32(length, 0, data.Length);
        output.Write(length);
        output.Write(name);
        output.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        Span<byte> checksum = stackalloc byte[4];
        WriteUInt32(checksum, 0, unchecked((int)~crc));
        output.Write(checksum);
    }

    private static void WriteUInt32(Span<byte> buffer, int offset, int value)
    {
        var unsigned = unchecked((uint)value);
        buffer[offset] = (byte)(unsigned >> 24);
        buffer[offset + 1] = (byte)(unsigned >> 16);
        buffer[offset + 2] = (byte)(unsigned >> 8);
        buffer[offset + 3] = (byte)unsigned;
    }
}
