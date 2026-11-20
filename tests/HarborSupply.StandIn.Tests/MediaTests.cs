using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using static HarborSupply.StandIn.Tests.HttpTestExtensions;

namespace HarborSupply.StandIn.Tests;

/// <summary>Contract: media[].href is relative to the service's base URL and the stand-in serves the images.</summary>
public sealed class MediaTests(HarborFactory factory) : IClassFixture<HarborFactory>
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    [Trait("CT", "CT-H-11")]
    public async Task CT_H_11_every_media_href_resolves_to_a_png_with_the_key()
    {
        using var client = factory.CreateKeyedClient();
        var (items, _) = await client.GetAllPagesAsync();
        var hrefs = items.SelectMany(i => i.GetProperty("media").EnumerateArray())
            .Select(m => m.GetProperty("href").GetString()!)
            .ToList();

        hrefs.Should().NotBeEmpty().And.AllSatisfy(h => h.Should().MatchRegex(@"^/media/HSC-\d{5}/[a-z]+\.png$"));
        foreach (var href in hrefs)
        {
            using var response = await client.GetAsync(href, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, href);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
            bytes.Take(8).Should().Equal(PngSignature);
        }
    }

    [Fact]
    public async Task Images_are_valid_distinct_pngs()
    {
        using var client = factory.CreateKeyedClient();

        var front = await client.GetByteArrayAsync("/media/HSC-10021/front.png", Ct);
        var side = await client.GetByteArrayAsync("/media/HSC-10021/side.png", Ct);
        var other = await client.GetByteArrayAsync("/media/HSC-20011/front.png", Ct);

        var (width, height, pixels) = DecodeRgb(front);
        width.Should().BeGreaterThan(100);
        pixels.Length.Should().Be(height * (1 + width * 3), "one filter byte plus RGB per pixel on every row");
        front.Should().NotEqual(side);
        front.Should().NotEqual(other);
    }

    [Fact]
    [Trait("CT", "CT-H-01")]
    public async Task Media_requires_the_key()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/media/HSC-10021/front.png", Ct);

        await response.ShouldBeHarborErrorAsync(401, "UNAUTHORIZED");
    }

    [Theory]
    [InlineData("/media/HSC-99999/front.png")]
    [InlineData("/media/HSC-10021/back.png")]
    [InlineData("/media/HSC-10021/front.jpg")]
    public async Task Unknown_media_returns_404_with_the_standard_body(string href)
    {
        using var client = factory.CreateKeyedClient();

        using var response = await client.GetAsync(href, Ct);

        await response.ShouldBeHarborErrorAsync(404, "NOT_FOUND");
    }

    /// <summary>Reads IHDR and inflates the IDAT stream, checking every chunk CRC on the way.</summary>
    private static (int Width, int Height, byte[] Pixels) DecodeRgb(byte[] png)
    {
        var width = 0;
        var height = 0;
        using var idat = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Crc32(png.AsSpan(offset + 4, length + 4)).Should().Be(crc, $"{type} CRC");
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
        using var pixels = new MemoryStream();
        inflater.CopyTo(pixels);
        return (width, height, pixels.ToArray());
    }

    // Written out bit by bit (not reusing the stand-in's encoder) so the test checks the format independently.
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
