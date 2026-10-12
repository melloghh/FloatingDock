using FloatingDock.Core.Models;
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
    private readonly TextBox _query=new(){BorderThickness=new Thickness(0),Background=new SolidColorBrush(Colors.Transparent),FontSize=19,Padding=new Thickness(12),Margin=new Thickness(0,0,0,10)};
    private readonly ListView _list=new(){BorderThickness=new Thickness(0),Background=new SolidColorBrush(Colors.Transparent),MaxHeight=440};
    private readonly TextBlock _status=new(){FontSize=12,Opacity=.65,Margin=new Thickness(12,10,12,0),TextWrapping=TextWrapping.Wrap};
    private readonly DispatcherQueueTimer _debounce,_openingTimer;
    private readonly GlassBackdrop _glass=new();
    private IReadOnlyList<Result> _results=Array.Empty<Result>();
    private Task<IReadOnlyList<AppRef>>? _catalog;
    private CancellationTokenSource? _cancel;
    private int _generation;
    private bool _visible,_opening,_disposed;
    private IntPtr _returnFocus;
    private int _width,_left,_top,_maxHeight;
    private double _scale=1;
    internal IntPtr Handle { get; }
    internal bool Visible=>_visible;
    internal int ResultCount=>_results.Count;
    internal Task SearchForDiagnostics(string text) { _query.Text=text;_debounce.Stop();return SearchAsync(); }

    internal CommandPaletteWindow()
    {
        var panel=new StackPanel();panel.Children.Add(_query);panel.Children.Add(_list);panel.Children.Add(_status);
        _root=new Border { Child=panel,Padding=new Thickness(16),CornerRadius=new CornerRadius(20),RequestedTheme=ElementTheme.Dark,Background=new SolidColorBrush(ColorHelper.FromArgb(180,18,21,27)),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(35,255,255,255)) };
        _window=new Window { Title="FloatingDock Search",Content=_root,SystemBackdrop=_glass };
        WindowSetup.MakeBorderless(_window);Handle=WindowSetup.Hwnd(_window);WindowStyler.MakeOverlayWindow(Handle,noActivate:false);
        _glass.SetVisible(false);
        _list.Resources["ListViewItemBackgroundSelected"]=new SolidColorBrush(ColorHelper.FromArgb(255,65,94,195));
        _list.Resources["ListViewItemBackgroundSelectedPointerOver"]=new SolidColorBrush(ColorHelper.FromArgb(255,78,108,215));
        _debounce=_app.UiQueue.CreateTimer();_debounce.IsRepeating=false;_debounce.Interval=TimeSpan.FromMilliseconds(180);_debounce.Tick+=async(_,_)=>await SearchAsync();
        _openingTimer=_app.UiQueue.CreateTimer();_openingTimer.IsRepeating=false;_openingTimer.Interval=TimeSpan.FromMilliseconds(300);_openingTimer.Tick+=(_,_)=>_opening=false;
        _query.TextChanged+=(_,_)=> { if(!_visible)return;_cancel?.Cancel();_generation++;_results=Array.Empty<Result>();_list.Items.Clear();_debounce.Stop();_debounce.Start(); };
        _root.KeyDown+=OnKey;
        _list.ItemClick+=(_,e)=> { if(e.ClickedItem is ListViewItem item&&item.Tag is Result result)Open(result); };
        _list.IsItemClickEnabled=true;
        _window.Activated+=(_,e)=> { if(_visible&&!_opening&&e.WindowActivationState==WindowActivationState.Deactivated)Hide(); };
        _query.Loaded+=(_,_)=> { if(_visible)_query.Focus(FocusState.Programmatic); };
    }
    internal void Toggle() { if(_visible)Hide();else Show(); }
    internal void Show()
    {
        if(_disposed||_visible)return;
        var mon=MonitorHelper.FromCursor()??MonitorHelper.Primary();if(mon is null)return;
        _returnFocus=NativeMethods.GetForegroundWindow();_visible=_opening=true;
        _query.PlaceholderText=L.Text("Search apps, files and folders");_query.Text="";
        _results=Array.Empty<Result>();_list.Items.Clear();_status.Text=L.Text("Enter to open · Esc to close");
        int width=(int)(Math.Min(650,(mon.Bounds.Width/mon.Scale)-40)*mon.Scale);
        _width=width;_scale=mon.Scale;_maxHeight=(int)Math.Min(580,mon.Bounds.Height/mon.Scale-60);
        _left=mon.Bounds.Left+(mon.Bounds.Width-width)/2;_top=Math.Min(mon.Bounds.Top+(int)(mon.Bounds.Height*.18),mon.Bounds.Bottom-(int)(_maxHeight*mon.Scale)-16);
        _list.MaxHeight=Math.Max(80,_maxHeight-122);ResizeSurface(0);
        _glass.SetVisible(true);_window.Activate();WindowStyler.BringToFront(Handle);
        SurfaceMotion.Enter(_root,_app.Settings.Current.MenuAnimationMs,1);
        _openingTimer.Start();_query.Focus(FocusState.Programmatic);_debounce.Stop();_debounce.Start();
    }
    internal void Hide()
    {
        if(!_visible)return;
        _visible=_opening=false;_generation++;_cancel?.Cancel();_debounce.Stop();_openingTimer.Stop();
        bool focused=NativeMethods.GetForegroundWindow()==Handle;
        _glass.SetVisible(false);_window.AppWindow.Hide();WindowStyler.Hide(Handle);
        if(focused&&NativeMethods.IsWindowVisible(_returnFocus)&&!NativeMethods.IsIconic(_returnFocus))NativeMethods.SetForegroundWindow(_returnFocus);
    }
    private async Task SearchAsync()
    {
        if(!_visible||_disposed)return;
        _cancel?.Cancel();_cancel?.Dispose();_cancel=new();var ct=_cancel.Token;int generation=++_generation;
        string query=_query.Text.Trim();var settings=_app.Settings.Current.Clone();_status.Text=L.Text("Searching…");
        try
        {
            _catalog??=_app.Worker.Run<IReadOnlyList<AppRef>>(()=>AppCatalog.Load());
            var catalog=await _catalog;if(ct.IsCancellationRequested)return;
            var apps=catalog.Concat(_app.Pinned.Snapshot()).Concat(_app.UserTiles.Snapshot()).Where(a=>!a.IsSeparator).GroupBy(a=>a.Key).Select(g=>g.First()).ToArray();
            var results=new List<Result>();
            var commands=new[]{new Result(L.Text("FloatingDock settings"),"","Command",Command:()=>_app.ShowSettings()),new Result(L.Text("Launchpad"),"","Command",Command:_app.ToggleLaunchpad)};
            results.AddRange(commands.Where(c=>query.Length==0||AppSearchIndex.Normalize(c.Name).Contains(AppSearchIndex.Normalize(query))));
            results.AddRange(new AppSearchIndex(apps).Search(query).Take(settings.SearchResultLimit).Select(a=>new Result(a.Name,a.ExePath??a.Aumid??a.ShortcutPath??"","App",App:a)));
            bool limited=false;
            if(settings.SearchFiles&&query.Length>=2)
            {
                var files=await Task.Run(()=>FileSearch.Search(query,settings.SearchFolders,settings.SearchResultLimit,ct),ct);
                limited=files.UsedFallback||files.Limited;
                results.AddRange(files.Hits.Select(h=>new Result(h.Name,h.Path,h.IsFolder?"Folder":"File",Path:h.Path)));
            }
            if(_disposed||!_visible||generation!=_generation||ct.IsCancellationRequested)return;
            // Leave room for files when many applications also match.
            var appResults=results.Where(r=>r.Path is null).ToArray();var fileResults=results.Where(r=>r.Path is not null).ToArray();
            int appLimit=fileResults.Length>0?Math.Max(3,settings.SearchResultLimit/2):settings.SearchResultLimit;
            _results=appResults.Take(appLimit).Concat(fileResults.Take(settings.SearchResultLimit-Math.Min(appLimit,appResults.Length))).ToArray();
            _list.Items.Clear();
            foreach(var result in _results)
            {
                var row=new Grid { ColumnSpacing=12,Padding=new Thickness(8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(32) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                if(result.App is not null){var icon=new IconView { Width=28,Height=28,VerticalAlignment=VerticalAlignment.Center };icon.Bind(result.App,GlyphKind.App,48);row.Children.Add(icon);}
                else row.Children.Add(new FontIcon { Glyph=result.Kind=="Folder"?"\uE8B7":result.Kind=="File"?"\uE8A5":"\uE713",FontSize=22,Foreground=new SolidColorBrush(ColorHelper.FromArgb(255,185,205,255)) });
                var label=new StackPanel { Spacing=3 };label.Children.Add(new TextBlock { Text=result.Name,FontSize=14,TextTrimming=TextTrimming.CharacterEllipsis });
                if(result.Detail.Length>0)label.Children.Add(new TextBlock { Text=result.Detail,FontSize=11,Opacity=.6,TextTrimming=TextTrimming.CharacterEllipsis });
                Grid.SetColumn(label,1);row.Children.Add(label);var kind=new TextBlock { Text=L.Text(result.Kind),FontSize=11,Opacity=.7,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(kind,2);row.Children.Add(kind);
                _list.Items.Add(new ListViewItem { Content=row,Tag=result,HorizontalContentAlignment=HorizontalAlignment.Stretch,CornerRadius=new CornerRadius(9) });
            }
            if(_results.Count>0)_list.SelectedIndex=0;
            _status.Text=L.Text(_results.Count==0?"No results":limited?"Includes results from a limited folder scan.":"Enter to open · Esc to close");
            ResizeSurface(_results.Count);
        }
        catch(OperationCanceledException){}
        catch(Exception ex) { if(generation==_generation&&_visible)_status.Text=L.Text("Search could not finish. Try again.");_catalog=null;_app.Log.Warn("Palette search failed: "+ex.Message); }
    }
    private void ResizeSurface(int count)
    {
        int height=(int)(Math.Min(_maxHeight,Math.Max(150,122+count*62))*_scale);
        WindowStyler.SetBounds(Handle,_left,_top,_width,height);
        WindowStyler.SetRoundedRegion(Handle,_width,height,(int)(20*_scale));
    }
    private void OnKey(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==global::Windows.System.VirtualKey.Escape){Hide();e.Handled=true;}
        else if(e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        { if(_results.Count>0)_list.SelectedIndex=Math.Clamp(_list.SelectedIndex+(e.Key==global::Windows.System.VirtualKey.Down?1:-1),0,_results.Count-1);e.Handled=true; }
        else if(e.Key==global::Windows.System.VirtualKey.Enter&&_list.SelectedIndex>=0&&_list.SelectedIndex<_results.Count){Open(_results[_list.SelectedIndex]);e.Handled=true;}
    }
    private void Open(Result result)
    {
        Hide();
        if(result.Command is not null)result.Command();
        else _=_app.Worker.Run(()=> { if(result.App is not null)ShellLauncher.Launch(result.App);else if(result.Path is not null)ShellLauncher.Execute(result.Path,null,null); });
    }
    public void Dispose(){if(_disposed)return;Hide();_disposed=true;_cancel?.Dispose();_debounce.Stop();_openingTimer.Stop();_window.Close();}
}
