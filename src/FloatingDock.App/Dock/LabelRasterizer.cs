using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.UI.Dock;

/// <summary>Bounded Segoe UI tooltip rasterization, once per text/DPI. No XAML measure, layout or render-target pass.</summary>
internal static class LabelRasterizer
{
    internal sealed record Image(byte[] Pixels, int Width, int Height);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int length, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    internal static unsafe Image Render(string text, double scale)
    {
        scale = Math.Clamp(scale, .5, 6);
        text = text.Length > 256 ? text[..256] : text;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), font = IntPtr.Zero, bitmap = IntPtr.Zero, oldFont = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            if (dc == IntPtr.Zero) throw new InvalidOperationException("Cannot create label DC.");
            font = CreateFont(-(int)Math.Ceiling(12.5 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* grayscale AA */, 0, "Segoe UI");
            if (font == IntPtr.Zero) throw new InvalidOperationException("Cannot create label font.");
            oldFont = SelectObject(dc, font);
            int paddingX = (int)Math.Ceiling(10 * scale), top = (int)Math.Ceiling(4 * scale), bottom = (int)Math.Ceiling(5 * scale);
            var measured = new RECT { Right = (int)Math.Ceiling(280 * scale) };
            DrawText(dc, text, text.Length, ref measured, 0x420 | 0x800 /* CALCRECT, SINGLELINE, NOPREFIX */);
            int width = Math.Clamp(measured.Right - measured.Left, 1, (int)Math.Ceiling(280 * scale)) + paddingX * 2;
            int height = Math.Clamp(measured.Bottom - measured.Top, 1, (int)Math.Ceiling(64 * scale)) + top + bottom;
            var header = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("Cannot create label pixels.");
            var output = new byte[checked(width * height * 4)];
            new Span<byte>((void*)bits, output.Length).Clear();
            oldBitmap = SelectObject(dc, bitmap);
            SetBkMode(dc, 1); SetTextColor(dc, 0xFFFFFF);
            var bounds = new RECT { Left = paddingX, Top = top, Right = width - paddingX, Bottom = height - bottom };
            DrawText(dc, text, text.Length, ref bounds, 0x20 | 0x800 | 0x8000 /* SINGLELINE, NOPREFIX, END_ELLIPSIS */);
            GdiFlush(); Marshal.Copy(bits, output, 0, output.Length);
            float radius = (float)(8 * scale), halfW = width / 2f, halfH = height / 2f;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float textAlpha = (output[i] + output[i + 1] + output[i + 2]) / (3f * 255);
                float dx = Math.Abs(x + .5f - halfW) - (halfW - radius), dy = Math.Abs(y + .5f - halfH) - (halfH - radius);
                float distance = MathF.Sqrt(Math.Max(0, dx) * Math.Max(0, dx) + Math.Max(0, dy) * Math.Max(0, dy)) + Math.Min(Math.Max(dx, dy), 0) - radius;
                float coverage = Math.Clamp(.5f - distance, 0, 1);
                float border = (coverage - Math.Clamp(.5f - distance - (float)scale, 0, 1)) * (60f / 255);
                float backgroundAlpha = (225f / 255) * coverage;
                float alpha = border + backgroundAlpha * (1 - border);
                float blue = border + 42f / 255 * backgroundAlpha * (1 - border);
                float redGreen = border + 38f / 255 * backgroundAlpha * (1 - border);
                output[i] = (byte)Math.Clamp((textAlpha + blue * (1 - textAlpha)) * 255, 0, 255);
                output[i + 1] = output[i + 2] = (byte)Math.Clamp((textAlpha + redGreen * (1 - textAlpha)) * 255, 0, 255);
                output[i + 3] = (byte)Math.Clamp((textAlpha + alpha * (1 - textAlpha)) * 255, 0, 255);
            }
            return new Image(output, width, height);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(dc, oldBitmap);
            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }
}
