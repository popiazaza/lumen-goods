using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace KestrelOutfitters.StandIn.Tests;

/// <summary>The pictures the feed points at: the sizes the stand-in has always served, and a different picture per product.</summary>
public sealed class PictureTests(KestrelStandInFactory factory) : IClassFixture<KestrelStandInFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/img/k7731_t.png", 160)]
    [InlineData("/img/k7731_l.png", 640)]
    public async Task Thumb_and_large_are_square_rgb_pngs_of_their_size(string path, int size)
    {
        using var client = factory.CreateAuthenticatedClient();

        var (width, height, _) = DecodeRgb(await client.GetByteArrayAsync(path, Ct));

        (width, height).Should().Be((size, size));
    }

    [Fact]
    public async Task Two_products_of_one_group_get_visibly_different_pictures()
    {
        using var client = factory.CreateAuthenticatedClient();

        var first = Silhouette(DecodeRgb(await client.GetByteArrayAsync("/img/k7731_l.png", Ct)));
        var second = Silhouette(DecodeRgb(await client.GetByteArrayAsync("/img/k7732_l.png", Ct)));

        // Same group colour, so the shapes must differ: a shade alone would leave every tile of a page looking alike.
        var differing = first.Zip(second).Count(p => p.First != p.Second);
        differing.Should().BeGreaterThan(first.Length / 20, "the motif's shape, position or size differs between products");
    }

    /// <summary>
    /// Which pixels belong to the drawing rather than the background, taking the left edge of each row as that row's
    /// background (the motif never reaches it, and the background shades from top to bottom).
    /// </summary>
    private static bool[] Silhouette((int Width, int Height, byte[] Rgb) image)
    {
        var (width, height, rgb) = image;
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var edge = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                var i = edge + (x * 3);
                var difference = Math.Abs(rgb[i] - rgb[edge]) + Math.Abs(rgb[i + 1] - rgb[edge + 1]) + Math.Abs(rgb[i + 2] - rgb[edge + 2]);
                mask[(y * width) + x] = difference > 90;
            }
        }

        return mask;
    }

    /// <summary>
    /// Reads IHDR, inflates the IDAT stream and drops each row's filter byte. Written here rather than reusing the
    /// stand-in's encoder, so the test checks the format independently; only filter type 0 (None) is expected.
    /// </summary>
    private static (int Width, int Height, byte[] Rgb) DecodeRgb(byte[] png)
    {
        png.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        var width = 0;
        var height = 0;
        using var idat = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                data[8].Should().Be(8, "bit depth");
                data[9].Should().Be(2, "colour type RGB");
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var rows = raw.ToArray();
        rows.Length.Should().Be(height * (1 + (width * 3)), "one filter byte plus RGB per pixel on every row");

        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            rows[y * (1 + (width * 3))].Should().Be(0, "filter type None");
            Array.Copy(rows, (y * (1 + (width * 3))) + 1, rgb, y * width * 3, width * 3);
        }

        return (width, height, rgb);
    }
}
