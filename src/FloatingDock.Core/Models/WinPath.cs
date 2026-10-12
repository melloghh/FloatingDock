namespace FloatingDock.Core.Models;

/// <summary>Windows-style path helpers that behave identically on every OS (paths coming from Windows APIs always use '\\' or '/').</summary>
public static class WinPath
{
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? path[(i + 1)..] : path;
    }

    public static string Extension(string? path)
    {
        var f = FileName(path);
        var i = f.LastIndexOf('.');
        return i > 0 ? f[i..] : "";
    }

    public static string FileNameWithoutExtension(string? path)
    {
        var f = FileName(path);
        var i = f.LastIndexOf('.');
        return i > 0 ? f[..i] : f;
    }

    public static string? Directory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i > 0 ? path[..i] : null;
    }
}
