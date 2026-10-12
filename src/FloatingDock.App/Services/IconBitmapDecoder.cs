using FloatingDock.Core.Icons;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FloatingDock.UI.Services;

internal static class IconBitmapDecoder
{
    internal static async Task<IconRaster> DecodeAsync(byte[] bytes, int pixels)
    {
        pixels = Math.Clamp(pixels, 16, 256);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        BitmapFrame frame = await decoder.GetFrameAsync(0);
        // ICO's first frame can be 16x16 even when a 256px frame is present.
        double best = double.MaxValue;
        for (uint i = 0; decoder.FrameCount > 1 && i < Math.Min(decoder.FrameCount, 64); i++)
        {
            var candidate = await decoder.GetFrameAsync(i);
            double dimension = Math.Max(candidate.PixelWidth, candidate.PixelHeight);
            double score = dimension >= pixels ? dimension - pixels : 4096 + pixels - dimension;
            if (score < best) { frame = candidate; best = score; }
        }
        // Prefilter to the largest displayed size once. A 256/512px texture sampled
        // directly into a 32px slot with bilinear filtering aliases the artwork and
        // its thin rounded border. Preserve the whole canvas; never crop or sharpen.
        double factor = Math.Min(1, pixels / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(frame.PixelWidth * factor)), height = Math.Max(1, (uint)Math.Round(frame.PixelHeight * factor));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = data.DetachPixelData();
        return new IconRaster(bgra, (int)width, (int)height);
    }
}
