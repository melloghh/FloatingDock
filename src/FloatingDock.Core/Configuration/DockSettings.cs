namespace FloatingDock.Core.Configuration;

public enum DockEdge { Bottom, Left, Right }
public enum WidthMode { Auto, Fixed }
public enum DockAlignment { Center, Left, Right }
public enum MonitorMode { Primary, All }
public enum ThemeMode { Dark, Light, System }
public enum IndicatorStyle { Dot, Bar }
public enum InterfaceLanguage { English, Russian }
public enum LaunchpadIconStyle { WhiteSquares, ColorOnDark, ColorOnWhite }
public enum TilingModifier { Alt,CtrlAltWin }
public enum TaskbarMode
{
    /// <summary>Do not touch the Windows taskbar.</summary>
    Leave,
    /// <summary>Turn on the documented taskbar "automatically hide" state (ABM_SETSTATE). Original state is restored.</summary>
    AutoHide,
    /// <summary>Additionally hide the taskbar windows (including clock/tray). Experimental; always reversible, see docs.</summary>
    HideCompletely,
}

/// <summary>User configuration. Every property is wired to real behaviour; values are clamped by <see cref="Normalize"/>.</summary>
public sealed class DockSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ---- Dock ----
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    public int IconSize { get; set; } = 48;
    public int IconSpacing { get; set; } = 6;
    public int DockHeight { get; set; } = 64;
    public int BottomOffset { get; set; } = 10;
    public WidthMode WidthMode { get; set; } = WidthMode.Auto;
    public int FixedWidth { get; set; } = 900;
    public DockAlignment Alignment { get; set; } = DockAlignment.Center;
    public int HorizontalOffset { get; set; } = 0;
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Primary;

    // ---- Appearance ----
    public string BackgroundColor { get; set; } = "1E1E1E";
    public int Opacity { get; set; } = 55;
    public int CornerRadius { get; set; } = 22;
    public bool Shadow { get; set; } = true;
    public bool Border { get; set; } = true;
    public bool Blur { get; set; } = false;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public InterfaceLanguage Language { get; set; } = InterfaceLanguage.English;
    public bool MenuAnimations { get; set; } = true;
    public int MenuAnimationMs { get; set; } = 200;
    public bool ShowWindowPreviews { get; set; } = true;
    public int PreviewDelayMs { get; set; } = 750;
    public IndicatorStyle IndicatorStyle { get; set; } = IndicatorStyle.Dot;
    public bool ShowLabels { get; set; } = true;

    // ---- Magnification ----
    public bool MagnificationEnabled { get; set; } = true;
    /// <summary>Size of the icon directly under the cursor, in percent of the base size.</summary>
    public int MaxScalePercent { get; set; } = 170;
    /// <summary>Reach of the effect around the cursor, DIP.</summary>
    public int MagnificationRadius { get; set; } = 150;
    /// <summary>1..10: how tightly the zoom follows the cursor (10 = no lag).</summary>
    public int Responsiveness { get; set; } = 10;
    /// <summary>1..10: speed of zoom in/out when the cursor enters / leaves, and of auto-hide sliding.</summary>
    public int AnimationSpeed { get; set; } = 6;
    /// <summary>0..10: easing curve of zoom in/out (0 = linear, 10 = very soft start and stop).</summary>
    public int Smoothness { get; set; } = 5;
    public int AnimationFps { get; set; } = 60;
    public bool IconAnimation { get; set; } = true;
    public int IconAnimationMs { get; set; } = 240;

    // ---- Behaviour ----
    public bool ShowStartButton { get; set; } = true;
    public bool ShowLaunchpadButton { get; set; } = true;
    public bool ShowPinned { get; set; } = true;
    public bool ShowRunning { get; set; } = true;
    public bool ShowActiveIndicator { get; set; } = true;
    public bool AutoHide { get; set; } = false;
    public bool RevealOnEdgeHover { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    /// <summary>Maximized and full-screen windows end above the dock instead of covering it, and the dock stays visible.</summary>
    public bool KeepDockInFullscreen { get; set; } = false;
    public bool MinimizeOnRepeatClick { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    // ---- Menu bar ----
    public bool TopBarEnabled { get; set; } = true;
    public bool TopBarAutoHide { get; set; } = false;
    public bool TopBarReserveSpace { get; set; } = true;
    public bool TopBarShowDate { get; set; } = false;
    public bool TopBarShowSeconds { get; set; } = false;
    public int TopBarAnimationFps { get; set; } = 60;
    public int TopBarHideMs { get; set; } = 220;
    public int TopBarShowMs { get; set; } = 170;
    public int TopBarHideDelayMs { get; set; } = 700;
    public bool TopBarMediaEnabled { get; set; } = true;
    public bool TopBarTrayEnabled { get; set; } = true;
    public bool TopBarSearchEnabled { get; set; } = true;

    // ---- Icon packs ----
    public bool UseIconPack { get; set; } = true;
    public string ActiveIconPack { get; set; } = "Default";
    public bool PreferDarkVariants { get; set; } = true;

    // ---- Launchpad ----
    public int LaunchpadIconSize { get; set; } = 72;
    public int LaunchpadSpacing { get; set; } = 28;
    public string LaunchpadHotkey { get; set; } = "Ctrl+Alt+Space";
    public LaunchpadIconStyle LaunchpadIconStyle { get; set; } = LaunchpadIconStyle.WhiteSquares;
    public string SearchHotkey { get; set; } = "Ctrl+Alt+K";
    public bool SearchFiles { get; set; } = true;
    public string SearchFolders { get; set; } = "Desktop;Downloads";
    public int SearchDepth { get; set; } = 1;
    public int SearchResultLimit { get; set; } = 30;
    // ---- Built-in, opt-in tiling ----
    public bool TilingEnabled { get; set; }
    public bool TilingShowWorkspaces { get; set; }=true;
    public int TilingWorkspaces { get; set; } = 5;
    public int TilingGap { get; set; } = 8;
    public int TilingOuterGap { get; set; } = 12;
    public int TilingAnimationMs { get; set; } = 220;
    public int TilingAnimationFps { get; set; } = 60;
    public int TilingStartupDelayMs { get; set; } = 180;
    public bool TilingHideDock { get; set; } = true;
    public bool TilingBringActivatedWindows { get; set; } = true;
    public bool TilingTileMaximizedApps { get; set; } = true;
    public bool TilingBorders { get; set; }
    public string TilingActiveBorderColor { get; set; } = "A8C7FA";
    public string TilingInactiveBorderColor { get; set; } = "414956";
    public Dictionary<string,string> TilingKeybindings { get; set; } = new(StringComparer.Ordinal);
    public TilingModifier TilingModifier { get; set; } = TilingModifier.Alt;
    public string TilingExcludedApps { get; set; } = "FloatingDock.exe;Taskmgr.exe;SnippingTool.exe;ScreenClippingHost.exe";

    // ---- Windows taskbar ----
    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.HideCompletely;
    public bool ReserveWorkArea { get; set; } = false;
    public string RestoreTaskbarHotkey { get; set; } = "Ctrl+Alt+Shift+T";
    /// <summary>Moves keyboard focus into the Dock (arrows, Enter, Esc).</summary>
    public string DockFocusHotkey { get; set; } = "Ctrl+Alt+D";

    public DockSettings Clone()
    {var copy=(DockSettings)MemberwiseClone();copy.TilingKeybindings=new(TilingKeybindings??new(),StringComparer.Ordinal);return copy;}

    /// <summary>Clamps everything into sane ranges (hand-edited JSON can contain anything).</summary>
    public DockSettings Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        if (!Enum.IsDefined(Edge)) Edge = DockEdge.Bottom;
        if (!Enum.IsDefined(WidthMode)) WidthMode = WidthMode.Auto;
        if (!Enum.IsDefined(Alignment)) Alignment = DockAlignment.Center;
        if (!Enum.IsDefined(MonitorMode)) MonitorMode = MonitorMode.Primary;
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.Dark;
        if (!Enum.IsDefined(IndicatorStyle)) IndicatorStyle = IndicatorStyle.Dot;
        if (!Enum.IsDefined(TaskbarMode)) TaskbarMode = TaskbarMode.Leave;
        if (!Enum.IsDefined(Language)) Language=InterfaceLanguage.English;
        if (!Enum.IsDefined(LaunchpadIconStyle)) LaunchpadIconStyle=LaunchpadIconStyle.WhiteSquares;
        TopBarAnimationFps=Math.Clamp(TopBarAnimationFps,30,144);
        TopBarHideMs=Math.Clamp(TopBarHideMs,80,800);TopBarShowMs=Math.Clamp(TopBarShowMs,80,800);
        TopBarHideDelayMs=Math.Clamp(TopBarHideDelayMs,100,3000);
        MenuAnimationMs=Math.Clamp(MenuAnimationMs,80,600);PreviewDelayMs=Math.Clamp(PreviewDelayMs,200,2000);
        SearchResultLimit=Math.Clamp(SearchResultLimit,5,100);SearchFolders??="";SearchHotkey??="";
        SearchDepth=Math.Clamp(SearchDepth,0,3);
        TilingWorkspaces=Math.Clamp(TilingWorkspaces,1,9);TilingGap=Math.Clamp(TilingGap,0,40);TilingOuterGap=Math.Clamp(TilingOuterGap,0,80);
        if(!Enum.IsDefined(TilingModifier))TilingModifier=TilingModifier.Alt;TilingExcludedApps??="";
        TilingAnimationMs=Math.Clamp(TilingAnimationMs,0,600);TilingAnimationFps=Math.Clamp(TilingAnimationFps,30,144);TilingStartupDelayMs=Math.Clamp(TilingStartupDelayMs,0,500);
        TilingKeybindings=TilingKeybindings is null?new():TilingKeybindings.Where(p=>p.Key.Length<=64).ToDictionary(p=>p.Key,p=>(p.Value??"").Trim(),StringComparer.Ordinal);
        if(!ColorUtil.TryParseRgb(TilingActiveBorderColor,out _))TilingActiveBorderColor="A8C7FA";
        if(!ColorUtil.TryParseRgb(TilingInactiveBorderColor,out _))TilingInactiveBorderColor="414956";
        SearchFolders=string.Join(';',SearchFolders.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Where(t=>!t.Equals("Documents",StringComparison.OrdinalIgnoreCase)));
        IconSize = Math.Clamp(IconSize, 24, 128);
        IconSpacing = Math.Clamp(IconSpacing, 0, 40);
        DockHeight = Math.Clamp(DockHeight, IconSize + 4, 200);
        BottomOffset = Math.Clamp(BottomOffset, 0, 200);
        FixedWidth = Math.Clamp(FixedWidth, 200, 7680);
        HorizontalOffset = Math.Clamp(HorizontalOffset, -4000, 4000);
        Opacity = Math.Clamp(Opacity, 0, 100);
        CornerRadius = Math.Clamp(CornerRadius, 0, 60);
        if (!ColorUtil.TryParseRgb(BackgroundColor, out _)) BackgroundColor = "1E1E1E";
        MaxScalePercent = Math.Clamp(MaxScalePercent, 100, 300);
        MagnificationRadius = Math.Clamp(MagnificationRadius, 40, 500);
        Responsiveness = Math.Clamp(Responsiveness, 1, 10);
        AnimationSpeed = Math.Clamp(AnimationSpeed, 1, 10);
        Smoothness = Math.Clamp(Smoothness, 0, 10);
        AnimationFps = Math.Clamp(AnimationFps, 30, 144);
        IconAnimationMs = Math.Clamp(IconAnimationMs, 80, 800);
        LaunchpadIconSize = Math.Clamp(LaunchpadIconSize, 32, 160);
        LaunchpadSpacing = Math.Clamp(LaunchpadSpacing, 4, 120);
        ActiveIconPack = string.IsNullOrWhiteSpace(ActiveIconPack) ? "Default" : ActiveIconPack;
        LaunchpadHotkey ??= "";
        RestoreTaskbarHotkey ??= "";
        DockFocusHotkey ??= "";
        return this;
    }
}

public static class ColorUtil
{
    public static bool TryParseRgb(string? text, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        rgb = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
