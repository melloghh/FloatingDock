using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Shell;

/// <summary>
/// Enumerates the installed applications from the sources Windows itself uses for "All apps": the AppsFolder (Win32 + packaged apps),
/// and the user / common Start Menu shortcuts. Runs on the <see cref="ShellWorker"/> thread; the result is de-duplicated by app identity.
/// </summary>
public static class AppCatalog
{
    private static readonly string[] SkipExtensions = { ".chm", ".txt", ".html", ".htm", ".url", ".pdf", ".rtf", ".doc", ".docx", ".xml", ".ini", ".log", ".md", ".mht" };
    private static readonly string[] SkipNameParts = { "uninstall", "удалить", "видалити", "readme", "release notes", "documentation", "license", "help", "справка", "manual" };

    /// <summary>Apps plus the problems met while enumerating (a failing source never aborts the other one).</summary>
    public sealed record CatalogResult(List<AppRef> Apps, IReadOnlyList<string> Warnings);

    /// <summary>Must run on an STA thread (the <see cref="ShellWorker"/>); throws <see cref="InvalidOperationException"/> otherwise. Honours <paramref name="ct"/> between items.</summary>
    public static List<AppRef> Load(CancellationToken ct = default) => LoadDetailed(ct).Apps;

    public static CatalogResult LoadDetailed(CancellationToken ct = default)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("AppCatalog must run on an STA thread (use ShellWorker).");
        var warnings = new List<string>();
        var all = new List<AppRef>();
        all.AddRange(SafeEnumerateAppsFolder(warnings, ct));
        ct.ThrowIfCancellationRequested();
        var names = new HashSet<string>(all.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        all.AddRange(EnumerateStartMenu(names, ct));

        var seen = new HashSet<string>();
        var result = new List<AppRef>();
        foreach (var a in all.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(a.Name) || IsNoise(a)) continue;
            if (!seen.Add(a.Key)) continue;
            result.Add(a);
        }
        return new CatalogResult(result, warnings);
    }

    private static bool IsNoise(AppRef a)
    {
        var lower = a.Name.ToLowerInvariant();
        foreach (var part in SkipNameParts) if (lower.Contains(part) && a.Source != AppSource.User) return true;
        if (!string.IsNullOrEmpty(a.ExePath))
        {
            var ext = Path.GetExtension(a.ExePath).ToLowerInvariant();
            if (SkipExtensions.Contains(ext)) return true;
        }
        return false;
    }

    private static List<AppRef> SafeEnumerateAppsFolder(List<string> warnings, CancellationToken ct)
    {
        var list = new List<AppRef>();
        try { EnumerateAppsFolder(list, warnings, ct); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException) { warnings.Add("AppsFolder: " + ex.Message); }
        return list;
    }

    /// <summary>Every COM object obtained here is released on every path (success, failed bind, exception, cancellation).</summary>
    private static void EnumerateAppsFolder(List<AppRef> into, List<string> warnings, CancellationToken ct)
    {
        var folderId = ShellGuids.FOLDERID_AppsFolder;
        var iidItem = ShellGuids.IShellItem;
        object? folderObj = null, enumObj = null;
        try
        {
            NativeMethods.SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out folderObj);
            var folder = (IShellItem)folderObj;
            var bhid = ShellGuids.BHID_EnumItems;
            var iidEnum = ShellGuids.IEnumShellItems;
            int hr = folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumObj);
            if (hr != 0 || enumObj is null) { warnings.Add($"AppsFolder: BindToHandler failed (0x{hr:X8})"); return; }
            var en = (IEnumShellItems)enumObj;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int nr = en.Next(1, out var item, out var fetched);
                if (nr != 0 || fetched != 1 || item is null)
                {
                    if (item is not null) Marshal.FinalReleaseComObject(item);
                    if (nr < 0) warnings.Add($"AppsFolder: enumeration stopped (0x{nr:X8})");       // S_FALSE (1) = end of list
                    break;
                }
                try { AddItem(item, into); }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { warnings.Add("AppsFolder item skipped: " + ex.Message); }
                finally { Marshal.FinalReleaseComObject(item); }
            }
        }
        finally
        {
            if (enumObj is not null) Marshal.FinalReleaseComObject(enumObj);
            if (folderObj is not null) Marshal.FinalReleaseComObject(folderObj);
        }
    }

    private static void AddItem(IShellItem item, List<AppRef> into)
    {
        var name = DisplayName(item, 0x0);                         // SIGDN_NORMALDISPLAY
        var parsing = DisplayName(item, 0x80028000);               // SIGDN_DESKTOPABSOLUTEPARSING
        if (name is null || parsing is null) return;
        var app = new AppRef { Name = name, Source = AppSource.AppsFolder, ShellParsingName = parsing };
        if (parsing.Contains('!') || (!parsing.Contains('\\') && !parsing.Contains('/') && !parsing.Contains(':')))
            app.Aumid = parsing;                                    // packaged app or explicit AUMID
        else
        {
            var path = ShellHelpers.ResolveKnownFolderPrefix(parsing) ?? (Path.IsPathRooted(parsing) ? parsing : null);
            if (path is not null)
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var fromLnk = ShellHelpers.AppFromShortcut(path, AppSource.AppsFolder);
                    if (fromLnk is null) return;
                    fromLnk.Name = name; fromLnk.ShellParsingName = parsing;
                    into.Add(fromLnk);
                    return;
                }
                app.ExePath = path;
            }
        }
        into.Add(app);
    }

    private static string? DisplayName(IShellItem item, uint sigdn)
    {
        int hr = item.GetDisplayName(sigdn, out var p);
        try { return hr >= 0 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null; } finally { if (p != IntPtr.Zero) NativeMethods.CoTaskMemFree(p); }
    }

    private static IEnumerable<AppRef> EnumerateStartMenu(HashSet<string> knownNames, CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var lnk in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (knownNames.Contains(name)) continue;                          // AppsFolder already lists it (and resolves it more reliably)
                var app = ShellHelpers.AppFromShortcut(lnk, AppSource.StartMenu);
                if (app is not null) yield return app;
            }
        }
    }

    /// <summary>Shortcuts that Windows itself keeps for the taskbar, oldest first (Windows does not expose the real order in a stable way).</summary>
    public static List<AppRef> ReadWindowsTaskbarPins()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        var result = new List<AppRef>();
        if (!Directory.Exists(dir)) return result;
        foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk").OrderBy(f => File.GetCreationTimeUtc(f)))
        {
            var app = ShellHelpers.AppFromShortcut(lnk, AppSource.WindowsTaskbar);
            if (app is not null) { app.Source = AppSource.WindowsTaskbar; result.Add(app); }
        }
        return result;
    }
}
