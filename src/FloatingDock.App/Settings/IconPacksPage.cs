using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FloatingDock.UI.Settings;

internal sealed partial class SettingsWindow
{
    internal ComboBox? IconPackComboForDiagnostics { get; private set; }
    internal Button? ApplyPackForDiagnostics { get; private set; }
    private UIElement IconPacksPage()
    {
        var s = S;
        var packs = _app.Packs.ListPacks();
        var combo = new ComboBox { Header = "Active icon pack", MinWidth = 300 };
        IconPackComboForDiagnostics=combo;
        foreach (var p in packs) combo.Items.Add(p.Id == IconPackService.DefaultPackId ? "Системные иконки" : p.Id == IconPackService.CustomPackId ? $"Мои иконки  ({p.IconCount})" : $"{p.Name}  ({p.IconCount} icons)");
        int sel = packs.ToList().FindIndex(p => string.Equals(p.Id, s.ActiveIconPack, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(p.Directory), s.ActiveIconPack, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = Math.Max(0, sel);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= packs.Count) return;
            var folder = Path.GetFileName(packs[combo.SelectedIndex].Directory);
            _app.Settings.Update(d => { d.ActiveIconPack = folder; d.UseIconPack = true; });
            Refresh();
        };

        var info = Ui.Note(sel >= 0 && sel < packs.Count ? Describe(packs[sel]) : "");
        var apply = Ui.Action("Применить пакет к приложениям дока", () =>
        {
            var pack=packs.ElementAtOrDefault(combo.SelectedIndex);
            if(pack is null)return;
            _app.Settings.Update(d=> { d.ActiveIconPack=Path.GetFileName(pack.Directory);d.UseIconPack=true; });
            foreach(var app in _app.Pinned.Snapshot().Concat(_app.UserTiles.Snapshot()))
                if(pack.Find(app.IconNames(),S.PreferDarkVariants) is not null)_app.Overrides.ResetForApp(app);
            _app.Icons.Invalidate();Refresh();
        });
        ApplyPackForDiagnostics=apply;

        var import = Ui.Action("Import ZIP…", async () => await ImportPackAsync());
        var export = Ui.Action("Export active pack…", async () => await ExportPackAsync(packs.ElementAtOrDefault(Math.Max(0, combo.SelectedIndex))));
        var delete = Ui.Action("Delete pack", async () =>
        {
            var p = packs.ElementAtOrDefault(combo.SelectedIndex);
            if (p is null) return;
            if (p.IsBuiltIn) { await Message("Icon packs", "Это папка системных или ваших отдельных иконок. Импортированные пакеты можно удалить."); return; }
            var r = await Dialog("Delete icon pack", new TextBlock { Text = $"Delete “{p.Name}” and its {p.IconCount} icons from disk?", TextWrapping = TextWrapping.Wrap }, "Delete");
            if (r != ContentDialogResult.Primary) return;
            _app.Packs.Delete(Path.GetFileName(p.Directory));
            _app.Settings.Update(d => d.ActiveIconPack = "Default");
            _app.Resolver.Reload(); _app.Icons.Invalidate();
            Refresh();
        });
        var open = Ui.Action("Open icon folder", () => ShellLauncher.OpenFolder(_app.Packs.RootDirectory));
        var reload = Ui.Action("Reload", () => { _app.Resolver.Reload(); _app.Icons.Invalidate(); Refresh(); });

        return Page("Icon Packs",
            Ui.Toggle("Use icon packs", s.UseIconPack, (d, v) => d.UseIconPack = v),
            Ui.Toggle("Prefer dark variants (name_dark.png)", s.PreferDarkVariants, (d, v) => d.PreferDarkVariants = v),
            Ui.Note("Individual icons set with Change icon take priority. Use Reset icon on an app to let the selected pack choose its icon automatically."),
            Ui.Section("Pack", combo, info, apply, Ui.Note("Применение заменяет индивидуальные иконки только для приложений, найденных в пакете."), Ui.Row(import, export, delete), Ui.Row(open, reload),
                Ui.Note("Drop PNG / ICO files named after the app (e.g. Firefox.png, Visual Studio Code.png) into the Custom folder: they are picked up automatically. The same icons are used by the Dock and the Launchpad.")),
            AppIconsSection());
    }

    private static string Describe(IconPack p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Version)) parts.Add("v" + p.Version);
        if (!string.IsNullOrWhiteSpace(p.Description)) parts.Add(p.Description);
        if (p.Warnings.Count > 0) parts.Add($"{p.Warnings.Count} warning(s): {p.Warnings[0]}");
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }

    private async Task ImportPackAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ZipPreview preview;
        try { preview = await Task.Run(() => _app.Packs.Inspect(file.Path)); }
        catch (IconPackImportException ex) { await Message("Cannot import", ex.Message); return; }
        catch (Exception ex) { await Message("Cannot import", ex.Message); return; }

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = $"{preview.Name}  v{preview.Version}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(preview.Description)) panel.Children.Add(new TextBlock { Text = preview.Description, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{preview.IconCount} icons, e.g. {string.Join(", ", preview.IconNames.Take(8))}", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        if (preview.Problems.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Problems:\n" + string.Join("\n", preview.Problems.Take(6)), TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed) });
        if (await Dialog("Import icon pack", panel, "Import") != ContentDialogResult.Primary) return;
        try
        {
            var pack = await Task.Run(() => _app.Packs.Import(file.Path, overwrite: true));
            var folder = Path.GetFileName(pack.Directory);
            _app.Settings.Update(d => { d.ActiveIconPack = folder; d.UseIconPack = true; });
            _app.Resolver.Reload(); _app.Icons.Invalidate();
            Refresh();
        }
        catch (Exception ex) { await Message("Import failed", ex.Message); }
    }

    private async Task ExportPackAsync(IconPack? pack)
    {
        if (pack is null) return;
        var picker = new FileSavePicker { SuggestedFileName = pack.Name };
        picker.FileTypeChoices.Add("Icon pack (ZIP)", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var folder = Path.GetFileName(pack.Directory);
            await Task.Run(() => _app.Packs.Export(folder, file.Path));
            await Message("Export", $"Saved to {file.Path}");
        }
        catch (Exception ex) { await Message("Export failed", ex.Message); }
    }

    /// <summary>Per-app overrides: a custom PNG/ICO beats every pack.</summary>
    private UIElement AppIconsSection()
    {
        var list = new StackPanel { Spacing = 4 };
        void Fill()
        {
            list.Children.Clear();
            var apps = _app.Pinned.Snapshot().Concat(_app.UserTiles.Snapshot()).GroupBy(a => a.Key).Select(g => g.First()).ToList();
            if (apps.Count == 0) list.Children.Add(Ui.Note("Pin apps to the Dock or add Launchpad tiles to set their icons here."));
            foreach (var app in apps)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new IconView { Width = 28, Height = 28 };
                icon.Bind(app, GlyphKind.App, 56);
                row.Children.Add(icon);
                var name = new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1); row.Children.Add(name);
                var change = new Button { Content = "Change…" };
                change.Click += async (_, _) => await WindowSetup.PickIconAsync(_hwnd, app);
                Grid.SetColumn(change, 2); row.Children.Add(change);
                var reset = new Button { Content = "Reset", IsEnabled = _app.Overrides.Has(app.Key) };
                reset.Click += (_, _) => _app.TryResetIcon(app.Key);
                Grid.SetColumn(reset, 3); row.Children.Add(reset);
                list.Children.Add(row);
            }
        }
        Fill();
        void Changed() => _app.UiQueue.TryEnqueue(Fill);
        _app.Overrides.Changed += Changed;
        _pageCleanup.Add(() => _app.Overrides.Changed -= Changed);
        return Ui.Section("Icons of individual apps", list);
    }
}
