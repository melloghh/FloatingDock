using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class Dock106Tests
{
    private static byte[] Solid(int width, int height, byte alpha = 255)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 10; bytes[i + 1] = 20; bytes[i + 2] = 30; bytes[i + 3] = alpha; }
        return bytes;
    }
    private static (int W, int H) AlphaBounds(IconRaster raster)
    {
        int l = raster.Width, t = raster.Height, r = -1, b = -1;
        for (int y = 0; y < raster.Height; y++) for (int x = 0; x < raster.Width; x++)
            if (raster.Pixels[(y * raster.Width + x) * 4 + 3] > 0) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        return (r - l + 1, b - t + 1);
    }

    [Fact]
    public void TransparentPaddingDoesNotChangeVisibleIconSize()
    {
        var padded = new byte[64 * 64 * 4];
        var solid = Solid(32, 32);
        for (int y = 0; y < 32; y++) Array.Copy(solid, y * 32 * 4, padded, ((y + 16) * 64 + 16) * 4, 32 * 4);
        Assert.Equal(IconRaster.Fit(solid, 32, 32, 64).Pixels, IconRaster.Fit(padded, 64, 64, 64).Pixels);
    }

    [Fact]
    public void NonSquareIconKeepsAspectAndHasAnInsetOnEverySide()
    {
        var raster = IconRaster.Fit(Solid(64, 32), 64, 32, 64);
        var bounds = AlphaBounds(raster);
        Assert.InRange(bounds.W / (double)bounds.H, 1.95, 2.05);
        Assert.True(bounds.W <= 58 && bounds.H <= 29);
        Assert.All(raster.Pixels.Take(64 * 4), value => Assert.Equal(0, value));
        Assert.All(raster.Pixels.Skip(63 * 64 * 4), value => Assert.Equal(0, value));
    }

    [Fact]
    public void AreaResamplingRemovesAliasingAndPreservesPremultipliedAlpha()
    {
        var pixels = Solid(64, 64, 128);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) pixels[(y * 64 + x) * 4] = (byte)(x % 2 * 128);
        var raster = IconRaster.Fit(pixels, 64, 64, 32);
        Assert.Contains(Enumerable.Range(0, raster.Width * raster.Height).Select(i => raster.Pixels[i * 4]), value => value is > 15 and < 113);
        for (int i = 0; i < raster.Pixels.Length; i += 4)
            for (int channel = 0; channel < 3; channel++) Assert.True(raster.Pixels[i + channel] <= raster.Pixels[i + 3]);
    }

    [Fact]
    public void EmptyIconIsTransparentWithoutReadingOutsideTheRaster()
    {
        Assert.All(IconRaster.Fit(new byte[16 * 16 * 4], 16, 16, 32).Pixels, value => Assert.Equal(0, value));
        Assert.Throws<ArgumentException>(() => IconRaster.Fit(new byte[3], 16, 16, 32));
    }

    [Theory]
    [InlineData(24, 28, 100)]
    [InlineData(38, 50, 170)]
    [InlineData(128, 132, 300)]
    public void MagnifiedOpaqueIconsStayAboveTheActiveIndicator(int icon, int height, int scale)
    {
        var p = GeometryParams.From(new DockSettings { IconSize = icon, DockHeight = height, MaxScalePercent = scale }.Normalize());
        var metrics = WindowMetrics.Compute(p, 1, 0);
        var motion = new DockMotion();
        motion.SetEntries(new[] { new DockEntry { Kind = DockEntryKind.App, Key = "app", App = new AppRef { ExePath = @"C:\app.exe" } } }, false);
        DockLayout.Compute(p, metrics, motion.Items, metrics.WindowWidth / 2, 1);
        float indicatorTop = metrics.PlateBottom - 5.5f - 2.5f * 1.25f;
        Assert.True(motion.Items[0].Bottom <= indicatorTop - 3);
        Assert.True(motion.Items[0].Top >= 0);
    }

    [Fact]
    public void PackagedOverrideSurvivesVersionChangeAndResetsItsLegacyAlias()
    {
        using var dir = new TempDir();
        string input = dir.File("input.png");
        File.WriteAllBytes(input, PngWriter.EncodeBgra(2, 2, Solid(2, 2), false));
        var store = new IconOverrideStore(dir.File("overrides.json"), dir.File("icons"));
        string old = @"exe:c:\program files\windowsapps\OpenAI.Codex_26.1002.7124.0_x64__publisher\app\chatgpt.exe".ToLowerInvariant();
        var current = new AppRef { ExePath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1007.2314.0_x64__publisher\app\ChatGPT.exe", Aumid = "OpenAI.Codex_publisher!App" };
        store.Set(old, input);
        string before = File.ReadAllText(dir.File("overrides.json"));
        Assert.Equal(store.TryGet(old), store.TryGetForApp(current));
        Assert.Equal(before, File.ReadAllText(dir.File("overrides.json")));
        Assert.True(store.Has(current));
        Assert.True(store.ResetForApp(current));
        Assert.False(store.Has(current));
    }

    [Theory]
    [InlineData("Other.Pkg_26.1007.2314.0_x64__publisher", @"app\ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.1007.2314.0_x64__otherpublisher", @"app\ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.1007.2314.0_x64__publisher", @"app\Different.exe")]
    public void PackageAliasDoesNotMatchAnotherApp(string package, string executable)
    {
        string old = PackagedIconIdentity.FromExeKey(@"exe:c:\program files\windowsapps\OpenAI.Codex_26.1002.7124.0_x64__publisher\app\chatgpt.exe")!;
        Assert.NotEqual(old, PackagedIconIdentity.FromExeKey(@"exe:c:\program files\windowsapps\" + package + @"\" + executable));
    }

    [Theory]
    [InlineData("ScreenClippingHost.exe", false, true, true, true)]
    [InlineData("SnippingTool.exe", false, true, true, true)]
    [InlineData("ScreenSketch.exe", false, true, true, true)]
    [InlineData("SnippingTool.exe", true, true, true, false)]
    [InlineData("SnippingTool.exe", false, true, false, false)]
    [InlineData("SnippingTool.exe", false, false, true, false)]
    [InlineData("Game.exe", false, true, true, false)]
    public void CaptureSelectionIsDistinguishedFromTheEditorAndOtherFullscreenApps(string exe, bool framed, bool topmost, bool covers, bool expected)
    {
        Assert.Equal(expected, ScreenCapturePolicy.IsSelectionOverlay(exe, true, false, framed, topmost, covers));
        Assert.False(ScreenCapturePolicy.IsSelectionOverlay(exe, false, false, framed, topmost, covers));
        Assert.False(ScreenCapturePolicy.IsSelectionOverlay(exe, true, true, framed, topmost, covers));
    }

    [Theory]
    [InlineData("SnippingTool.exe", "XamlWindow", true)]
    [InlineData("SnippingTool.exe", "SnipOverlayRootWindow", true)]
    [InlineData("SnippingTool.exe", "WinUIDesktopWin32WindowClass", false)]
    [InlineData("AnotherApp.exe", "XamlWindow", false)]
    public void ModernSelectionCaptionStyleDoesNotPreventSuppression(string exe, string windowClass, bool expected)
    {
        Assert.Equal(expected, ScreenCapturePolicy.IsSelectionOverlay(exe, true, false, true, true, true, windowClass));
    }
}
