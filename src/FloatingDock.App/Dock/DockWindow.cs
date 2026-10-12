using System.Diagnostics;
using System.Numerics;
using FloatingDock.Windows.Native;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;
using Windows.UI.Composition;
using global::Windows.UI;

namespace FloatingDock.UI.Dock;

/// <summary>
/// One borderless, transparent, always-on-top, non-activating window that draws the dock on one monitor.
/// Icons, plate and cached labels are drawn directly by the Windows system compositor; Win32 delivers input directly to the same native window.
/// A bounded frame scheduler sleeps between updates and stops when hover and transitions end.
/// </summary>
internal sealed class DockWindow : IDisposable
{
    private sealed class ItemVisual
    {
        public CompositionIcon? Icon;
        public required Visual HostVisual;
        public Visual? IndicatorVisual;
        public float IndicatorWidth, IndicatorHeight;
        public Vector3 LastOffset = new(float.NaN), LastScale = new(float.NaN);
        public float LastOpacity = float.NaN;
        public Vector3 LastIndicatorOffset = new(float.NaN), LastIndicatorScale = new(float.NaN);
        public float LastIndicatorOpacity = float.NaN;
    }

    private const int HoverDragThreshold = 6;

    private readonly DockApp _app;
    private readonly bool _traceInput = Environment.GetCommandLineArgs().Contains("--diagnostic-input");
    private readonly DockViewModel _vm;
    private readonly NativeDockWindow _window;
    private readonly AlphaWindow _alpha;
    private readonly IntPtr _hwnd;


    private readonly SystemDockGraphics _graphics;
    private readonly ContainerVisual _itemsVisual;
    private readonly CompositionDockPlate _compositionPlate;


    private readonly CompositionLabel _compositionLabel;

    private readonly Dictionary<string, ItemVisual> _visuals = new();
    private readonly DockMotion _motion = new();
    private readonly MagnificationState _mag = new();
    private readonly FrameScheduler _frameClock;
    private readonly DockLayoutResult _layoutResult = new();

    private MonitorDescriptor _monitor;
    private DockSettings _s;
    private GeometryParams _gp;
    private WindowMetrics _m;
    private (int, int) _shape;
    private int _winX, _winY, _winW, _winH;
    private double _scale = 1.0;

    private bool _looping;
    private long _lastTicks;
    private float _frameDelta = 1f / 60f;
    private bool _renderNeeded = true;
    private (float X, float Y)? _lastRenderCursor;
    private IReadOnlyList<DockEntry>? _deferredEntries;
    private bool _disposed;

    // visibility
    private bool _fsHidden, _userHidden, _suspended, _captureHidden, _concealForFullscreen;
    private bool _strip;                    // auto-hide: window is just a thin edge strip
    private float _hide;                    // 0 shown .. 1 hidden
    private float _idleFor;
    private bool _regionFull = true;
    private bool _shown;

    // interaction
    private int _hoverIndex = -1;
    private readonly DockWindowPreview _preview=new();
    internal DockWindowPreview PreviewForDiagnostics=>_preview;
    private int _pressIndex = -1;
    private global::Windows.Foundation.Point _pressPos;
    private bool _dragging;
    private string? _dragKey;
    private bool _menuOpen;
    private bool _kbMode;
    private int _kbIndex = -1;
    private IntPtr _prevForeground;
    private float _labelAlpha;
    private bool _diagnosticHover;
    private bool _diagnosticStationary;
    private float? _diagnosticCursorX;
    private readonly List<double> _diagnosticFrameCosts = new();
    private readonly List<double> _diagnosticFrameIntervals = new();
    private double _diagnosticElapsed;
    private int _diagnosticLayouts;
    private TimeSpan _diagnosticCpu;
    private long _diagnosticStarted;
    private long _diagnosticCpuSampleStarted;
    private TimeSpan _diagnosticCpuSample;
    private readonly List<double> _diagnosticCpuSamples = new();
    internal bool IgnoreRealCursorForDiagnostics { get; set; }

    public DockWindow(MonitorDescriptor monitor, DockViewModel vm)
    {
        _app = DockApp.Current;
        _frameClock = new FrameScheduler(action => _app.UiQueue.TryEnqueue(() => action()), OnRendering);
        _vm = vm;
        _monitor = monitor;
        _s = _app.Settings.Current;
        _gp = GeometryParams.From(_s);
        if (_s.Edge != DockEdge.Bottom && _s.ShowLabels) _gp = _gp with { LabelHeadroom = 260 };
        _m = WindowMetrics.Compute(_gp, 1, 0);

        _window = new NativeDockWindow();
        _hwnd = _window.Handle;
        _alpha = new AlphaWindow(_hwnd);

        _graphics = new SystemDockGraphics(_hwnd);
        _compositionPlate = new CompositionDockPlate(_graphics);
        _graphics.Content.Children.InsertAtTop(_compositionPlate.Visual);

        _itemsVisual = _graphics.Compositor.CreateContainerVisual();
        _graphics.Content.Children.InsertAtTop(_itemsVisual);

        _compositionLabel = new CompositionLabel(_graphics, Invalidate);
        _itemsVisual.Children.InsertAtTop(_compositionLabel.Visual);
        _window.Pointer += OnNativePointer;
        _window.KeyDown += OnKeyDown;
        _window.Activated += active => { if (!active && _kbMode && !_menuOpen) ExitKeyboardMode(false); };
        _window.InputFailed += ex => _app.Log.Error("dock input failed", ex);
        _window.CloseRequested += () => _app.UiQueue.TryEnqueue(_app.Shutdown);

        _motion.SetEntries(_vm.Entries, animate: false);
        _vm.EntriesChanged += OnEntriesChanged;
        ApplySettings();
    }

    public IntPtr Handle => _hwnd;
    public MonitorDescriptor Monitor => _monitor;
    public bool IsVisible => _shown && !_strip && !_userHidden && !_suspended && !_captureHidden;
    internal float HideAmount => _hide;
    public (int X, int Y, int W, int H) Bounds => (_winX, _winY, _winW, _winH);
    internal bool IsAnimating => _looping;
    internal bool IsContextMenuOpen => _menuOpen;
    internal bool IsHovering => _mag.Hovering;
    internal int PlateRasterizations => _compositionPlate.Rasterizations;
    internal FloatingDock.Windows.Native.RECT? AnimationRectFor(IntPtr hwnd)
    {
        var root = FloatingDock.Windows.Native.NativeMethods.GetAncestor(hwnd, FloatingDock.Windows.Native.NativeMethods.GA_ROOTOWNER);
        long target = (root == IntPtr.Zero ? hwnd : root).ToInt64();
        var item = _motion.Items.FirstOrDefault(i => !i.Leaving && i.Presence > .1f &&
            i.Entry.Kind == DockEntryKind.App && i.Entry.Windows.Any(w => w.Hwnd == target && WindowActivator.IsLive(w)));
        return item is null ? null : AnimationRectFor(item);
    }

    private FloatingDock.Windows.Native.RECT? AnimationRectFor(MotionItem item)
    {
        if (item.Size <= 0 || !float.IsFinite(item.X) || !float.IsFinite(item.Top)) return null;
        float inset = item.Size * .05f;
        return ScreenRect(new Rect(item.X + inset, item.Top + inset, item.Size - 2 * inset, item.Size - 2 * inset));
    }
    private DockAxis Axis => new(_s.Edge, _m.WindowHeight + ExtraBottomDip);
    private bool Vertical => _s.Edge != DockEdge.Bottom;
    private Vector3 IconOffset(float x, float y, float size)
    {
        var rect = Axis.ToPhysical(new Rect(x,y,size,size)); return new Vector3(rect.X,rect.Y,0);
    }
    private RECT ScreenRect(Rect logical)
    {
        var rect = Axis.ToPhysical(logical);
        var slide = !_strip && _shown ? _graphics.Content.Offset : Vector3.Zero;
        return new RECT { Left=(int)Math.Floor(_winX+(rect.X+slide.X)*_scale), Top=(int)Math.Floor(_winY+(rect.Y+slide.Y)*_scale), Right=(int)Math.Ceiling(_winX+(rect.Right+slide.X)*_scale), Bottom=(int)Math.Ceiling(_winY+(rect.Bottom+slide.Y)*_scale) };
    }
    internal RECT PlateScreenForDiagnostics => ScreenRect(new Rect(_m.RestPlateLeft,_m.PlateTop,_m.RestPlateRight-_m.RestPlateLeft,_s.DockHeight));
    internal int PlateTopScreen => _winY + (int)(_m.PlateTop * _scale);
    internal void ShowContextMenuForSmoke() => ShowContextMenu(_motion.Items.FirstOrDefault(i => i.Entry.Kind == DockEntryKind.App), _winX + _winW / 2, Vertical ? (PlateScreenForDiagnostics.Top+PlateScreenForDiagnostics.Bottom)/2 : _winY + _winH);

    internal void BeginRenderDiagnostics(bool stationary = false, float? cursorX = null)
    {
        _diagnosticHover = true;
        _diagnosticStationary = stationary;
        _diagnosticCursorX = cursorX;
        _diagnosticElapsed = 0;
        _diagnosticLayouts = 0;
        _diagnosticCpu = Process.GetCurrentProcess().TotalProcessorTime;
        _diagnosticStarted = Stopwatch.GetTimestamp();
        _diagnosticCpuSampleStarted = _diagnosticStarted; _diagnosticCpuSample = _diagnosticCpu;
        _diagnosticCpuSamples.Clear();
        _diagnosticFrameCosts.Clear(); _diagnosticFrameIntervals.Clear();
        BeginHover((_m.RestPlateLeft + _m.RestPlateRight) / 2);
        UpdateRegion(true);
    }

    internal sealed record RenderMetrics(int Frames, double MeanTickMs, double P95TickMs, double MeanFrameIntervalMs, int LayoutPasses, double CpuPercentOfOneCore, double CpuPercentOfMachine, double DurationMs, double PeakOneSecondCpuPercentOfMachine, int Items, int LabelRasterizations);

    internal RenderMetrics EndRenderDiagnostics()
    {
        _diagnosticHover = false;
        _diagnosticCursorX = null;
        EndHover();
        var costs = _diagnosticFrameCosts.Order().ToArray();
        double duration = Stopwatch.GetElapsedTime(_diagnosticStarted).TotalMilliseconds;
        double cpu = duration <= 0 ? 0 : (Process.GetCurrentProcess().TotalProcessorTime - _diagnosticCpu).TotalMilliseconds / duration * 100;
        return new(costs.Length, costs.Length == 0 ? 0 : costs.Average(), costs.Length == 0 ? 0 : costs[(int)((costs.Length - 1) * .95)], _diagnosticFrameIntervals.Count == 0 ? 0 : _diagnosticFrameIntervals.Average(), _diagnosticLayouts, cpu, cpu / Environment.ProcessorCount, duration, _diagnosticCpuSamples.Count == 0 ? 0 : _diagnosticCpuSamples.Max(), _motion.Items.Count, _compositionLabel.Rasterizations);
    }

    // ------------------------------------------------------------------ settings & geometry

    public void SetMonitor(MonitorDescriptor m)
    {
        _monitor = m;
        Reposition();
        Invalidate();
    }

    public void ApplySettings()
    {
        _preview.Hide();
        _s = _app.Settings.Current;
        _gp = GeometryParams.From(_s);
        if (_s.Edge != DockEdge.Bottom && _s.ShowLabels) _gp = _gp with { LabelHeadroom = 260 };
        RecomputeMetrics(force: true);
        _frameClock.SetFrameRate(_s.AnimationFps);

        _compositionPlate.SetStyle(_s);


        foreach (var v in _visuals.Values)
        {
            if (v.Icon is not null) v.Icon.SetSize(_s.IconSize);
            else
            {
                _itemsVisual.Children.Remove(v.HostVisual); v.HostVisual.Dispose();
                v.HostVisual = CreateMark(Vertical ? _s.IconSize * .55f : 1.5f, Vertical ? 1.5f : _s.IconSize * .55f, .75f, Color.FromArgb(90,255,255,255));
                _itemsVisual.Children.InsertAtTop(v.HostVisual);v.LastOffset=new(float.NaN);v.LastScale=new(float.NaN);
            }
            RebuildIndicator(v);
        }
        SyncVisuals();
        ApplyHideOffset();
        Reposition();
        UpdateVisibility();
        Invalidate();
    }

    private void RecomputeMetrics(bool force)
    {
        var shape = _motion.MetricsShape();
        if (!force && shape == _shape) return;
        _shape = shape;
        _m = WindowMetrics.Compute(_gp, Math.Max(1, shape.Count), shape.Separators);


        SetViewport();
        _itemsVisual.Size = Vertical ? new Vector2(Axis.CrossExtent,_m.WindowWidth) : new Vector2(_m.WindowWidth,Axis.CrossExtent);
        _compositionPlate.SetViewport(_m.WindowWidth, _m.WindowHeight);
        if (!force) Reposition();
    }

    private bool EffectiveAutoHide => _s.AutoHide || _fsHidden;
    private float ExtraBottomDip => Math.Max(0, _s.BottomOffset - _m.ShadowMargin + 2);

    /// <summary>Y (px) of the line the dock sits on: the work-area bottom (above the taskbar) or the monitor bottom.</summary>
    private int BaseEdge()
    {
        bool aboveTaskbar = !_fsHidden && _s.TaskbarMode == TaskbarMode.Leave && !_s.ReserveWorkArea;
        var bounds = aboveTaskbar ? _monitor.WorkArea : _monitor.Bounds;
        return _s.Edge switch { DockEdge.Left => bounds.Left, DockEdge.Right => bounds.Right, _ => bounds.Bottom };
    }

    private void Reposition()
    {
        if (_disposed) return;
        _scale = _monitor.Scale;
        SetViewport();
        var viewport = Axis.ToPhysical(new Rect(0,0,_m.WindowWidth,Axis.CrossExtent));
        _winW = (int)Math.Ceiling(viewport.Width * _scale);
        _winH = (int)Math.Ceiling(viewport.Height * _scale);
        float anchorLeft = _m.RestPlateLeft, anchorRight = _m.RestPlateRight;
        var b = _monitor.Bounds;
        // AppBar/taskbar reservations can change while a panel hides or reveals.
        // Anchor along the physical monitor edge so a side dock only slides on X.
        // BaseEdge still respects the taskbar on the perpendicular axis.
        int start = Vertical ? b.Top : b.Left, end = Vertical ? b.Bottom : b.Right;
        double along = _s.Alignment switch
        {
            DockAlignment.Left when !_gp.FixedWidth => start + (12 + _s.HorizontalOffset - anchorLeft) * _scale,
            DockAlignment.Right when !_gp.FixedWidth => end + (-12 + _s.HorizontalOffset - anchorRight) * _scale,
            _ => (start + end) / 2.0 + (_s.HorizontalOffset - (anchorLeft + anchorRight) / 2.0) * _scale,
        };
        var plate = Axis.ToPhysical(new Rect(anchorLeft,_m.PlateTop,anchorRight-anchorLeft,_s.DockHeight));
        if (!Vertical)
        {
            _winX=(int)Math.Round(along);
            _winY=BaseEdge()-(int)Math.Round((_s.BottomOffset+_m.PlateBottom)*_scale);
        }
        else
        {
            _winY=(int)Math.Round(along);
            _winX=_s.Edge==DockEdge.Left ? BaseEdge()+(int)Math.Round((_s.BottomOffset-plate.X)*_scale) : BaseEdge()-(int)Math.Round((_s.BottomOffset+plate.Right)*_scale);
        }
        SetWindowBounds();

        ApplyBlur();
        _regionApplied = false;
        if (!_strip) UpdateRegion(_mag.Hovering || _kbMode || _dragging);
    }

    private void SetViewport()
    {
        var rect=Axis.ToPhysical(new Rect(0,0,_m.WindowWidth,Axis.CrossExtent));
        _graphics.SetViewport(rect.Width,rect.Height,_scale);
        _compositionPlate.Visual.TransformMatrix=Axis.PlateTransform;
    }
    private void SetWindowBounds()
    {
        if (!_strip) WindowStyler.SetBounds(_hwnd,_winX,_winY,_winW,_winH,topmost:!_menuOpen);
        else if (!Vertical) WindowStyler.SetBounds(_hwnd,_winX,BaseEdge()-2,_winW,2,topmost:!_menuOpen);
        else WindowStyler.SetBounds(_hwnd,_s.Edge==DockEdge.Left?BaseEdge():BaseEdge()-2,_winY,2,_winH,topmost:!_menuOpen);
        _app.TopBar?.ReassertMenus();
    }
    private void ApplyBlur()
    {
        if (_strip) return;
        var rect=Axis.ToPhysical(new Rect(_m.RestPlateLeft,_m.PlateTop,_m.RestPlateRight-_m.RestPlateLeft,_s.DockHeight));
        int l=(int)(rect.X*_scale),r=(int)(rect.Right*_scale),t=(int)(rect.Y*_scale),bt=(int)(rect.Bottom*_scale);
        try { WindowStyler.SetBlurBehind(_hwnd, _s.Blur, l, t, r, bt, (int)(_s.CornerRadius * _scale)); }
        catch (Exception ex) { _app.Log.Warn("blur failed: " + ex.Message); }
    }

    private bool _regionApplied;

    /// <summary>While idle the window is clipped to the plate so its transparent margins never swallow clicks meant for other apps.</summary>
    private void UpdateRegion(bool full)
    {
        if (_strip) return;
        if (_regionApplied && full == _regionFull) return;
        _regionApplied = true;
        _regionFull = full;
        try
        {
            if (full) WindowStyler.SetRegion(_hwnd, null);
            else
            {
                var rect=Axis.ToPhysical(new Rect(Math.Max(0,_m.RestPlateLeft-24),Math.Max(0,_m.PlateTop-6),Math.Min(_m.WindowWidth,_m.RestPlateRight+24)-Math.Max(0,_m.RestPlateLeft-24),_m.PlateBottom+_m.ShadowMargin-Math.Max(0,_m.PlateTop-6)));
                WindowStyler.SetRegion(_hwnd, ((int)(rect.X*_scale),(int)(rect.Y*_scale),(int)Math.Ceiling(rect.Right*_scale),(int)Math.Ceiling(rect.Bottom*_scale)));
            }
        }
        catch (Exception ex) { _app.Log.Warn("region failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ entries & visuals

    private void OnEntriesChanged(IReadOnlyList<DockEntry> entries)
    {
        if (_disposed) return;
        if (_menuOpen) { _deferredEntries = entries; return; }
        _motion.SetEntries(entries, animate: _shown);
        RecomputeMetrics(force: false);
        SyncVisuals();
        Invalidate();
    }

    private void SyncVisuals()
    {
        var alive = new HashSet<string>(_motion.Items.Select(i => i.Key));
        foreach (var k in _visuals.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            var v = _visuals[k];
            _itemsVisual.Children.Remove(v.HostVisual);
            if (v.IndicatorVisual is not null) { _itemsVisual.Children.Remove(v.IndicatorVisual); v.IndicatorVisual.Dispose(); }
            if (v.Icon is not null) v.Icon.Dispose(); else v.HostVisual.Dispose();
            _visuals.Remove(k);
        }
        int decode = DecodePx();
        foreach (var it in _motion.Items)
        {
            if (!_visuals.TryGetValue(it.Key, out var v))
            {
                v = CreateVisual(it);
                _visuals[it.Key] = v;
            }
            if (v.Icon is not null) BindIcon(v, it.Entry, decode);
        }
        WarmLabels();
    }

    private void WarmLabels()
    {
        if (!_s.ShowLabels || _disposed) return;
        foreach (var name in _motion.Items.Select(i => i.Entry.Name).Distinct().Take(64)) _compositionLabel.Prepare(name, _scale);
    }

    internal bool LabelsPrepared => !_compositionLabel.IsPreparing;
    internal int LabelRasterizations => _compositionLabel.Rasterizations;
    internal async Task VerifyTextureDecodeForSmokeAsync(byte[] bytes)
    {
        using var texture = await _graphics.DecodeAsync(bytes, 64);
        if (texture.Size.Width <= 0 || texture.Size.Height <= 0) throw new InvalidOperationException("Native texture did not decode.");
    }

    private int DecodePx() => (int)Math.Ceiling(_s.IconSize * (_s.MagnificationEnabled ? _s.MaxScalePercent / 100.0 : 1.0) * _scale);

    private ItemVisual CreateVisual(MotionItem it)
    {
        Visual host;
        CompositionIcon? icon = null;
        if (it.IsSeparator)
        {
            host = CreateMark(Vertical ? _s.IconSize * .55f : 1.5f, Vertical ? 1.5f : _s.IconSize * .55f, .75f, Color.FromArgb(90, 255, 255, 255));
        }
        else
        {
            icon = new CompositionIcon(_graphics);
            icon.SetSize(_s.IconSize);
            host = icon.Visual;
        }
        _itemsVisual.Children.InsertAtTop(host);
        var v = new ItemVisual { Icon = icon, HostVisual = host };
        v.HostVisual.CenterPoint = Vector3.Zero;
        RebuildIndicator(v, it);
        return v;
    }

    private void RebuildIndicator(ItemVisual v, MotionItem? it = null)
    {
        it ??= _motion.Items.FirstOrDefault(i => _visuals.TryGetValue(i.Key, out var vv) && ReferenceEquals(vv, v));
        if (v.IndicatorVisual is not null) { _itemsVisual.Children.Remove(v.IndicatorVisual); v.IndicatorVisual.Dispose(); v.IndicatorVisual = null; }
        if (it is null || it.Entry.Kind != DockEntryKind.App) return;
        float width = _s.IndicatorStyle == IndicatorStyle.Bar ? 16 : 5;
        float height = _s.IndicatorStyle == IndicatorStyle.Bar ? 3 : 5;
        var shape = CreateMark(Vertical ? height : width, Vertical ? width : height, height / 2, Color.FromArgb(255, 235, 238, 245));
        shape.Opacity = 0;
        _itemsVisual.Children.InsertAtTop(shape);
        v.IndicatorVisual = shape;
        v.IndicatorWidth = width; v.IndicatorHeight = height;
        v.IndicatorVisual.CenterPoint = new Vector3((Vertical ? v.IndicatorHeight : v.IndicatorWidth)*.5f,(Vertical ? v.IndicatorWidth : v.IndicatorHeight)*.5f,0);
        v.LastIndicatorOffset = new(float.NaN); v.LastIndicatorScale = new(float.NaN); v.LastIndicatorOpacity = float.NaN;
    }

    private ShapeVisual CreateMark(float width, float height, float radius, Color color)
    {
        var compositor = _graphics.Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2(width, height); geometry.CornerRadius = new Vector2(radius);
        var shape = compositor.CreateSpriteShape(geometry); shape.FillBrush = compositor.CreateColorBrush(color);
        var visual = compositor.CreateShapeVisual(); visual.Size = new Vector2(width, height); visual.Shapes.Add(shape);
        return visual;
    }

    private void BindIcon(ItemVisual v, DockEntry entry, int decodePx)
    {
        if (v.Icon is null) return;
        switch (entry.Kind)
        {
            case DockEntryKind.Start: v.Icon.Bind(SpecialApps.Start, GlyphKind.Start, decodePx); break;
            case DockEntryKind.Launchpad: v.Icon.Bind(SpecialApps.Launchpad, GlyphKind.Launchpad, decodePx); break;
            default: if (entry.App is not null) v.Icon.Bind(entry.App, GlyphKind.App, decodePx); break;
        }
    }

    /// <summary>Re-resolves every icon (override / pack changed).</summary>
    public void ReloadIcons()
    {
        foreach (var v in _visuals.Values) v.Icon?.Reload();
    }

    // ------------------------------------------------------------------ visibility

    private bool _blockFullscreenReveal;
    public void SetFullscreenHidden(bool hidden, bool blockEdgeReveal = false)
    {
        if(hidden)_preview.Hide();
        bool block = hidden && blockEdgeReveal;
        if (_fsHidden == hidden && _blockFullscreenReveal == block) return;
        _blockFullscreenReveal = block;
        if (_traceInput) _app.Log.Info("screen-filling app concealment: hidden=" + hidden);
        _fsHidden = hidden;
        _concealForFullscreen = hidden && _hide < 1;
        if (hidden) { _idleFor = 1; EndHover(); CancelPress(); }
        else { LeaveStrip(); _idleFor = 0; }
        // Retain the visible bounds until the exit finishes; do not jump to the monitor bottom mid-slide.
        if (!hidden || _strip) Reposition();
        UpdateVisibility(); Invalidate();
    }
    public void SetUserHidden(bool hidden)
    {
        if(hidden)_preview.Hide();
        if (_userHidden == hidden) return;
        _userHidden = hidden;
        if (hidden) { EndHover(); CancelPress(); } else { _idleFor = 0; LeaveStrip(); }
        UpdateVisibility(); Invalidate();
    }

    public void SetCaptureHidden(bool hidden)
    {
        if(hidden)_preview.Hide();
        if (_captureHidden == hidden) return;
        _captureHidden = hidden;
        if (_traceInput) _app.Log.Info("screen capture overlay: hidden=" + hidden);
        if (hidden)
        {
            EndHover(); CancelPress(); StopLoop();
            _hide = 1; ApplyHideOffset();
            WindowStyler.Hide(_hwnd); _shown = false;
        }
        else
        {
            _idleFor = 0;
            if (!_fsHidden && !_suspended && !_userHidden) LeaveStrip();
            UpdateVisibility(); Invalidate();
        }
    }

    /// <summary>The Launchpad (or a dialog) covers the screen: the dock must not react to the cursor meanwhile.</summary>
    public void SetSuspended(bool suspended)
    {
        if(suspended)_preview.Hide();
        if (_suspended == suspended) return;
        _suspended = suspended;
        if (suspended) { EndHover(); CancelPress(); }
        else { _idleFor = 0; LeaveStrip(); }
        UpdateVisibility(); Invalidate();
    }

    private bool WantsWindowVisible => !_captureHidden && !((_userHidden || _suspended || _blockFullscreenReveal) && _hide >= 1) && !(_strip && !_s.RevealOnEdgeHover);

    private void UpdateVisibility()
    {
        if (_disposed) return;
        if (WantsWindowVisible)
        {
            if (!_shown)
            {
                _shown = true;
                WindowStyler.Show(_hwnd, false);
                Reposition();
            }
            else WindowStyler.Show(_hwnd, false);
            Invalidate();
        }
        else if (_shown || WindowStyler.IsVisible(_hwnd))
        {
            WindowStyler.Hide(_hwnd);
            _shown = false;
            if (_mag.Hovering) EndHover();
        }
    }

    /// <summary>Keeps the dock above other topmost windows; called on foreground changes.</summary>
    public void Reassert()
    {
        if (_disposed || _menuOpen || _captureHidden || _suspended || _userHidden || _concealForFullscreen || !WantsWindowVisible || !_shown) return;
        SetWindowBounds();
    }

    // ------------------------------------------------------------------ frame loop

    public void Invalidate() { _renderNeeded = true; StartLoop(); }

    private void StartLoop()
    {
        if (_looping || _disposed || _menuOpen) return;
        _looping = true;
        _lastTicks = Stopwatch.GetTimestamp();
        _frameClock.Start();
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        _frameClock.Stop();
    }

    private void OnRendering()
    {
        long start = Stopwatch.GetTimestamp();
        try { Tick(); }
        catch (Exception ex) { _app.Log.Error("dock tick failed", ex); StopLoop(); }
        if (_diagnosticHover) _diagnosticFrameCosts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (_diagnosticHover && Stopwatch.GetElapsedTime(_diagnosticCpuSampleStarted).TotalSeconds >= 1)
        {
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            double elapsed = Stopwatch.GetElapsedTime(_diagnosticCpuSampleStarted).TotalMilliseconds;
            _diagnosticCpuSamples.Add((cpu - _diagnosticCpuSample).TotalMilliseconds / elapsed * 100 / Environment.ProcessorCount);
            _diagnosticCpuSample = cpu; _diagnosticCpuSampleStarted = Stopwatch.GetTimestamp();
        }
    }

    private (float X, float Y)? CursorDip()
    {
        if (IgnoreRealCursorForDiagnostics) return null;
        var c = WindowStyler.CursorPos();
        if (c is null) return null;
        var logical=Axis.ToLogical((float)((c.Value.X-_winX)/_scale),(float)((c.Value.Y-_winY)/_scale));
        return (logical.X,logical.Y);
    }

    private void Tick()
    {
        if (_menuOpen) return;
        if (_captureHidden) { StopLoop(); return; }
        long now = Stopwatch.GetTimestamp();
        float dt = (float)((now - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = now;
        dt = Math.Clamp(dt, 0f, 0.1f);
        _frameDelta = dt;

        var cur = CursorDip();
        if (_diagnosticHover)
        {
            _diagnosticElapsed += dt;
            _diagnosticFrameIntervals.Add(dt * 1000);
            cur = (_diagnosticCursorX ?? _m.RestPlateLeft + (_m.RestPlateRight - _m.RestPlateLeft) * (float)(.5 + (_diagnosticStationary ? 0 : .35 * Math.Sin(_diagnosticElapsed * 3))), _m.PlateTop + _s.DockHeight * .5f);
        }
        if (!_kbMode && !_suspended && !_userHidden && !_concealForFullscreen)
        {
            if (_mag.Hovering && !_dragging)
            {
                bool inside = _preview.PointerInside || cur is { } c && !_strip && _m.KeepZone.Contains(c.X, c.Y);
                if (!inside && !_menuOpen) EndHover();
                else if (!_preview.PointerInside && cur is { } c2) _mag.SetCursor(c2.X, false);
            }
            else if (_dragging && cur is { } cd) _mag.SetCursor(cd.X, false);
            else if (!_mag.Hovering && !_strip && !_suspended && _shown && IsVisible && cur is { } ce && _m.EnterZone.Contains(ce.X, ce.Y) && IsCursorOnThisWindow())
                BeginHover(ce.X);
        }
        else if (_kbIndex >= 0 && _kbIndex < _motion.Items.Count)
        {
            var it = _motion.Items[_kbIndex];
            _mag.SetCursor(it.BaseX + _gp.IconSize * 0.5f, false);
        }

        float previousZoom = _mag.Zoom, previousCursor = _mag.CursorX;
        bool a = _mag.Advance(dt, _s.Responsiveness, _s.AnimationSpeed, _s.Smoothness);
        bool b = _motion.Advance(dt, _s);
        bool c3 = StepAutoHide(dt, cur);
        RecomputeMetrics(force: false);
        if (_motion.Items.Count != _visuals.Count) SyncVisuals();

        if (_renderNeeded || b || c3 || _mag.Zoom != previousZoom || _mag.CursorX != previousCursor || cur != _lastRenderCursor || _labelAlpha is > .001f and < .999f)
        {
            Render(cur);
            _lastRenderCursor = cur;
            _renderNeeded = false;
        }

        bool full = _mag.Hovering || _mag.Zoom > 0.001f || b || _dragging || _kbMode || _menuOpen || _hide is > 0 and < 1;
        if (!_strip) UpdateRegion(full);

        if (!(a || b || c3 || _dragging || (_diagnosticHover && !_diagnosticStationary)) && (_labelAlpha <= .001f || _labelAlpha >= .999f)) StopLoop();
    }

    private bool IsCursorOnThisWindow()
    {
        var c = WindowStyler.CursorPos();
        return c is not null && c.Value.X >= _winX && c.Value.X < _winX + _winW && c.Value.Y >= _winY && c.Value.Y < _winY + _winH;
    }

    private void BeginHover(float cursorX)
    {
        bool snap = _mag.Zoom <= 0.001f;
        _mag.Hovering = true;
        _mag.SetCursor(cursorX, snap);
        _renderNeeded = true;
        StartLoop();
    }

    private void EndHover()
    {
        _mag.Hovering = false;
        _hoverIndex = -1;
        _renderNeeded = true;
        StartLoop();
    }

    // ------------------------------------------------------------------ auto-hide

    private bool StepAutoHide(float dt, (float X, float Y)? cur)
    {
        bool forced = _suspended || _userHidden || _concealForFullscreen || _blockFullscreenReveal;
        bool busy = !forced && (_mag.Hovering || _dragging || _menuOpen || _kbMode);
        if (busy) _idleFor = 0; else _idleFor += dt;
        float target = forced || EffectiveAutoHide && !busy && _idleFor > 0.55f ? 1f : 0f;
        if (_strip)
        {
            if (target == 1) return false;
            LeaveStrip();
        }
        float speed = 1.2f + _s.AnimationSpeed * 0.3f;
        float before = _hide;
        _hide = target > _hide ? Math.Min(target, _hide + dt * speed) : Math.Max(target, _hide - dt * speed * 1.4f);
        ApplyHideOffset();
        if (_hide >= 1f && !_strip) { _concealForFullscreen = false; EnterStrip(); }
        return _hide != target || before != _hide || (EffectiveAutoHide && !busy && target == 0f && _idleFor < 0.56f);
    }

    private void ApplyHideOffset()
    {
        float dist = _m.WindowHeight + ExtraBottomDip - _m.PlateTop + 4;
        var offset = Axis.HideOffset(Magnification.Smooth01(_hide) * dist);
        _graphics.Content.Offset = offset;
    }

    private void EnterStrip()
    {
        _preview.Hide();
        _strip = true;
        if (_mag.Hovering) EndHover();
        Reposition();
        WindowStyler.SetRegion(_hwnd, null);
        _regionApplied = false;
        SetWindowBounds();
        UpdateVisibility();
    }

    private void LeaveStrip()
    {
        if (!_strip) return;
        _strip = false;
        _hide = Math.Max(_hide, 1f);
        WindowStyler.SetBounds(_hwnd, _winX, _winY, _winW, _winH);
        UpdateVisibility();
        _regionApplied = false;
        UpdateRegion(true);
        StartLoop();
    }

    /// <summary>Brings an auto-hidden dock back (edge hover or keyboard).</summary>
    public void Reveal()
    {
        if (_captureHidden || _suspended || _userHidden || _concealForFullscreen || _blockFullscreenReveal) return;
        _idleFor = 0;
        if (_strip) LeaveStrip();
        else if (!_shown) UpdateVisibility();
        StartLoop();
    }

    // ------------------------------------------------------------------ rendering

    private static void SetVisual(Visual v, Vector3 offset, Vector3 scale, float opacity, ref Vector3 previousOffset, ref Vector3 previousScale, ref float previousOpacity)
    {
        offset = new Vector3(MathF.Round(offset.X * 8) / 8, MathF.Round(offset.Y * 8) / 8, offset.Z);
        scale = new Vector3(MathF.Round(scale.X * 4096) / 4096, MathF.Round(scale.Y * 4096) / 4096, scale.Z);
        opacity = MathF.Round(opacity * 255) / 255;
        if (previousOffset != offset) { v.Offset = offset; previousOffset = offset; }
        if (previousScale != scale) { v.Scale = scale; previousScale = scale; }
        if (previousOpacity != opacity) { v.Opacity = opacity; previousOpacity = opacity; }
    }

    private void Render((float X, float Y)? cur)
    {
        var items = _motion.Items;
        var res = DockLayout.Compute(_gp, _m, items, _mag.CursorX, _mag.Zoom, _layoutResult);

        float pl = res.PlateLeft, pw = Math.Max(0, res.PlateRight - res.PlateLeft);
        _compositionPlate.SetBounds(pl, _m.PlateTop, pw, _s.DockHeight);

        float iconSize = _gp.IconSize;
        float centerY = _m.PlateTop + _s.DockHeight * 0.5f;
        float indY = _m.PlateBottom - 5.5f;
        foreach (var it in items)
        {
            if (!_visuals.TryGetValue(it.Key, out var v)) continue;
            float pe = it.Presence;
            if (it.IsSeparator)
            {
                var separator=Axis.ToPhysical(new Rect(it.X+it.Size*.5f-.75f,centerY-_s.IconSize*.275f,1.5f,_s.IconSize*.55f));
                SetVisual(v.HostVisual, new Vector3(separator.X,separator.Y,0), Vector3.One, pe, ref v.LastOffset, ref v.LastScale, ref v.LastOpacity);
                continue;
            }
            float scale = it.Size / iconSize;
            bool dragged = _dragging && it.Key == _dragKey;
            SetVisual(v.HostVisual, IconOffset(it.X,it.Top,it.Size), new Vector3(scale, scale, 1), dragged ? Math.Min(pe, .92f) : pe, ref v.LastOffset, ref v.LastScale, ref v.LastOpacity);
            if (v.IndicatorVisual is { } iv)
            {
                float w = v.IndicatorWidth, h = v.IndicatorHeight;
                float alpha = it.RunningAlpha * pe * (_s.ShowActiveIndicator ? 0.55f + 0.45f * it.ActiveAlpha : 0.9f);
                var indicatorScale = _s.ShowActiveIndicator ? new Vector3(1f + .25f * it.ActiveAlpha, 1f + .25f * it.ActiveAlpha, 1f) : Vector3.One;
                var indicator=Axis.ToPhysical(new Rect(it.X+it.Size*.5f-w*.5f,indY-h*.5f,w,h));
                SetVisual(iv, new Vector3(indicator.X,indicator.Y,0), indicatorScale, alpha, ref v.LastIndicatorOffset, ref v.LastIndicatorScale, ref v.LastIndicatorOpacity);
            }
        }

        UpdateLabel(cur);
        int previewIndex=cur is { } pc?DockLayout.HitTest(items,_gp,_m,pc.X,pc.Y):-1;
        if(!_menuOpen&&!_dragging&&!_strip&&!_captureHidden&&!_suspended&&!_userHidden&&previewIndex>=0)
        {
            var item=items[previewIndex];
            _preview.Track(item.Entry,ScreenRect(new Rect(item.X,item.Top,item.Size,item.Size)),edge:_s.Edge);
        }
        else _preview.Track(null,default);
    }

    private void UpdateLabel((float X, float Y)? cur)
    {
        int idx = -1;
        bool show = _s.ShowLabels && (_mag.Hovering || _kbMode) && !_menuOpen && !_dragging && !_strip;
        if (show)
        {
            if (_kbMode) idx = _kbIndex;
            else if (cur is { } c) idx = DockLayout.HitTest(_motion.Items, _gp, _m, c.X, c.Y);
        }
        _hoverIndex = idx;
        float target = 0;
        if (idx >= 0 && idx < _motion.Items.Count)
        {
            var it = _motion.Items[idx];
            var name = it.Entry.Name;
            if (!string.IsNullOrEmpty(name))
            {
                target = _compositionLabel.Select(name, _scale) ? 1 : 0;
                float w = _compositionLabel.Visual.Size.X, h = _compositionLabel.Visual.Size.Y;
                float x = Math.Clamp(it.X + it.Size * 0.5f - w * 0.5f, 2, Math.Max(2, _m.WindowWidth - w - 2));
                float y = Math.Max(2, it.Top - h - 8);
                if (!Vertical) _compositionLabel.Visual.Offset = new Vector3(x,y,0);
                else
                {
                    var icon=Axis.ToPhysical(new Rect(it.X,it.Top,it.Size,it.Size));
                    float sideX=_s.Edge==DockEdge.Left?icon.Right+12:icon.X-w-12;
                    _compositionLabel.Visual.Offset=new Vector3(Math.Clamp(sideX,2,Math.Max(2,Axis.CrossExtent-w-2)),Math.Clamp(icon.Y+icon.Height*.5f-h*.5f,2,Math.Max(2,_m.WindowWidth-h-2)),0);
                }
            }
        }
        _labelAlpha += (target - _labelAlpha) * (1f - MathF.Exp(-26f * _frameDelta));
        if (Math.Abs(target - _labelAlpha) < 0.01f) _labelAlpha = target;
        _compositionLabel.Visual.Opacity = _labelAlpha;
    }

    // ------------------------------------------------------------------ pointer input

    internal int PointerMessages => _window.PointerMessages;
    internal (int X, int Y) LaunchpadPointForDiagnostics()
    {
        var item = _motion.Items.First(i => i.Entry.Kind == DockEntryKind.Launchpad);
        var point=Axis.ToPhysical(item.X+item.Size/2,item.Top+item.Size/2);
        return ((int)(point.X*_scale),(int)(point.Y*_scale));
    }

    private void OnNativePointer(DockPointerEvent e)
    {
        if (_disposed || IgnoreRealCursorForDiagnostics) return;
        if (_traceInput && e.Kind is not (DockPointerKind.Move or DockPointerKind.Leave))
            _app.Log.Info($"native pointer: {e.Kind}, x={e.X}, y={e.Y}");
        var logical=Axis.ToLogical((float)(e.X/_scale),(float)(e.Y/_scale));
        float x=logical.X,y=logical.Y;
        if (e.Kind == DockPointerKind.Cancel) { CancelPress(); return; }
        if (e.Kind == DockPointerKind.Leave) { StartLoop(); return; }
        if (_menuOpen || _suspended || _userHidden || _captureHidden || _concealForFullscreen) return;
        if (_strip) { if (_s.RevealOnEdgeHover && e.Kind == DockPointerKind.Move) Reveal(); return; }
        switch (e.Kind)
        {
            case DockPointerKind.Move:
                StartLoop();
                if (!_mag.Hovering && _m.EnterZone.Contains(x, y)) BeginHover(x);
                if (_pressIndex >= 0 && e.LeftPressed)
                {
                    if (!_dragging && Math.Abs(x - _pressPos.X) + Math.Abs(y - _pressPos.Y) > HoverDragThreshold) TryBeginDrag();
                    if (_dragging) UpdateDrag(x);
                }
                break;
            case DockPointerKind.LeftDown:
                _pressPos = new global::Windows.Foundation.Point(x, y);
                _pressIndex = HitTestIncludingSeparators(x, y);
                if (_pressIndex >= 0) _window.Capture();
                break;
            case DockPointerKind.LeftUp: OnPointerReleased(x, y, middle: false); break;
            case DockPointerKind.MiddleUp: OnPointerReleased(x, y, middle: true); break;
            case DockPointerKind.RightUp:
                int hit = HitTestIncludingSeparators(x, y);
                var item = hit >= 0 && hit < _motion.Items.Count ? _motion.Items[hit] : null;
                // Finish dispatching this input before entering the modal menu loop.
                _app.UiQueue.TryEnqueue(() => { if (!_disposed) ShowContextMenu(item, null, null); });
                break;
        }
    }

    private int HitTestIncludingSeparators(float x, float y)
    {
        for (int i = 0; i < _motion.Items.Count; i++)
        {
            var item = _motion.Items[i];
            if (item.IsSeparator && item.Entry.IsPinned && x >= item.X - 6 && x <= item.X + item.Size + 6 && y >= _m.PlateTop && y <= _m.PlateBottom) return i;
        }
        return DockLayout.HitTest(_motion.Items, _gp, _m, x, y);
    }

    private void CancelPress()
    {
        if (_dragging) EndDrag(null);
        _pressIndex = -1;
    }

    private void OnPointerReleased(float x, float y, bool middle)
    {
        int pressed = _pressIndex;
        _pressIndex = -1;
        _window.Release();
        int hit = DockLayout.HitTest(_motion.Items, _gp, _m, x, y);
        if (middle)
        {
            if (hit >= 0 && _motion.Items[hit].Entry is { Kind: DockEntryKind.App, App: { } app }) LaunchApp(app, newInstance: true);
            return;
        }
        if (_dragging) { EndDrag(y); return; }
        if (pressed >= 0 && hit == pressed && hit < _motion.Items.Count)
        {
            var entry = _motion.Items[hit].Entry;
            _app.UiQueue.TryEnqueue(() => { if (!_disposed) ActivateEntry(entry); });
        }
    }

    // ------------------------------------------------------------------ drag to reorder / unpin

    private void TryBeginDrag()
    {
        if (_pressIndex < 0 || _pressIndex >= _motion.Items.Count) return;
        var it = _motion.Items[_pressIndex];
        if (!it.Entry.IsPinned || it.Entry.Kind is not (DockEntryKind.App or DockEntryKind.Separator)) return;
        _dragging = true;
        _dragKey = it.Key;
        if (_visuals.TryGetValue(it.Key, out var v)) { _itemsVisual.Children.Remove(v.HostVisual); _itemsVisual.Children.InsertAtTop(v.HostVisual); }
        StartLoop();
    }

    private void UpdateDrag(float xDip)
    {
        var drag = _motion.Items.FirstOrDefault(i => i.Key == _dragKey);
        if (drag is null) return;
        drag.DragX = Math.Clamp(xDip, 0, _m.WindowWidth);
        var pinned = _motion.Items.Where(i => !i.Leaving && i.Entry.IsPinned).ToList();
        int current = pinned.IndexOf(drag);
        int target = pinned.Count(i => i != drag && i.BaseX + (i.IsSeparator ? _gp.SeparatorWidth : _gp.IconSize) * .5f < xDip);
        if (current >= 0 && target != current) _app.Pinned.Move(drag.Key, target);
        StartLoop();
    }

    private void EndDrag(float? releaseY)
    {
        var drag = _motion.Items.FirstOrDefault(i => i.Key == _dragKey);
        var key = _dragKey;
        _dragging = false;
        _dragKey = null;
        if (drag is not null)
        {
            if (_visuals.TryGetValue(drag.Key, out var v)) { _itemsVisual.Children.Remove(v.HostVisual); _itemsVisual.Children.InsertAtTop(v.HostVisual); }
            drag.Slide = drag.X - drag.BaseX;
            drag.DragX = null;
            // dragged out of the dock: unpin (a running app stays as a running icon)
            if (releaseY is float y && y < _m.PlateTop - 50 && key is not null) _app.Pinned.Remove(key);
        }
        StartLoop();
    }

    // ------------------------------------------------------------------ actions

    private void ActivateEntry(DockEntry e)
    {
        if(_visuals.TryGetValue(e.Key,out var visual))visual.Icon?.Pulse(_s.Edge);
        switch (e.Kind)
        {
            case DockEntryKind.Start:
                StartButton.ToggleStartMenu();
                break;
            case DockEntryKind.Launchpad:
                _app.ToggleLaunchpad();
                break;
            case DockEntryKind.App when e.IsRunning:
                _ = ActivateRunningAppAsync(e);
                break;
            case DockEntryKind.App when e.App is { } app:
                LaunchApp(app, newInstance: false);
                break;
        }
    }

    private void LaunchApp(AppRef app, bool newInstance)
    {
        var item=_motion.Items.FirstOrDefault(i=>!i.Leaving&&i.Entry.App?.Key==app.Key);
        if(item is not null&&AnimationRectFor(item) is { } origin)_app.Dock.RememberLaunchOrigin(app,origin);
        _ = _app.Worker.Run(() =>
        {
            if (!ShellLauncher.Launch(app, newInstance))
            {
                _app.Log.Warn("launch failed: " + app.Name);
                _app.UiQueue.TryEnqueue(() => _app.Notify("Could not open " + app.Name, "The shortcut or application is unavailable.", true));
            }
        });
    }

    private readonly HashSet<string> _activatingApps = new();
    private async Task ActivateRunningAppAsync(DockEntry entry)
    {
        if (!_activatingApps.Add(entry.Key)) return;
        try
        {
            var item = _motion.Items.FirstOrDefault(i => i.Key == entry.Key);
            if (item is not null && AnimationRectFor(item) is { } rect) _app.Dock.RememberAnimationOrigin(entry.Windows, rect);
            var result = await WindowActivator.ActivateOrMinimizeAsync(entry.Windows, _s.MinimizeOnRepeatClick);
            if (result == WindowActivator.ActivationResult.NoLiveWindow)
            {
                _app.Running.FullScan();
                if (entry.App is { } app) LaunchApp(app, false);
            }
            else if (result == WindowActivator.ActivationResult.Denied)
            {
                _app.Log.Warn("foreground activation was not confirmed: " + entry.Name);
                _app.Notify("Could not switch to " + entry.Name, "Windows did not give this window focus. Check for an open dialog or a different virtual desktop.", true);
            }
        }
        catch (Exception ex) { _app.Log.Error("window activation failed", ex); }
        finally { _activatingApps.Remove(entry.Key); }
    }

    private const int CmdOpen = 1, CmdPin = 2, CmdFolder = 3, CmdIcon = 4, CmdResetIcon = 5, CmdClose = 6, CmdSettings = 20, CmdLaunchpad = 21, CmdQuit = 22, CmdAddSeparator = 30, CmdRemoveSeparator = 31, CmdMoveSeparatorLeft = 32, CmdMoveSeparatorRight = 33;

    private void ShowContextMenu(MotionItem? it, int? x, int? y)
    {
        var e = it?.Entry;
        var items = new List<MenuItemSpec>();
        AppRef? app = e?.Kind switch
        {
            DockEntryKind.App => e.App,
            DockEntryKind.Start => SpecialApps.Start,
            DockEntryKind.Launchpad => SpecialApps.Launchpad,
            _ => null,
        };
        if (e is { Kind: DockEntryKind.App, App: { } a })
        {
            items.Add(new(CmdOpen, e.IsRunning ? "New window" : "Open"));
            items.Add(new(CmdPin, e.IsPinned ? "Unpin from Dock" : "Pin to Dock"));
            if (e.IsRunning) items.Add(new(CmdClose, e.Windows.Count > 1 ? $"Close all windows ({e.Windows.Count})" : "Close window"));
            items.Add(new(CmdFolder, "Open file location", Disabled: ShellLauncher.LocationOf(a) is null));
            items.Add(MenuItemSpec.Separator);
        }
        else if (e is { Kind: DockEntryKind.Launchpad }) { items.Add(new(CmdOpen, "Open Launchpad")); items.Add(MenuItemSpec.Separator); }
        else if (e is { Kind: DockEntryKind.Separator, IsPinned: true })
        {
            var pinned = _app.Pinned.Snapshot();
            int index = pinned.ToList().FindIndex(p => p.Key == e.Key);
            items.Add(new(CmdRemoveSeparator, "Remove separator"));
            items.Add(new(CmdMoveSeparatorLeft, "Move separator left", Disabled: index <= 0));
            items.Add(new(CmdMoveSeparatorRight, "Move separator right", Disabled: index < 0 || index >= pinned.Count - 1));
            items.Add(MenuItemSpec.Separator);
        }
        if (app is not null)
        {
            items.Add(new(CmdIcon, "Change icon…"));
            items.Add(new(CmdResetIcon, "Reset icon", Disabled: !_app.Overrides.Has(app)));
            items.Add(MenuItemSpec.Separator);
        }
        items.Add(new(CmdAddSeparator, e?.IsPinned == true ? "Add separator after this item" : "Add separator"));
        items.Add(new(CmdLaunchpad, "Launchpad"));
        items.Add(new(CmdSettings, "Dock settings…"));
        items.Add(new(CmdQuit, "Quit FloatingDock"));

        var previousForeground = WindowStyler.Foreground();
        FloatingDock.Windows.Native.NativeMethods.GetWindowThreadProcessId(previousForeground, out var previousPid);
        FreezeForMenu();
        int cmd;
        try
        {
            WindowStyler.SetNoActivate(_hwnd, false);
            int aboveDock = _winY + (int)((_m.PlateTop - 8) * _scale);
            var cursor = WindowStyler.CursorPos();
            var plate=PlateScreenForDiagnostics;
            if (!Vertical) cmd=NativeMenu.Show(_hwnd,items,x??cursor?.X??_winX,Math.Min(y??cursor?.Y??aboveDock,aboveDock),bottomAlign:true);
            else cmd=NativeMenu.Show(_hwnd,items,_s.Edge==DockEdge.Left?plate.Right+8:plate.Left-8,y??cursor?.Y??(_winY+_winH/2),bottomAlign:false,rightAlign:_s.Edge==DockEdge.Right);
        }
        finally
        {
            WindowStyler.SetNoActivate(_hwnd, !_kbMode);
            if (!_kbMode && WindowStyler.Foreground() == _hwnd && previousForeground != _hwnd)
            {
                FloatingDock.Windows.Native.NativeMethods.GetWindowThreadProcessId(previousForeground, out var currentPid);
                if (currentPid == previousPid) WindowActivator.Activate(previousForeground);
            }
            ResumeAfterMenu();
        }
        if (cmd == 0) return;

        switch (cmd)
        {
            case CmdOpen when e is not null:
                if (e.Kind == DockEntryKind.Launchpad) _app.ToggleLaunchpad();
                else if (e.App is { } oa) LaunchApp(oa, newInstance: e.IsRunning);
                break;
            case CmdPin when e is { App: { } pa }:
                if (e.IsPinned) _app.Pinned.Remove(pa.Key); else _app.Pinned.Add(pa.Clone());
                break;
            case CmdClose when e is not null: WindowActivator.RequestClose(e.Windows); break;
            case CmdFolder when e?.App is { } fa:
                var loc = ShellLauncher.LocationOf(fa);
                if (loc is not null) ShellLauncher.RevealInExplorer(loc);
                break;
            case CmdIcon when app is not null: _ = WindowSetup.PickIconAsync(_app.MessageHandle, app); break;
            case CmdResetIcon when app is not null: _app.TryResetIcon(app); break;
            case CmdLaunchpad: _app.ToggleLaunchpad(); break;
            case CmdSettings: _app.ShowSettings(); break;
            case CmdQuit: _app.Shutdown(); break;
            case CmdAddSeparator:
                var pins = _app.Pinned.Snapshot();
                int after = e is null ? -1 : pins.ToList().FindIndex(p => p.Key == e.Key);
                _app.Pinned.Add(AppRef.Separator(), after >= 0 ? after + 1 : pins.Count);
                break;
            case CmdRemoveSeparator when e is not null: _app.Pinned.Remove(e.Key); break;
            case CmdMoveSeparatorLeft when e is not null:
            case CmdMoveSeparatorRight when e is not null:
                int position = _app.Pinned.Snapshot().ToList().FindIndex(p => p.Key == e.Key);
                if (position >= 0) _app.Pinned.Move(e.Key, position + (cmd == CmdMoveSeparatorLeft ? -1 : 1));
                break;
        }
    }

    // ------------------------------------------------------------------ keyboard mode (hotkey)

    private void FreezeForMenu()
    {
        _preview.Hide();
        _menuOpen = true;
        StopLoop();
        _labelAlpha = 0;
        _compositionLabel.Visual.Opacity = 0;
    }

    private void ResumeAfterMenu()
    {
        _menuOpen = false;
        if (_deferredEntries is { } entries) { _deferredEntries = null; OnEntriesChanged(entries); }
        Invalidate();
    }

    internal async Task<bool> CheckFrozenMenuForSmokeAsync()
    {
        FreezeForMenu();
        var positions = _motion.Items.Select(i => i.X).ToArray();
        var cursor = _mag.CursorX;
        try
        {
            Invalidate();
            await Task.Delay(300);
            return !_looping && cursor == _mag.CursorX && positions.SequenceEqual(_motion.Items.Select(i => i.X));
        }
        finally { ResumeAfterMenu(); }
    }

    public void EnterKeyboardMode()
    {
        if (_disposed) return;
        Reveal();
        if (!IsVisible && !_shown) return;
        _prevForeground = WindowStyler.Foreground();
        _kbMode = true;
        var live = _motion.Items.Select((i, ix) => (i, ix)).Where(t => !t.i.IsSeparator && !t.i.Leaving).ToList();
        _kbIndex = live.FirstOrDefault(t => t.i.Entry.IsActive).ix;
        if (_kbIndex == 0 && live.Count > 0 && !live[0].i.Entry.IsActive) _kbIndex = live[0].ix;
        WindowStyler.SetNoActivate(_hwnd, false);
        _window.Focus();
        StartLoop();
    }

    private void ExitKeyboardMode(bool restoreFocus)
    {
        if (!_kbMode) return;
        _kbMode = false;
        _kbIndex = -1;
        WindowStyler.SetNoActivate(_hwnd, true);
        if (restoreFocus && _prevForeground != IntPtr.Zero) WindowActivator.Activate(_prevForeground);
        EndHover();
    }

    private void OnKeyDown(int key)
    {
        if (!_kbMode) return;
        int Step(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _motion.Items.Count; i += dir)
                if (!_motion.Items[i].IsSeparator && !_motion.Items[i].Leaving) return i;
            return from;
        }
        switch ((global::Windows.System.VirtualKey)key)
        {
            case global::Windows.System.VirtualKey.Up when Vertical:
            case global::Windows.System.VirtualKey.Left: _kbIndex = Step(_kbIndex, -1); break;
            case global::Windows.System.VirtualKey.Down when Vertical:
            case global::Windows.System.VirtualKey.Right: _kbIndex = Step(_kbIndex, +1); break;
            case global::Windows.System.VirtualKey.Home: _kbIndex = Step(-1, +1); break;
            case global::Windows.System.VirtualKey.End: _kbIndex = Step(_motion.Items.Count, -1); break;
            case global::Windows.System.VirtualKey.Escape: ExitKeyboardMode(true); break;
            case global::Windows.System.VirtualKey.Enter:
            case global::Windows.System.VirtualKey.Space:
                if (_kbIndex >= 0 && _kbIndex < _motion.Items.Count)
                {
                    var entry = _motion.Items[_kbIndex].Entry;
                    ExitKeyboardMode(false);
                    ActivateEntry(entry);
                }
                break;
            case global::Windows.System.VirtualKey.Application:
                if (_kbIndex >= 0 && _kbIndex < _motion.Items.Count)
                {
                    var it = _motion.Items[_kbIndex];
                    var anchor=ScreenRect(new Rect(it.X,it.Top,it.Size,it.Size));
                    ShowContextMenu(it,(anchor.Left+anchor.Right)/2,anchor.Top);
                }
                break;
            default: return;
        }
        StartLoop();
    }

    // ------------------------------------------------------------------ teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.Dispose();
        StopLoop();
        _frameClock.Dispose();
        foreach (var v in _visuals.Values) { v.Icon?.Dispose(); v.IndicatorVisual?.Dispose(); if (v.Icon is null) v.HostVisual.Dispose(); }
        _compositionLabel.Dispose(); _itemsVisual.Dispose(); _compositionPlate.Dispose();
        _vm.EntriesChanged -= OnEntriesChanged;
        _graphics.Dispose();
        _alpha.Dispose();
        _window.Dispose();

    }
}




