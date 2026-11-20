using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HarborSupply.StandIn.Media;

/// <summary>
/// Minimal PNG writer (8-bit RGB, no filtering, one IDAT chunk). Enough for placeholder product images
/// without an imaging library, so the stand-in carries no third-party licence.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgb">width * height * 3 bytes, row by row.</param>
    public static byte[] EncodeRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(rgb.Length, width * height * 3, nameof(rgb));

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // colour type: RGB

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var rowLength = width * 3;
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter type "None" for every scanline
                zlib.Write(rgb.Slice(y * rowLength, rowLength));
            }
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);

        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData.AsSpan(4));
        output.Write(typeAndData);

        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        output.Write(buffer);
    }

    // CRC-32 (ISO 3309), as the PNG spec requires over each chunk's type and data.
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
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
