using System.Runtime.InteropServices;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;
using Xunit;

namespace FloatingDock.Windows.Tests;

public class WindowsRegressionTests
{
    [Fact]
    public void NativeStructures_MatchTheX64WindowsAbi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<PROPVARIANT>());
        Assert.Equal(new IntPtr(8), Marshal.OffsetOf<PROPVARIANT>(nameof(PROPVARIANT.pointerValue)));
        Assert.Equal(48, Marshal.SizeOf<APPBARDATA>());
        Assert.Equal(40, Marshal.SizeOf<INPUT>());
        Assert.Equal(104, Marshal.SizeOf<MONITORINFOEX>());
        Assert.Equal(976, Marshal.SizeOf<NOTIFYICONDATA>());
        Assert.Equal(112, Marshal.SizeOf<SHELLEXECUTEINFO>());
    }

    [Fact]
    public void PropVariant_DecodesAnsiAndUnicodeUsingTheirOwnEncoding()
    {
        var ansi = Marshal.StringToCoTaskMemAnsi("Shell");
        var unicode = Marshal.StringToCoTaskMemUni("Приложение");
        try
        {
            Assert.Equal("Shell", new PROPVARIANT { vt = 30, pointerValue = ansi }.GetString());
            Assert.Equal("Приложение", new PROPVARIANT { vt = 31, pointerValue = unicode }.GetString());
        }
        finally { Marshal.FreeCoTaskMem(ansi); Marshal.FreeCoTaskMem(unicode); }
    }

    [Fact]
    public async Task ShellWorker_RejectsNewItemsAfterIdempotentDispose()
    {
        var worker = new ShellWorker();
        Assert.Equal(ApartmentState.STA, await worker.Run(() => Thread.CurrentThread.GetApartmentState()));
        worker.Dispose(); worker.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Run(() => 1));
    }

    [Fact]
    public void SingleInstance_DisposeTwice_DoesNotThrow()
    {
        var instance = new SingleInstance("FloatingDock.Test." + Guid.NewGuid().ToString("N"));
        Assert.True(instance.IsFirst);
        instance.Dispose(); instance.Dispose();
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    public async Task ScriptShortcuts_AreRejectedWithoutExecutingTheirTargets(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FloatingDock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "script" + extension);
            File.WriteAllText(target, "should never run");
            using var worker = new ShellWorker();
            var result = await worker.Run(() =>
            {
                var shortcut = Path.Combine(dir, "script.lnk");
                var link = (IShellLinkW)new ShellLinkCoClass();
                try { link.SetPath(target); ((IPersistFile)link).Save(shortcut, true); }
                finally { Marshal.FinalReleaseComObject(link); }
                return UserTileImporter.Import(shortcut, Path.Combine(dir, "copies"), _ => false);
            });
            Assert.Equal(TileImportStatus.Unsupported, result.Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "copies")));
            Assert.Equal("should never run", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, true); }
    }
}
