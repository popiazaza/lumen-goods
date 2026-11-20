using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KestrelOutfitters.StandIn.Catalogue;

internal enum PictureSize
{
    Thumb,
    Large,
}

/// <summary>
/// The stand-in serves its own pictures (the contract's kestrel.example URLs do not resolve). They are
/// small placeholder PNGs drawn in code, deterministic per product, so no image files or libraries are needed.
/// </summary>
internal static partial class ProductPictures
{
    public static string FileName(string productRef, PictureSize size) =>
        $"{productRef.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}_{(size == PictureSize.Thumb ? "t" : "l")}.png";

    /// <summary>"k7731_l.png" to ("K-7731", Large).</summary>
    public static bool TryParseFileName(string fileName, out string productRef, out PictureSize size)
    {
        var match = FileNamePattern().Match(fileName);
        productRef = match.Success ? $"K-{match.Groups["number"].Value}" : "";
        size = match.Groups["size"].Value == "t" ? PictureSize.Thumb : PictureSize.Large;
        return match.Success;
    }

    public static byte[] Render(string productRef, string group, PictureSize size)
    {
        var pixels = size == PictureSize.Thumb ? 160 : 640;
        var (r, g, b) = GroupColour(group);
        var seed = Fnv1a(productRef);
        var centreX = pixels * (0.35 + ((seed & 0xFF) / 255.0 * 0.3));
        var centreY = pixels * (0.35 + (((seed >> 8) & 0xFF) / 255.0 * 0.3));
        var radius = pixels * (0.18 + (((seed >> 16) & 0xFF) / 255.0 * 0.1));

        return Png.Encode(pixels, pixels, (x, y) =>
        {
            var dx = x - centreX;
            var dy = y - centreY;
            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                return (Lighten(r), Lighten(g), Lighten(b));
            }

            // A gentle vertical gradient keeps the placeholder from looking like a flat error tile.
            var shade = 1.0 - (0.25 * y / pixels);
            return ((byte)(r * shade), (byte)(g * shade), (byte)(b * shade));
        });
    }

    private static (byte R, byte G, byte B) GroupColour(string group) => group switch
    {
        "SOCKS" => (142, 108, 138),
        "JACKETS" => (47, 93, 138),
        "BOOTS" => (107, 79, 58),
        "PACKS" => (62, 124, 89),
        "TENTS" => (196, 127, 44),
        "LAMPS" => (217, 180, 74),
        "CAPS" => (162, 62, 72),
        _ => (122, 122, 122),
    };

    private static byte Lighten(byte channel) => (byte)(channel + ((255 - channel) * 0.6));

    // string.GetHashCode is randomised per process; pictures must be identical across restarts.
    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    [GeneratedRegex("^k(?<number>[0-9]+)_(?<size>[tl])\\.png$")]
    private static partial Regex FileNamePattern();

    /// <summary>Minimal PNG encoder: 8-bit RGB, one IDAT chunk, no filtering.</summary>
    private static class Png
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] Encode(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
        {
            var stride = (width * 3) + 1;
            var raw = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                // raw[y * stride] stays 0: filter type "None" for every scanline.
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b) = pixel(x, y);
                    var offset = (y * stride) + 1 + (x * 3);
                    raw[offset] = r;
                    raw[offset + 1] = g;
                    raw[offset + 2] = b;
                }
            }

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; // bit depth
            header[9] = 2; // colour type: truecolour RGB

            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", Deflate(raw));
            WriteChunk(png, "IEND", []);
            return png.ToArray();
        }

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            {
                zlib.Write(data);
            }

            return output.ToArray();
        }

        private static void WriteChunk(Stream png, string type, byte[] data)
        {
            var typeAndData = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(type, typeAndData);
            data.CopyTo(typeAndData, 4);

            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            png.Write(number);
            png.Write(typeAndData);
            BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
            png.Write(number);
        }

        private static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
