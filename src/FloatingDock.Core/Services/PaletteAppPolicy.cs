using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

/// <summary>Palette-only filtering: explicit user apps remain searchable; Launchpad keeps its catalog.</summary>
public static class PaletteAppPolicy
{
    private static readonly string[] NoiseNames = ["uninstall", "деинсталл", "удалить", "видалити", "readme", "documentation", "release notes", "справка"];
    private static readonly string[] SystemIds = ["Microsoft.Windows.", "Microsoft.WindowsBackup", "MicrosoftWindows.", "Windows.", "Microsoft.Backup", "Microsoft.Copilot", "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.PowerAutomateDesktop"];
    public static bool Include(AppRef app)
    {
        if(app.IsSeparator)return false;
        if(app.Source==AppSource.User)return true;
        if(NoiseNames.Any(n=>app.Name.Contains(n,StringComparison.OrdinalIgnoreCase)))return false;
        if(app.Aumid is { } id&&(SystemIds.Any(p=>id.StartsWith(p,StringComparison.OrdinalIgnoreCase))||id.Contains("LiveCaptions",StringComparison.OrdinalIgnoreCase)||id.Contains("VoiceAccess",StringComparison.OrdinalIgnoreCase)))return false;
        if(app.ExePath is { } exe)
        {
            string path=exe.Replace('/','\\');
            if(path.Contains("\\Windows\\System32\\",StringComparison.OrdinalIgnoreCase)||path.Contains("\\Windows\\SysWOW64\\",StringComparison.OrdinalIgnoreCase)||path.Contains("\\Windows\\SystemApps\\",StringComparison.OrdinalIgnoreCase))return false;
        }
        return app.ShellParsingName is not { } shell||!shell.StartsWith("::{",StringComparison.Ordinal);
    }
}
