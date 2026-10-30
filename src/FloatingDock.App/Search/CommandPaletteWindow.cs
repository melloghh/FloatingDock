using FloatingDock.Core.Models;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Services;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Search;

/// <summary>A separate keyboard-first palette. Queries are cancellable and never run on the UI thread.</summary>
internal sealed class CommandPaletteWindow : IDisposable
{
    private sealed record Result(string Name,string Detail,string Kind,AppRef? App=null,string? Path=null,Action? Command=null);
    private readonly DockApp _app=DockApp.Current;
    private readonly Window _window;
    private readonly Border _root;
    private readonly TextBox _query=new(){BorderThickness=new Thickness(0),Background=new SolidColorBrush(Colors.Transparent),FontSize=17,Padding=new Thickness(4,10,8,10),VerticalAlignment=VerticalAlignment.Center};
    private readonly StackPanel _rows=new(){Spacing=2};
    private readonly Border _resultsPlate;
    private readonly ScrollViewer _list=new(){BorderThickness=new Thickness(0),Background=new SolidColorBrush(Colors.Transparent),MaxHeight=440,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly List<Button> _rowButtons=new();
    private int _selectedIndex=-1;
    private readonly TextBlock _status=new(){FontSize=12,Foreground=GlassStyle.Secondary,Margin=new Thickness(12,12,12,6),TextWrapping=TextWrapping.Wrap};
    private readonly DispatcherQueueTimer _debounce,_openingTimer;
    private readonly GlassBackdrop _glass=new();
    private readonly MenuBar.MenuSurfaceMotion _motion;
    private IReadOnlyList<Result> _results=Array.Empty<Result>();
    private Task<IReadOnlyList<AppRef>>? _catalog;
    private AppSearchIndex? _appIndex;
    private SearchUsage? _usage;
    private IReadOnlyList<AppRef> _searchApps=Array.Empty<AppRef>();
    private DateTime _catalogTime;
    private readonly StackPanel _panel=new(){Spacing=10};
    private CancellationTokenSource? _cancel;
    private int _generation;
    private bool _visible,_opening,_disposed;
    private bool _surfaceVisible;
    private int _visibilityGeneration;
    private IntPtr _returnFocus;
    private int _width,_left,_top,_maxHeight;
    private double _scale=1;
    internal IntPtr Handle { get; }
    internal bool Visible=>_visible;
    internal bool SurfaceVisible=>_surfaceVisible;
    internal MenuBar.MenuSurfaceMotion MotionForDiagnostics=>_motion;
    internal int ResultCount=>_results.Count;
    internal Task SearchForDiagnostics(string text) { _query.Text=text;_debounce.Stop();return SearchAsync(); }

    internal CommandPaletteWindow()
    {
        var input=new Grid { ColumnSpacing=10,Height=54 };
        input.ColumnDefinitions.Add(new(){Width=GridLength.Auto});input.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});input.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        input.Children.Add(new FontIcon { Glyph="\uE721",FontSize=17,Foreground=GlassStyle.Secondary,Margin=new Thickness(18,0,0,0) });
        _query.Margin=new Thickness(0);Grid.SetColumn(_query,1);input.Children.Add(_query);
        var esc=new TextBlock { Text="Esc",FontSize=11,Foreground=GlassStyle.Secondary,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,18,0) };Grid.SetColumn(esc,2);input.Children.Add(esc);
        _list.Content=_rows;
        var results=new StackPanel();results.Children.Add(_list);results.Children.Add(_status);
        _resultsPlate=GlassStyle.Plate(results,22,6);
        _panel.Children.Add(GlassStyle.Plate(input,28,0));_panel.Children.Add(_resultsPlate);
        _root=new Border { Child=_panel,RequestedTheme=ElementTheme.Dark,Background=new SolidColorBrush(Colors.Transparent) };
        _window=new Window { Title="FloatingDock Search",Content=_root,SystemBackdrop=_glass };
        WindowSetup.MakeBorderless(_window);Handle=WindowSetup.Hwnd(_window);WindowStyler.MakeOverlayWindow(Handle,noActivate:false);
        _glass.SetVisible(false);
        _motion=new(Handle,_root,_glass,minX:.96f,minY:.9f,lift:-10,radius:22);
        _motion.SetSplit(56,10);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_query,L.Text("Search apps"));
        foreach(var key in new[]{"TextControlBackground","TextControlBackgroundPointerOver","TextControlBackgroundFocused","TextControlBorderBrush","TextControlBorderBrushPointerOver","TextControlBorderBrushFocused"})_query.Resources[key]=new SolidColorBrush(Colors.Transparent);
        _debounce=_app.UiQueue.CreateTimer();_debounce.IsRepeating=false;_debounce.Interval=TimeSpan.FromMilliseconds(180);_debounce.Tick+=async(_,_)=>await SearchAsync();
        _openingTimer=_app.UiQueue.CreateTimer();_openingTimer.IsRepeating=false;_openingTimer.Interval=TimeSpan.FromMilliseconds(300);_openingTimer.Tick+=(_,_)=>_opening=false;
        _query.TextChanged+=(_,_)=> { if(!_visible)return;_cancel?.Cancel();_generation++;_results=Array.Empty<Result>();ClearRows();_debounce.Stop();_debounce.Start(); };
        _root.KeyDown+=OnKey;
        _window.Activated+=(_,e)=> { if(_visible&&!_opening&&e.WindowActivationState==WindowActivationState.Deactivated&&_app.TopBar?.SearchTogglePointerActive!=true)Hide(immediately:false); };
        _query.Loaded+=(_,_)=> { if(_visible)_query.Focus(FocusState.Programmatic); };
        _app.Pinned.Changed+=InvalidateApps;_app.UserTiles.Changed+=InvalidateApps;
    }
    internal void Toggle() { if(_visible)Hide(immediately:false);else Show(); }
    internal void Show()
    {
        if(_disposed||_visible)return;
        var mon=MonitorHelper.FromCursor()??MonitorHelper.Primary();if(mon is null)return;
        float progress=_surfaceVisible?_motion.Progress:0;
        if(!_surfaceVisible)_returnFocus=NativeMethods.GetForegroundWindow();
        _visibilityGeneration++;_visible=_opening=_surfaceVisible=true;_root.IsHitTestVisible=true;
        _query.PlaceholderText=L.Text("Search apps");_query.Text="";
        _results=Array.Empty<Result>();ClearRows();_status.Text=L.Text("Enter to open · Esc to close");
        int width=(int)(Math.Min(650,(mon.Bounds.Width/mon.Scale)-40)*mon.Scale);
        _width=width;_scale=mon.Scale;_maxHeight=(int)Math.Min(580,mon.Bounds.Height/mon.Scale-60);
        _left=mon.Bounds.Left+(mon.Bounds.Width-width)/2;_top=Math.Min(mon.Bounds.Top+(int)(mon.Bounds.Height*.18),mon.Bounds.Bottom-(int)(_maxHeight*mon.Scale)-16);
        _glass.SetVisible(true);_motion.Prepare(SurfaceMotion.Enabled?progress:1);
        _list.MaxHeight=Math.Max(44,_maxHeight-126);ResizeSurface(0);
        AnimateOpening();_window.Activate();WindowStyler.BringToFront(Handle);
        _openingTimer.Start();_query.Focus(FocusState.Programmatic);_debounce.Stop();_debounce.Start();
    }
    internal void Hide(bool immediately=true)
    {
        if(!_visible){if(immediately&&_surfaceVisible)FinishHide();return;}
        _visible=_opening=false;_generation++;_cancel?.Cancel();_debounce.Stop();_openingTimer.Stop();
        int generation=++_visibilityGeneration;_root.IsHitTestVisible=false;
        bool focused=NativeMethods.GetForegroundWindow()==Handle;
        if(focused&&NativeMethods.IsWindowVisible(_returnFocus)&&!NativeMethods.IsIconic(_returnFocus))NativeMethods.SetForegroundWindow(_returnFocus);
        if(immediately||!SurfaceMotion.Enabled)FinishHide();
        else _motion.Play(false,Math.Clamp(_app.Settings.Current.MenuAnimationMs*4/5,80,300),()=>{if(generation==_visibilityGeneration&&!_visible)FinishHide();});
    }
    private void FinishHide(){_motion.Stop();_surfaceVisible=false;_glass.SetVisible(false);_window.AppWindow.Hide();WindowStyler.Hide(Handle);}
    private async Task SearchAsync()
    {
        if(!_visible||_disposed)return;
        _cancel?.Cancel();_cancel?.Dispose();_cancel=new();var ct=_cancel.Token;int generation=++_generation;
        string query=_query.Text.Trim();var settings=_app.Settings.Current.Clone();_status.Text=L.Text("Searching…");
        try
        {
            _usage??=await _app.Worker.Run(()=>SearchUsage.Load(_app.Paths.SearchUsageFile));
            if(_disposed||!_visible||generation!=_generation||ct.IsCancellationRequested)return;
            if(query.Length==0&&!_usage.HasEntries)
            {_results=Array.Empty<Result>();ClearRows();_status.Text=L.Text("Search apps");ResizeSurface(0);return;}
            if(_catalog is not null&&DateTime.UtcNow-_catalogTime>TimeSpan.FromMinutes(5)){_catalog=null;_appIndex=null;}
            if(_catalog is null){_catalogTime=DateTime.UtcNow;_catalog=_app.Worker.Run<IReadOnlyList<AppRef>>(()=>AppCatalog.Load());}
            var catalog=await _catalog;if(ct.IsCancellationRequested)return;
            if(_appIndex is null)
            {
                _searchApps=_app.UserTiles.Snapshot().Concat(_app.Pinned.Snapshot()).Concat(catalog).Where(PaletteAppPolicy.Include).GroupBy(a=>a.Key).Select(g=>g.First()).ToArray();
                _appIndex=new AppSearchIndex(_searchApps);
            }
            var results=new List<Result>();
            var commands=new[]{new Result(L.Text("FloatingDock settings"),"","Command",Command:()=>_app.ShowSettings()),new Result(L.Text("Launchpad"),"","Command",Command:_app.ToggleLaunchpad)};
            if(query.Length>0)results.AddRange(commands.Where(c=>AppSearchIndex.Normalize(c.Name).Contains(AppSearchIndex.Normalize(query))));
            var apps=query.Length==0?_usage.Suggestions(_searchApps,Math.Min(8,settings.SearchResultLimit)):_appIndex.Search(query);
            results.AddRange(apps.Take(settings.SearchResultLimit).Select(a=>new Result(a.Name,"","App",App:a)));
            if(_disposed||!_visible||generation!=_generation||ct.IsCancellationRequested)return;
            _results=results.Take(settings.SearchResultLimit).ToArray();
            bool freshList=_rows.Children.Count==0||_opening;
            ClearRows();
            foreach(var result in _results)
            {
                var row=new Grid { ColumnSpacing=10,Padding=new Thickness(12,7,12,7),Height=46 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(24) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                if(result.App is not null){var icon=new IconView { Width=22,Height=22,VerticalAlignment=VerticalAlignment.Center };icon.Bind(result.App,GlyphKind.App,(int)Math.Ceiling(24*_scale));row.Children.Add(icon);}
                else row.Children.Add(new FontIcon { Glyph=result.Kind=="Folder"?"\uE8B7":result.Kind=="File"?"\uE8A5":"\uE713",FontSize=19,Foreground=GlassStyle.Secondary });
                var label=new Grid { VerticalAlignment=VerticalAlignment.Center,ColumnSpacing=10 };
                label.ColumnDefinitions.Add(new(){Width=GridLength.Auto});label.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
                var name=new TextBlock { Text=result.Name,FontSize=14,MaxWidth=330,TextTrimming=TextTrimming.CharacterEllipsis };label.Children.Add(name);
                if(result.Detail.Length>0){var detail=new TextBlock { Text=result.Detail,FontSize=12,Foreground=GlassStyle.Secondary,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(detail,1);label.Children.Add(detail);}
                Grid.SetColumn(label,1);row.Children.Add(label);var kind=new TextBlock { Text=L.Text(result.Kind),FontSize=12,Foreground=GlassStyle.Secondary,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(kind,2);row.Children.Add(kind);
                var button=new Button { Content=row,Tag=result,Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,CornerRadius=new CornerRadius(15),BorderThickness=new Thickness(0) };
                GlassStyle.Button(button);button.Background=new SolidColorBrush(Colors.Transparent);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,result.Name+" · "+L.Text(result.Kind));
                button.Click+=(_,_)=>Open(result);_rowButtons.Add(button);_rows.Children.Add(button);
                if(freshList)SurfaceMotion.Reveal(button,_rowButtons.Count-1);
            }
            Select(_results.Count>0?0:-1);
            _status.Text=L.Text(_results.Count==0&&query.Length==0?"Search apps":_results.Count==0?"No matches. Try an app name.":"↑ ↓ to select · Enter to open · Esc to close");
            ResizeSurface(_results.Count);
        }
        catch(OperationCanceledException){}
        catch(Exception ex) { if(generation==_generation&&_visible)_status.Text=L.Text("Search could not finish. Try again.");_catalog=null;_app.Log.Warn("Palette search failed: "+ex.Message); }
    }
    private void ResizeSurface(int count)
    {
        _list.Height=Math.Min(_list.MaxHeight,count*48);_list.Visibility=count==0?Visibility.Collapsed:Visibility.Visible;
        _root.Measure(new global::Windows.Foundation.Size(_width/_scale,_maxHeight));
        int height=(int)Math.Ceiling(Math.Min(_maxHeight,Math.Max(114,_root.DesiredSize.Height))*_scale);
        WindowStyler.SetBounds(Handle,_left,_top,_width,height);
        _motion.SetGeometry(_width,height,_scale,_width/(2*_scale));
    }
    private void OnKey(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==global::Windows.System.VirtualKey.Escape){Hide(immediately:false);e.Handled=true;}
        else if(e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        { if(_results.Count>0)Select(Math.Clamp(_selectedIndex+(e.Key==global::Windows.System.VirtualKey.Down?1:-1),0,_results.Count-1));e.Handled=true; }
        else if(e.Key==global::Windows.System.VirtualKey.Enter&&_selectedIndex>=0&&_selectedIndex<_results.Count){Open(_results[_selectedIndex]);e.Handled=true;}
    }
    internal void OpenApplicationForDiagnostics(AppRef app)
    {
        if(!Program.IsSmokeTest)throw new InvalidOperationException("Search launch probe requires smoke-test mode.");
        Open(new Result(app.Name,"","App",App:app));
    }
    private void Open(Result result)
    {
        if(result.App is not null)_app.Tiling.NoteLaunch(result.App);
        Hide();
        if(result.Command is not null)result.Command();
        else _=_app.Worker.Run(()=>
        {
            if(result.App is not null&&ShellLauncher.Launch(result.App)&&_usage is { } usage)
            {
                usage.Record(result.App,DateTime.UtcNow);
                try { JsonFile.Save(_app.Paths.SearchUsageFile,usage.Snapshot()); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_app.Log.Warn("Search suggestions could not be saved: "+ex.Message);}
            }
            else if(result.Path is not null)ShellLauncher.Execute(result.Path,null,null);
        });
    }
    private void InvalidateApps()=>_app.UiQueue.TryEnqueue(()=>_appIndex=null);
    private void AnimateOpening(){_motion.Play(true,Math.Clamp((int)(_app.Settings.Current.MenuAnimationMs*1.4),160,600));}
    private void ClearRows(){_rows.Children.Clear();_rowButtons.Clear();_selectedIndex=-1;}
    private void Select(int index)
    {
        _selectedIndex=index;
        for(int i=0;i<_rowButtons.Count;i++)_rowButtons[i].Background=i==index?GlassStyle.Selected:new SolidColorBrush(Colors.Transparent);
        if(index>=0&&index<_rowButtons.Count)_rowButtons[index].StartBringIntoView();
    }
    public void Dispose(){if(_disposed)return;Hide();_motion.Dispose();_disposed=true;_app.Pinned.Changed-=InvalidateApps;_app.UserTiles.Changed-=InvalidateApps;_cancel?.Dispose();_debounce.Stop();_openingTimer.Stop();_window.Close();}
}
