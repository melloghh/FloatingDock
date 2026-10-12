using System.IO.Compression;
using System.Buffers.Binary;

namespace FloatingDock.Core.Icons;

/// <summary>Minimal PNG encoder (RGBA8, no interlace) used to store extracted shell icons in the disk cache without extra dependencies.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <param name="bgra">Top-down 32 bpp pixels, B G R A order.</param>
    /// <param name="premultiplied">True if colour channels are alpha-premultiplied (they are un-premultiplied for PNG).</param>
    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> bgra, bool premultiplied)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("bad pixel buffer");
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int ro = y * (width * 4 + 1);
            raw[ro] = 0;                                   // filter: none
            for (int x = 0; x < width; x++)
            {
                int si = (y * width + x) * 4, di = ro + 1 + x * 4;
                byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2], a = bgra[si + 3];
                if (premultiplied && a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a);
                }
                raw[di] = r; raw[di + 1] = g; raw[di + 2] = b; raw[di + 3] = a;
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw, 0, raw.Length);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (var b in t) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}
