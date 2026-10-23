using FloatingDock.Core.Models;
using FloatingDock.Core.Services;

namespace FloatingDock.Windows.Shell;

public enum TileImportStatus { Added, Duplicate, Unsupported, SaveFailed }
public sealed record TileImportResult(TileImportStatus Status, AppRef? App, string? Message);

/// <summary>
/// Turns a dropped file (a .lnk from the Desktop / Explorer, or an .exe) into a persistent Launchpad tile. Nothing is ever started.
/// A shortcut is copied into FloatingDock's own folder, so deleting or moving the original does not destroy the tile.
/// Runs on the <see cref="ShellWorker"/> thread.
/// </summary>
public static class UserTileImporter
{
    /// <summary>A copied shortcut is kept only after the corresponding app list was saved.</summary>
    public static TileImportResult ImportAndSave(string path, string shortcutsDir, AppListStore store)
    {
        var result = Import(path, shortcutsDir, store.Contains);
        if (result.Status != TileImportStatus.Added || result.App is null) return result;
        if (store.Add(result.App)) return result;
        if (result.App.ShortcutPath is { } copy) TryDelete(copy);
        return store.Contains(result.App.Key)
            ? new(TileImportStatus.Duplicate, null, "Already added.")
            : new(TileImportStatus.SaveFailed, null, store.LastError ?? "Could not add application.");
    }

    public static TileImportResult Import(string path, string shortcutsDir, Func<string, bool> alreadyExists)
    {
        try
        {
            if (!File.Exists(path)) return new(TileImportStatus.Unsupported, null, "The file does not exist.");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lnk")
            {
                Directory.CreateDirectory(shortcutsDir);
                var copy = Path.Combine(shortcutsDir, Guid.NewGuid().ToString("N") + ".lnk");
                File.Copy(path, copy, overwrite: false);
                var app = ShellHelpers.AppFromShortcut(copy, AppSource.User);
                if (app is null) { TryDelete(copy); return new(TileImportStatus.Unsupported, null, "This shortcut does not point to an application."); }
                app.Name = Path.GetFileNameWithoutExtension(path);
                if (alreadyExists(app.Key)) { TryDelete(copy); return new(TileImportStatus.Duplicate, null, $"'{app.Name}' is already in the Launchpad."); }
                return new(TileImportStatus.Added, app, null);
            }
            if (ext == ".exe")
            {
                var app = new AppRef { Name = ShellHelpers.ExeDisplayName(path) ?? Path.GetFileNameWithoutExtension(path), ExePath = path, WorkingDirectory = Path.GetDirectoryName(path), Source = AppSource.User };
                if (alreadyExists(app.Key)) return new(TileImportStatus.Duplicate, null, $"'{app.Name}' is already in the Launchpad.");
                return new(TileImportStatus.Added, app, null);
            }
            return new(TileImportStatus.Unsupported, null, "Only shortcuts (.lnk) and programs (.exe) can be added.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(TileImportStatus.Unsupported, null, ex.Message);
        }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
