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
/// The stand-in serves its own pictures (the contract's kestrel.example URLs do not resolve). They are small placeholder
/// PNGs drawn in code, so no image files or libraries are needed. A hash of the product reference picks the motif
/// (circle, rounded square, diamond, stripes or rings), where it sits, how big it is and its shade; the group sets the
/// colour, so a page of products looks varied but calm. Thumb and large are the same picture at two sizes.
/// </summary>
internal static partial class ProductPictures
{
    private enum Motif
    {
        Circle,
        RoundedSquare,
        Diamond,
        Stripes,
        Rings,
    }

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

        // string.GetHashCode is randomised per process; pictures must be identical across restarts.
        var seed = Mix(Fnv1a(productRef));
        var more = Mix(seed ^ 0x9E3779B9u);
        var motif = (Motif)(seed % 5);

        var background = Scale(GroupColour(group), 0.86 + (Fraction(more, 8) * 0.24));
        var primary = Blend(background, (255, 255, 255), 0.6);
        var secondary = Scale(background, 0.72);
        var angle = (Fraction(more, 24) - 0.5) * 0.7;

        // In units of the picture's width, so both sizes show the same picture.
        var centreX = 0.38 + (Fraction(seed, 11) * 0.24);
        var centreY = 0.38 + (Fraction(seed, 19) * 0.16);
        var radius = 0.19 + (Fraction(more, 0) * 0.1);

        return Png.Encode(pixels, pixels, (x, y) =>
        {
            var u = (x + 0.5) / pixels;
            var v = (y + 0.5) / pixels;

            // A gentle vertical gradient and a soft shadow keep the placeholder from looking like a flat error tile.
            var colour = Scale(background, 1.0 - (0.25 * v));
            var shadow = (Distance((u - centreX) / (radius * 0.95), (v - centreY - (radius * 1.25)) / (radius * 0.16)) - 1) * radius * 0.16;
            colour = Blend(colour, (0, 0, 0), 0.12 * Math.Clamp(0.5 - (shadow / 0.05), 0, 1));

            // Motif coordinates: relative to its centre, turned by the product's angle.
            var dx = u - centreX;
            var dy = v - centreY;
            var lx = (dx * Math.Cos(angle)) + (dy * Math.Sin(angle));
            var ly = (dy * Math.Cos(angle)) - (dx * Math.Sin(angle));
            foreach (var (distance, isPrimary) in Layers(motif, lx, ly, radius))
            {
                // Edges are smoothed over one pixel.
                colour = Blend(colour, isPrimary ? primary : secondary, Math.Clamp(0.5 - (distance * pixels), 0, 1));
            }

            return (ToByte(colour.R), ToByte(colour.G), ToByte(colour.B));
        });
    }

    /// <summary>The motif's layers, bottom first, as signed distances (negative inside) and which colour they take.</summary>
    private static IEnumerable<(double Distance, bool IsPrimary)> Layers(Motif motif, double x, double y, double r)
    {
        var length = Distance(x, y);
        switch (motif)
        {
            case Motif.Circle:
                yield return (length - r, true);
                yield return (Distance(x + (r * 0.35), y + (r * 0.35)) - (r * 0.26), false);
                break;
            case Motif.RoundedSquare:
                var square = RoundedBox(x, y, r * 0.9, r * 0.22);
                yield return (square, true);
                yield return (Math.Max(square, Math.Abs(y) - (r * 0.16)), false);
                break;
            case Motif.Diamond:
                yield return ((Math.Abs(x) + Math.Abs(y) - (r * 1.15)) / Math.Sqrt(2), true);
                yield return ((Math.Abs(x) + Math.Abs(y) - (r * 0.5)) / Math.Sqrt(2), false);
                break;
            case Motif.Stripes:
                var period = r * 0.42;
                var across = (x + y) / Math.Sqrt(2);
                var stripe = Math.Abs(across - (period * Math.Floor(across / period)) - (period / 2)) - (period / 4);
                yield return (length - r, true);
                yield return (Math.Max(length - r, stripe), false);
                break;
            default:
                yield return (Math.Abs(length - (r * 0.86)) - (r * 0.14), true);
                yield return (Math.Abs(length - (r * 0.5)) - (r * 0.12), false);
                yield return (length - (r * 0.18), true);
                break;
        }
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

    private static double Distance(double x, double y) => Math.Sqrt((x * x) + (y * y));

    private static double RoundedBox(double x, double y, double halfSide, double corner)
    {
        var qx = Math.Abs(x) - halfSide + corner;
        var qy = Math.Abs(y) - halfSide + corner;
        return Distance(Math.Max(qx, 0), Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - corner;
    }

    private static (double R, double G, double B) Scale((double R, double G, double B) colour, double factor) =>
        (colour.R * factor, colour.G * factor, colour.B * factor);

    private static (double R, double G, double B) Blend((double R, double G, double B) under, (double R, double G, double B) over, double alpha) =>
        (under.R + ((over.R - under.R) * alpha), under.G + ((over.G - under.G) * alpha), under.B + ((over.B - under.B) * alpha));

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 255), MidpointRounding.AwayFromZero);

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    // FNV-1a barely changes the high bits between references that differ only in the last digit; this finaliser
    // (MurmurHash3's) spreads every input bit over the whole value.
    private static uint Mix(uint hash)
    {
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35u;
        return hash ^ (hash >> 16);
    }

    /// <summary>Eight bits of <paramref name="hash"/> from <paramref name="shift"/>, as a number from 0 to 1.</summary>
    private static double Fraction(uint hash, int shift) => ((hash >> shift) & 0xFF) / 255.0;

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
