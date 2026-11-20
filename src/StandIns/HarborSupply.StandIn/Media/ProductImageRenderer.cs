using System.Collections.Concurrent;

namespace HarborSupply.StandIn.Media;

/// <summary>
/// Draws a deterministic placeholder for each (item, view): a tint chosen by department and item code,
/// with a shape per view (front: circle, side: tall bar, anything else: diagonal stripes).
/// Rendered once and cached, since an item's views never change.
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

    public byte[] Render(string itemCode, string dept, string view) =>
        _cache.GetOrAdd((itemCode, view), key => Draw(key.ItemCode, dept, key.View));

    private static byte[] Draw(string itemCode, string dept, string view)
    {
        // string.GetHashCode is randomised per process; images must be identical across restarts.
        var seed = StableHash(itemCode);
        var hue = (DeptHues.GetValueOrDefault(dept, 0) + (seed % 40) - 20 + 360) % 360;
        var background = HsvToRgb(hue, 0.12, 0.97);
        var foreground = HsvToRgb(hue, 0.55, 0.45 + ((seed >> 8) % 30) / 100.0);

        var rgb = new byte[Size * Size * 3];
        const double centre = Size / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var inShape = view switch
                {
                    "front" => Math.Pow(x - centre, 2) + Math.Pow(y - centre, 2) < Math.Pow(Size * 0.32, 2),
                    "side" => Math.Abs(x - centre) < Size * 0.14 && Math.Abs(y - centre) < Size * 0.36,
                    _ => (x + y) / 40 % 2 == 0,
                };
                var (r, g, b) = inShape ? foreground : background;
                var i = ((y * Size) + x) * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }

        return PngEncoder.EncodeRgb(Size, Size, rgb);
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u; // FNV-1a
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    private static (byte R, byte G, byte B) HsvToRgb(double hue, double saturation, double value)
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
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
