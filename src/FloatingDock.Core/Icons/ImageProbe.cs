namespace FloatingDock.Core.Icons;

/// <summary>Limits for every locally supplied icon (user override, icon pack entry). They are checked on the file header BEFORE any decoder sees the file.</summary>
public static class IconLimits
{
    /// <summary>Largest single icon file.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;
    /// <summary>Largest width / height of a PNG (or of a PNG stored inside an ICO). Decoding needs about 4 bytes per pixel.</summary>
    public const int MaxDimension = 4096;
    /// <summary>Largest pixel count (4096 x 4096 would be 64 MB decoded; this allows 2048 x 4096 at most).</summary>
    public const long MaxPixels = 4096L * 2048;
    /// <summary>Largest number of images inside one ICO.</summary>
    public const int MaxIcoImages = 64;
    /// <summary>Largest number of per-app overrides.</summary>
    public const int MaxOverrides = 1000;
    /// <summary>Largest number of icon files in one icon pack.</summary>
    public const int MaxPackImages = 5000;
    /// <summary>Largest number of installed icon packs.</summary>
    public const int MaxPacks = 100;
}

public enum ImageFormat { Unknown, Png, Ico }

public readonly record struct ImageInfo(ImageFormat Format, int Width, int Height, int ImageCount);

/// <summary>Reads just enough of a PNG / ICO header to know its dimensions. Never decodes pixels.</summary>
public static class ImageProbe
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Validates a file on disk. Returns null and sets <paramref name="error"/> when it violates <see cref="IconLimits"/> or is not a PNG / ICO.</summary>
    public static ImageInfo? Probe(string path, out string? error)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { error = "The file does not exist."; return null; }
            if (fi.Length > IconLimits.MaxFileBytes) { error = "The file is too large."; return null; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Probe(fs, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static ImageInfo? Probe(Stream stream, out string? error)
    {
        long length = stream.Length;
        if (length > IconLimits.MaxFileBytes) { error = "The file is too large."; return null; }
        if (length < 8) { error = "The file is too small to be an image."; return null; }
        var head = new byte[Math.Min(length, 8 + 4 + 4 + 8)];
        stream.Position = 0;
        stream.ReadExactly(head);

        if (head.AsSpan(0, 8).SequenceEqual(PngSignature)) return ProbePng(head, out error);
        if (head[0] == 0 && head[1] == 0 && (head[2] == 1 || head[2] == 2) && head[3] == 0) return ProbeIco(stream, length, out error);
        error = "Only PNG and ICO images are supported.";
        return null;
    }

    private static ImageInfo? ProbePng(byte[] head, out string? error)
    {
        // signature (8) + chunk length (4) + "IHDR" (4) + width (4) + height (4)
        if (head.Length < 24 || head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') { error = "The PNG header is damaged."; return null; }
        long w = ReadBe32(head, 16), h = ReadBe32(head, 20);
        return CheckDimensions(ImageFormat.Png, w, h, 1, out error);
    }

    private static ImageInfo? CheckDimensions(ImageFormat format, long w, long h, int count, out string? error)
    {
        if (w <= 0 || h <= 0) { error = "The image has no size."; return null; }
        if (w > IconLimits.MaxDimension || h > IconLimits.MaxDimension) { error = $"The image is too large ({w}x{h}, at most {IconLimits.MaxDimension}x{IconLimits.MaxDimension})."; return null; }
        if (w * h > IconLimits.MaxPixels) { error = $"The image has too many pixels ({w}x{h})."; return null; }
        error = null;
        return new ImageInfo(format, (int)w, (int)h, count);
    }

    private static ImageInfo? ProbeIco(Stream s, long length, out string? error)
    {
        var dirHead = new byte[6];
        s.Position = 0;
        s.ReadExactly(dirHead);
        int count = dirHead[4] | dirHead[5] << 8;
        if (count < 1) { error = "The icon contains no images."; return null; }
        if (count > IconLimits.MaxIcoImages) { error = "The icon contains too many images."; return null; }
        if (6 + count * 16L > length) { error = "The icon directory is damaged."; return null; }
        var dir = new byte[count * 16];
        s.ReadExactly(dir);
        int maxW = 0, maxH = 0;
        for (int i = 0; i < count; i++)
        {
            var e = dir.AsSpan(i * 16, 16);
            int w = e[0] == 0 ? 256 : e[0], h = e[1] == 0 ? 256 : e[1];
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8, 4));
            long offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12, 4));
            if (size <= 0 || offset < 6 + count * 16L || offset + size > length) { error = "The icon directory is damaged."; return null; }
            // an image inside an ICO may be a PNG that is larger than 256 pixels: check its real header
            bool pngImage = false;
            if (size >= 24)
            {
                var ph = new byte[24];
                s.Position = offset;
                s.ReadExactly(ph);
                if (ph.AsSpan(0, 8).SequenceEqual(PngSignature))
                {
                    pngImage = true;
                    var inner = ProbePng(ph, out error);
                    if (inner is null) return null;
                    w = inner.Value.Width; h = inner.Value.Height;
                }
            }
            if (!pngImage)
            {
                if (size < 12) { error = "The ICO bitmap header is truncated."; return null; }
                var dib = new byte[12]; s.Position = offset; s.ReadExactly(dib);
                uint headerSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(0, 4));
                long realWidth, realHeight;
                if (headerSize == 12)
                {
                    realWidth = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(4, 2));
                    realHeight = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(6, 2));
                }
                else if (headerSize >= 40 && headerSize <= size)
                {
                    realWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4));
                    realHeight = Math.Abs((long)System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4)));
                }
                else { error = "The ICO bitmap header is invalid."; return null; }
                if (realHeight % 2 != 0) { error = "The ICO bitmap height is invalid."; return null; }
                if (CheckDimensions(ImageFormat.Ico, realWidth, realHeight / 2, count, out error) is null) return null;
                w = (int)realWidth; h = (int)(realHeight / 2);
            }
            maxW = Math.Max(maxW, w); maxH = Math.Max(maxH, h);
        }
        return CheckDimensions(ImageFormat.Ico, maxW, maxH, count, out error);
    }

    private static long ReadBe32(byte[] b, int o) => (long)b[o] << 24 | (long)b[o + 1] << 16 | (long)b[o + 2] << 8 | b[o + 3];
}
