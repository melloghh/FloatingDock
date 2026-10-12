using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Shell;

/// <summary>
/// The single place that asks Windows for an application icon. Produces a cached PNG (via IShellItemImageFactory, up to 256 px with alpha) so that the
/// UI only ever loads plain image files - the same way for system icons, icon-pack icons and user overrides.
/// Must be called on the <see cref="ShellWorker"/> thread.
/// </summary>
public sealed class ShellIconExtractor
{
    private readonly string _cacheDir;
    private int _writes;

    public ShellIconExtractor(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        PruneCache();
    }

    /// <summary>Path of the shell item that carries the icon of <paramref name="app"/>, in preference order.</summary>
    public static IEnumerable<string> IconSources(AppRef app)
    {
        if (!string.IsNullOrEmpty(app.Aumid) && (app.Aumid.Contains('!') || string.IsNullOrEmpty(app.ExePath))) yield return "shell:AppsFolder\\" + app.Aumid;
        if (!string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath)) yield return app.ShortcutPath;
        if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath)) yield return app.ExePath;
        if (!string.IsNullOrEmpty(app.ShellParsingName)) yield return "shell:AppsFolder\\" + app.ShellParsingName;
    }

    public string? GetIconPng(AppRef app, int sizePx)
    {
        sizePx = Math.Clamp(sizePx, 16, 256);
        foreach (var source in IconSources(app))
        {
            var cached = CachePath(source, sizePx);
            if (File.Exists(cached) && IsFresh(source, cached)) return cached;
            var png = Render(source, sizePx);
            if (png is null) continue;
            try { File.WriteAllBytes(cached, png); if (++_writes % 32 == 0) PruneCache(); return cached; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    private void PruneCache()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long bytes = 0;
            for (int i = 0; i < files.Count; i++)
            {
                bytes += files[i].Length;
                if (i >= 2048 || bytes > 64L * 1024 * 1024) try { files[i].Delete(); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string CachePath(string source, int size)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant())))[..24];
        return Path.Combine(_cacheDir, $"{hash}_{size}.png");
    }

    private static bool IsFresh(string source, string cached)
    {
        try
        {
            if (!File.Exists(source)) return true;                // AppsFolder entries: refreshed by clearing the cache when the app list changes
            return File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(cached);
        }
        catch (IOException) { return true; }
    }

    private static byte[]? Render(string source, int size)
    {
        object? o = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = ShellGuids.IShellItem;
            NativeMethods.SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out o);
            var factory = (IShellItemImageFactory)o;
            int hr = factory.GetImage(new SIZE(size, size), NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            return BitmapToPng(hbm);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or FileNotFoundException) { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) NativeMethods.DeleteObject(hbm);
            if (o is not null) Marshal.FinalReleaseComObject(o);
        }
    }

    internal static byte[]? BitmapToPng(IntPtr hbm)
    {
        if (NativeMethods.GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        if (w > 4096 || h > 4096) return null;                    // a shell icon is never this large; do not allocate for a bogus size
        var hdr = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };   // top-down
        var pixels = new byte[w * h * 4];
        var dc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(dc, hbm, 0, (uint)h, pixels, ref hdr, NativeMethods.DIB_RGB_COLORS) == 0) return null;
        }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, dc); }

        // some icons come without an alpha channel: treat colour as opaque
        bool anyAlpha = false, anyColour = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0) { anyAlpha = true; break; }
            if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) anyColour = true;
        }
        if (!anyAlpha)
        {
            if (!anyColour) return null;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        return PngWriter.EncodeBgra(w, h, pixels, premultiplied: false);
    }
}
