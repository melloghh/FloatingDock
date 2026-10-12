namespace FloatingDock.Core.Configuration;

/// <summary>All user-data locations. Everything lives under %LOCALAPPDATA%\FloatingDock and never inside the install folder.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public static AppPaths Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.Combine(Path.GetTempPath(), "FloatingDockData");
        return new AppPaths(Path.Combine(local, "FloatingDock"));
    }

    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string PinnedFile => Path.Combine(Root, "pinned.json");
    public string IconOverridesFile => Path.Combine(Root, "icon-overrides.json");
    public string TaskbarStateFile => Path.Combine(Root, "taskbar-state.json");
    public string AppLibraryDir => Path.Combine(Root, "AppLibrary");
    public string AppLibraryFile => Path.Combine(AppLibraryDir, "library.json");
    public string AppLibraryShortcutsDir => Path.Combine(AppLibraryDir, "Shortcuts");
    public string IconPacksDir => Path.Combine(Root, "IconPacks");
    public string CustomIconsDir => Path.Combine(IconPacksDir, "Custom");
    public string UserIconsDir => Path.Combine(Root, "UserIcons");
    public string CacheDir => Path.Combine(Root, "Cache");
    public string SystemIconCacheDir => Path.Combine(CacheDir, "SystemIcons");
    public string LogsDir => Path.Combine(Root, "Logs");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, AppLibraryDir, AppLibraryShortcutsDir, IconPacksDir, CustomIconsDir, UserIconsDir, CacheDir, SystemIconCacheDir, LogsDir })
            Directory.CreateDirectory(d);
    }
}
