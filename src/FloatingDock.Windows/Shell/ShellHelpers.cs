using System.Runtime.InteropServices;
using System.Text;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Shell;

public sealed record ShortcutInfo(string? Target, string? Arguments, string? WorkingDirectory, string? IconPath, int IconIndex, string? Aumid, string? Description);

/// <summary>Small helpers around the shell: shortcuts, known folders, property stores.</summary>
public static class ShellHelpers
{
    public static string? KnownFolder(Guid id)
    {
        var g = id;
        int hr = NativeMethods.SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out var p);
        try { return hr >= 0 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null; } finally { if (p != IntPtr.Zero) NativeMethods.CoTaskMemFree(p); }
    }

    /// <summary>"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Foo\bar.exe" (AppsFolder parsing name) to a real path.</summary>
    public static string? ResolveKnownFolderPrefix(string parsingName)
    {
        if (parsingName.Length > 38 && parsingName[0] == '{' && parsingName[37] == '}' && Guid.TryParse(parsingName.AsSpan(1, 36), out var g) && parsingName[38] == '\\')
        {
            var root = KnownFolder(g);
            if (root is not null) return Path.Combine(root, parsingName[39..]);
        }
        return null;
    }

    /// <summary>Reads the AppUserModel.ID property of a file (a .lnk of a Store app has one, its target is empty).</summary>
    public static string? ReadAumidOfFile(string path)
    {
        object? o = null;
        try
        {
            var iid = ShellGuids.IPropertyStore;
            NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out o);
            return ReadString((IPropertyStore)o, ShellGuids.PKEY_AppUserModel_ID);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return null; }
        finally { if (o is not null) Marshal.FinalReleaseComObject(o); }
    }

    public static string? ReadAumidOfWindow(IntPtr hwnd)
    {
        object? o = null;
        try
        {
            var iid = ShellGuids.IPropertyStore;
            NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out o);
            return ReadString((IPropertyStore)o, ShellGuids.PKEY_AppUserModel_ID);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return null; }
        finally { if (o is not null) Marshal.FinalReleaseComObject(o); }
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        int hr = store.GetValue(ref key, out var pv);
        try { if (hr < 0) return null; var s = pv.GetString(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        finally { NativeMethods.PropVariantClear(ref pv); }
    }

    /// <summary>Resolves a .lnk without UI and without searching for moved targets (fast, predictable).</summary>
    public static ShortcutInfo? ReadShortcut(string lnkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            var file = (IPersistFile)link;
            if (file.Load(lnkPath, 0) != 0) return null;
            link.Resolve(IntPtr.Zero, 0x1 | 0x8 | 0x10);               // SLR_NO_UI | SLR_NOUPDATE | SLR_NOSEARCH
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);
            var wd = new StringBuilder(1024);
            link.GetWorkingDirectory(wd, wd.Capacity);
            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out var idx);
            var desc = new StringBuilder(512);
            link.GetDescription(desc, desc.Capacity);
            var aumid = ShellHelpers.ReadAumidOfFile(lnkPath);
            return new ShortcutInfo(Nz(target), string.IsNullOrWhiteSpace(args.ToString()) ? null : args.ToString(), Nz(wd), Nz(icon), idx, aumid, desc.ToString());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return null; }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); }
    }

    private static string? Nz(StringBuilder sb)
    {
        var s = Environment.ExpandEnvironmentVariables(sb.ToString());
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static AppRef? AppFromShortcut(string lnkPath, AppSource source)
    {
        var info = ReadShortcut(lnkPath);
        if (info is null) return null;
        var exe = info.Target;
        if (exe is not null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(exe)) exe = null;
        // a shortcut that points to a folder / document is not an application
        if (exe is not null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (exe is null && info.Aumid is null) return null;
        return new AppRef
        {
            Name = Path.GetFileNameWithoutExtension(lnkPath),
            ExePath = exe,
            Aumid = info.Aumid is not null && (exe is null || info.Aumid.Contains('!')) ? info.Aumid : null,
            ShortcutPath = lnkPath,
            Arguments = info.Arguments,
            WorkingDirectory = info.WorkingDirectory,
            Source = source,
        };
    }

    public static string? ExeDisplayName(string exePath)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var n = string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.ProductName : vi.FileDescription;
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
