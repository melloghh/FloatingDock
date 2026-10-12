using FloatingDock.Core.Models;

namespace FloatingDock.UI.Dock;

/// <summary>Pseudo apps for the built-in Start / Launchpad buttons: they use the same icon-pack matching (by name and aliases) as real apps.</summary>
internal static class SpecialApps
{
    public static readonly AppRef Start = new()
    {
        Id = "special-start", Name = "Start",
        Aliases = new() { "Windows", "Start Menu", "Windows Start", "Пуск" },
    };

    public static readonly AppRef Launchpad = new()
    {
        Id = "special-launchpad", Name = "Launchpad",
        Aliases = new() { "Apps", "App Library", "All Apps", "Launcher", "Приложения" },
    };
}
