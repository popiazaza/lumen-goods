using System.Collections.Concurrent;

namespace HarborSupply.StandIn.Media;

/// <summary>
/// Draws a deterministic placeholder for each (item, view). A hash of the item code picks the motif (circle, rounded
/// square, diamond, stripes or rings), where it sits, how big it is and its exact tint; the department sets the colour
/// family, so a page of products looks varied but calm. Every view of an item shows the same motif: front as it is,
/// side narrowed like a profile, any other view (detail) enlarged. Rendered once and cached, since an item's views
/// never change.
/// </summary>
internal sealed class ProductImageRenderer
{
    private const int Size = 480;

    private static readonly Dictionary<string, double> DeptHues = new(StringComparer.Ordinal)
    {
        ["BAGS"] = 30,
        ["TEES"] = 200,
        ["OUTERWEAR"] = 140,
        ["KITCHEN"] = 10,
        ["CAMP"] = 95,
        ["MISC"] = 270,
    };

    private readonly ConcurrentDictionary<(string ItemCode, string View), byte[]> _cache = new();

    private enum Motif
    {
        Circle,
        RoundedSquare,
        Diamond,
        Stripes,
        Rings,
    }

    public byte[] Render(string itemCode, string dept, string view) =>
        _cache.GetOrAdd((itemCode, view), key => Draw(key.ItemCode, dept, key.View));

    private static byte[] Draw(string itemCode, string dept, string view)
    {
        // string.GetHashCode is randomised per process; images must be identical across restarts.
        var seed = Mix(StableHash(itemCode));
        var more = Mix(seed ^ 0x9E3779B9u);
        var motif = (Motif)(seed % 5);

        var hue = (DeptHues.GetValueOrDefault(dept, 0) + (Fraction(seed, 3) * 48) - 24 + 360) % 360;
        var primary = HsvToRgb(hue, 0.45 + (Fraction(more, 8) * 0.2), 0.5 + (Fraction(more, 16) * 0.2));
        var secondary = HsvToRgb((hue + 28) % 360, 0.28, 0.9);
        var angle = (Fraction(more, 24) - 0.5) * 0.7;

        // In units of the image width: the centre stays high enough to leave room for the shadow below it.
        var (centreX, centreY, radius) = (0.4 + (Fraction(seed, 11) * 0.2), 0.4 + (Fraction(seed, 19) * 0.14), 0.2 + (Fraction(more, 0) * 0.09));

        // The view changes the geometry, not the motif: (horizontal, vertical) scale of the motif around its centre.
        var (scaleX, scaleY) = view switch
        {
            "front" => (1.0, 1.0),
            "side" => (0.6, 1.0),
            _ => (2.2, 2.2),
        };
        if (scaleX > 1)
        {
            (centreX, centreY) = (0.5, 0.5);
        }

        var rgb = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var u = (x + 0.5) / Size;
                var v = (y + 0.5) / Size;

                // A barely visible vertical gradient and a soft shadow keep the tile from looking like an error image.
                var colour = HsvToRgb(hue, 0.1, 0.985 - (0.05 * v));
                var shadow = Ellipse(u - centreX, v - (centreY + (radius * 1.25 * scaleY)), radius * 0.95 * scaleX, radius * 0.16);
                colour = Blend(colour, (0.0, 0.0, 0.0), 0.08 * SmoothCoverage(shadow, 0.05));

                // Motif coordinates: relative to its centre, undone for the view's scale, then turned by the item's angle.
                var dx = (u - centreX) / scaleX;
                var dy = (v - centreY) / scaleY;
                var (lx, ly) = ((dx * Math.Cos(angle)) + (dy * Math.Sin(angle)), (dy * Math.Cos(angle)) - (dx * Math.Sin(angle)));

                // A distance in motif units times this is a distance in pixels; edges are smoothed over one pixel.
                var pixels = Size * Math.Min(scaleX, scaleY);
                foreach (var (distance, isPrimary) in Layers(motif, lx, ly, radius))
                {
                    colour = Blend(colour, isPrimary ? primary : secondary, Math.Clamp(0.5 - (distance * pixels), 0, 1));
                }

                var i = ((y * Size) + x) * 3;
                rgb[i] = ToByte(colour.R);
                rgb[i + 1] = ToByte(colour.G);
                rgb[i + 2] = ToByte(colour.B);
            }
        }

        return PngEncoder.EncodeRgb(Size, Size, rgb);
    }

    /// <summary>The motif's layers, bottom first, as signed distances (negative inside) and which colour they take.</summary>
    private static IEnumerable<(double Distance, bool IsPrimary)> Layers(Motif motif, double x, double y, double r)
    {
        var length = Math.Sqrt((x * x) + (y * y));
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
                yield return (length - r, false);
                yield return (Math.Max(length - r, stripe), true);
                break;
            default:
                yield return (Math.Abs(length - (r * 0.86)) - (r * 0.14), true);
                yield return (Math.Abs(length - (r * 0.5)) - (r * 0.12), false);
                yield return (length - (r * 0.18), true);
                break;
        }
    }

    private static double Distance(double x, double y) => Math.Sqrt((x * x) + (y * y));

    private static double RoundedBox(double x, double y, double halfSide, double corner)
    {
        var qx = Math.Abs(x) - halfSide + corner;
        var qy = Math.Abs(y) - halfSide + corner;
        return Distance(Math.Max(qx, 0), Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - corner;
    }

    /// <summary>Roughly 0 at the ellipse's edge, negative inside, in units of its smaller radius.</summary>
    private static double Ellipse(double x, double y, double radiusX, double radiusY) =>
        (Distance(x / radiusX, y / radiusY) - 1) * Math.Min(radiusX, radiusY);

    private static double SmoothCoverage(double distance, double softness) => Math.Clamp(0.5 - (distance / softness), 0, 1);

    private static (double R, double G, double B) Blend((double R, double G, double B) under, (double R, double G, double B) over, double alpha) =>
        (under.R + ((over.R - under.R) * alpha), under.G + ((over.G - under.G) * alpha), under.B + ((over.B - under.B) * alpha));

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 255), MidpointRounding.AwayFromZero);

    private static uint StableHash(string value)
    {
        var hash = 2166136261u; // FNV-1a
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    // FNV-1a barely changes the high bits between codes that differ only in the last digit; this finaliser
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

    private static (double R, double G, double B) HsvToRgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = value - chroma;
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }
}
