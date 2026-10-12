using System.Text.RegularExpressions;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Icons;

public static partial class PackagedIconIdentity
{
    [GeneratedRegex(@"^(?<name>.+)_(?<version>\d+\.\d+\.\d+\.\d+)_(?<arch>x64|x86|arm64|neutral)_[^_]*_(?<publisher>[^_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageFolder();

    /// <summary>Legacy WindowsApps overrides survive a package version change, only within the same family and relative executable.</summary>
    public static string? FromExeKey(string key)
    {
        if (!key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)) return null;
        string path = AppMatching.NormalizePath(key[4..]);
        const string marker = @"\windowsapps\";
        int at = path.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + marker.Length, end = path.IndexOf('\\', start);
        if (end < 0 || end == path.Length - 1) return null;
        var match = PackageFolder().Match(path[start..end]);
        return match.Success ? path[..start] + match.Groups["name"].Value + "_" + match.Groups["publisher"].Value + path[end..] : null;
    }
}
