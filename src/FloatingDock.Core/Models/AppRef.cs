using System.Text.Json.Serialization;

namespace FloatingDock.Core.Models;

public enum AppSource { Unknown, AppsFolder, StartMenu, User, Window, WindowsTaskbar }

/// <summary>
/// A launchable application as known to FloatingDock: a pinned dock app, a Launchpad tile, or the identity of a running window group.
/// Only what is needed to launch it, name it and find its icon is stored.
/// </summary>
public sealed class AppRef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? ExePath { get; set; }
    public string? Aumid { get; set; }
    public string? ShortcutPath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>Shell parsing name inside the AppsFolder (used to launch/extract icons of entries that have neither exe nor AUMID).</summary>
    public string? ShellParsingName { get; set; }
    public List<string>? Aliases { get; set; }
    public AppSource Source { get; set; } = AppSource.Unknown;
    /// <summary>Persisted dock divider; never used as a launchable app or a Launchpad tile.</summary>
    public bool IsSeparator { get; set; }
    public static AppRef Separator() => new() { Name = "Separator", IsSeparator = true, Source = AppSource.User };

    /// <summary>Stable identity used for de-duplication and for animation continuity (pin / unpin of a running app).</summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (IsSeparator) return "separator:" + Id;
            if (!string.IsNullOrEmpty(Aumid) && Aumid.Contains('!')) return "aumid:" + Aumid.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ExePath) && !AppMatching.IsHostExe(ExePath)) return "exe:" + AppMatching.CanonicalExePath(ExePath);
            if (!string.IsNullOrEmpty(Aumid)) return "aumid:" + Aumid.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ExePath)) return "exe:" + AppMatching.NormalizePath(ExePath);
            if (!string.IsNullOrEmpty(ShortcutPath)) return "lnk:" + AppMatching.NormalizePath(ShortcutPath);
            if (!string.IsNullOrEmpty(ShellParsingName)) return "shell:" + ShellParsingName.ToLowerInvariant();
            return "id:" + Id;
        }
    }

    public AppRef Clone() => new()
    {
        Id = Id, Name = Name, ExePath = ExePath, Aumid = Aumid, ShortcutPath = ShortcutPath, Arguments = Arguments,
        WorkingDirectory = WorkingDirectory, ShellParsingName = ShellParsingName, Aliases = Aliases is null ? null : new List<string>(Aliases), Source = Source, IsSeparator = IsSeparator,
    };

    /// <summary>Names that may identify this app in an icon pack (exe name, AUMID parts, display name, shortcut name, aliases).</summary>
    public IEnumerable<string> IconNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Gen()
        {
            if (!string.IsNullOrWhiteSpace(ExePath)) yield return WinPath.FileNameWithoutExtension(ExePath);
            if (!string.IsNullOrWhiteSpace(ExePath)) yield return WinPath.FileName(ExePath);
            if (!string.IsNullOrWhiteSpace(Aumid))
            {
                yield return Aumid;
                var bang = Aumid.IndexOf('!');
                var pkg = bang >= 0 ? Aumid[..bang] : Aumid;
                var us = pkg.IndexOf('_');
                if (us > 0) pkg = pkg[..us];
                yield return pkg;
                var lastDot = pkg.LastIndexOf('.');
                if (lastDot > 0 && lastDot < pkg.Length - 1) yield return pkg[(lastDot + 1)..];
                if (bang >= 0 && bang < Aumid.Length - 1) yield return Aumid[(bang + 1)..];
            }
            if (!string.IsNullOrWhiteSpace(Name)) yield return Name;
            if (!string.IsNullOrWhiteSpace(ShortcutPath)) yield return WinPath.FileNameWithoutExtension(ShortcutPath);
            if (Aliases is not null) foreach (var a in Aliases) yield return a;
        }
        foreach (var n in Gen()) if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) yield return n;
    }
}
