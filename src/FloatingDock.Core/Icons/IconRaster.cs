namespace FloatingDock.Core.Icons;

public sealed record IconRaster(byte[] Pixels, int Width, int Height)
{
    /// <summary>Area resampling of premultiplied BGRA. Fits visible content into a common inset, preserving aspect and alpha.</summary>
    public static IconRaster Fit(byte[] pixels, int width, int height, int size)
    {
        if (width < 1 || height < 1 || width > 512 || height > 512 || pixels.Length != checked(width * height * 4) || size is < 16 or > 256)
            throw new ArgumentException("Invalid bounded icon raster.");
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] >= 8)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        var output = new byte[size * size * 4];
        if (right < left) return new(output, size, size);
        // Discard transparent padding and very faint shadow tails without changing the visible aspect ratio.
        int sourceWidth = right - left + 1, sourceHeight = bottom - top + 1;
        int extent = Math.Max(1, (int)Math.Floor(size * .90));
        double scale = extent / (double)Math.Max(sourceWidth, sourceHeight);
        int destWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale)), destHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        int destLeft = (size - destWidth) / 2, destTop = (size - destHeight) / 2;
        for (int y = 0; y < destHeight; y++)
        {
            double y0 = top + y * sourceHeight / (double)destHeight, y1 = top + (y + 1) * sourceHeight / (double)destHeight;
            for (int x = 0; x < destWidth; x++)
            {
                double x0 = left + x * sourceWidth / (double)destWidth, x1 = left + (x + 1) * sourceWidth / (double)destWidth;
                double blue = 0, green = 0, red = 0, alpha = 0;
                for (int sy = (int)y0; sy < Math.Ceiling(y1); sy++)
                {
                    double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (int sx = (int)x0; sx < Math.Ceiling(x1); sx++)
                    {
                        double weight = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        int i = (Math.Min(height - 1, sy) * width + Math.Min(width - 1, sx)) * 4;
                        blue += pixels[i] * weight; green += pixels[i + 1] * weight; red += pixels[i + 2] * weight; alpha += pixels[i + 3] * weight;
                    }
                }
                double area = (x1 - x0) * (y1 - y0);
                int d = ((destTop + y) * size + destLeft + x) * 4;
                output[d] = (byte)Math.Clamp(Math.Round(blue / area), 0, 255);
                output[d + 1] = (byte)Math.Clamp(Math.Round(green / area), 0, 255);
                output[d + 2] = (byte)Math.Clamp(Math.Round(red / area), 0, 255);
                output[d + 3] = (byte)Math.Clamp(Math.Round(alpha / area), 0, 255);
            }
        }
        return new(output, size, size);
    }
}
