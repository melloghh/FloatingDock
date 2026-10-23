using System.Runtime.InteropServices;
using FloatingDock.Core.Services;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;
using Xunit;

namespace FloatingDock.Windows.Tests;

public sealed class TileImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveDoesNotReportSuccessOrKeepAnOrphanShortcut(bool shortcut)
    {
        var root = Path.Combine(Path.GetTempPath(), "FloatingDock-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AppListStore(Path.Combine(root, "library.json"));
            Directory.CreateDirectory(Path.Combine(root, "library.json")); // Refuse every save.
            int failures = 0;
            store.SaveFailed += _ => failures++;
            using var worker = new ShellWorker();
            var copies = Path.Combine(root, "copies");
            var result = await worker.Run(() =>
            {
                var source = shortcut ? CreateShortcut(root) : Environment.ProcessPath!;
                return UserTileImporter.ImportAndSave(source, copies, store);
            });
            Assert.Equal(TileImportStatus.SaveFailed, result.Status);
            Assert.Null(result.App);
            Assert.NotEmpty(result.Message!);
            Assert.Empty(store.Snapshot());
            Assert.Equal(1, failures);
            Assert.True(!Directory.Exists(copies) || !Directory.EnumerateFiles(copies).Any());
            if (shortcut) Assert.True(File.Exists(Path.Combine(root, "Original.lnk")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SuccessfulShortcutImportPersistsOneCopyAndRejectsDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "FloatingDock-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AppListStore(Path.Combine(root, "library.json"));
            using var worker = new ShellWorker();
            var copies = Path.Combine(root, "copies");
            await worker.Run(() =>
            {
                var source = CreateShortcut(root);
                Assert.Equal(TileImportStatus.Added, UserTileImporter.ImportAndSave(source, copies, store).Status);
                Assert.Equal(TileImportStatus.Duplicate, UserTileImporter.ImportAndSave(source, copies, store).Status);
            });
            Assert.Single(Directory.GetFiles(copies));
            var saved = Assert.Single(new AppListStore(Path.Combine(root, "library.json")).Snapshot());
            Assert.True(File.Exists(saved.ShortcutPath));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateShortcut(string root)
    {
        var path = Path.Combine(root, "Original.lnk");
        var link = (IShellLinkW)new ShellLinkCoClass();
        try { link.SetPath(Environment.ProcessPath!); ((IPersistFile)link).Save(path, true); }
        finally { Marshal.FinalReleaseComObject(link); }
        return path;
    }
}
