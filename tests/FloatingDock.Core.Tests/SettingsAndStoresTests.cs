using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fd-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SettingsTests
{
    [Fact]
    public void RoundTrip_PreservesValues_AndWritesSchemaVersion()
    {
        using var d = new TempDir();
        var path = d.File("config.json");
        using (var store = new SettingsStore(path))
        {
            store.Update(s => { s.IconSize = 64; s.Alignment = DockAlignment.Right; s.TaskbarMode = TaskbarMode.AutoHide; s.BackgroundColor = "112233"; });
            store.Flush();
        }
        var loaded = SettingsStore.Load(path);
        Assert.Equal(64, loaded.IconSize);
        Assert.Equal(DockAlignment.Right, loaded.Alignment);
        Assert.Equal(TaskbarMode.AutoHide, loaded.TaskbarMode);
        Assert.Equal(DockSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Contains("\"Alignment\": \"Right\"", File.ReadAllText(path));
    }

    [Fact]
    public void Update_RaisesChanged_WithClampedValues()
    {
        using var d = new TempDir();
        using var store = new SettingsStore(d.File("c.json"));
        DockSettings? seen = null;
        store.Changed += s => seen = s;
        store.Update(s => { s.IconSize = 5000; s.Opacity = -4; s.BackgroundColor = "zzz"; s.MagnificationRadius = 1; });
        Assert.NotNull(seen);
        Assert.Equal(128, seen!.IconSize);
        Assert.Equal(0, seen.Opacity);
        Assert.Equal("1E1E1E", seen.BackgroundColor);
        Assert.Equal(40, seen.MagnificationRadius);
    }

    [Fact]
    public void Migration_FromV0_RenamesAndConverts()
    {
        using var d = new TempDir();
        var path = d.File("config.json");
        File.WriteAllText(path, """{ "IconSize": 56, "ShowLaunchpad": false, "Opacity": 0.4, "HideTaskbar": true }""");
        var s = SettingsStore.Load(path);
        Assert.Equal(56, s.IconSize);
        Assert.False(s.ShowLaunchpadButton);
        Assert.Equal(40, s.Opacity);
        Assert.Equal(TaskbarMode.AutoHide, s.TaskbarMode);
        Assert.Equal(DockSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsACopy()
    {
        using var d = new TempDir();
        var path = d.File("config.json");
        File.WriteAllText(path, "{ this is not json");
        var s = SettingsStore.Load(path);
        Assert.Equal(new DockSettings().Normalize().IconSize, s.IconSize);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void UnknownFields_AreIgnored_AndHandEditedValuesNormalized()
    {
        using var d = new TempDir();
        var path = d.File("config.json");
        File.WriteAllText(path, $"{{ \"SchemaVersion\": {DockSettings.CurrentSchemaVersion}, \"FutureFeature\": 1, \"IconSize\": 1 }}");
        Assert.Equal(24, SettingsStore.Load(path).IconSize);
    }
}

public class AppListStoreTests
{
    private static AppRef App(string name, string exe) => new() { Name = name, ExePath = exe };

    [Fact]
    public void AddMoveRemove_PersistsOrder()
    {
        using var d = new TempDir();
        var path = d.File("pinned.json");
        var store = new AppListStore(path);
        var a = App("A", @"C:\a.exe"); var b = App("B", @"C:\b.exe"); var c = App("C", @"C:\c.exe");
        Assert.True(store.Add(a)); store.Add(b); store.Add(c);
        Assert.False(store.Add(App("A again", @"C:\A.EXE")));          // same program, case-insensitive
        store.Move(c.Key, 0);
        var reloaded = new AppListStore(path).Snapshot();
        Assert.Equal(new[] { "C", "A", "B" }, reloaded.Select(x => x.Name).ToArray());
        store.Remove(a.Key);
        Assert.Equal(new[] { "C", "B" }, new AppListStore(path).Snapshot().Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Changed_IsRaised()
    {
        using var d = new TempDir();
        var store = new AppListStore(d.File("p.json"));
        int n = 0; store.Changed += () => n++;
        store.Add(App("A", @"C:\a.exe")); store.Move("exe:c:\\a.exe", 0); store.Remove("exe:c:\\a.exe");
        Assert.Equal(2, n);   // Move to the same index is a no-op
    }
}

public class SearchTests
{
    [Fact]
    public void Search_IsCaseInsensitive_Unicode_AndRanked()
    {
        var idx = new AppSearchIndex(new[]
        {
            new AppRef { Name = "Adobe Photoshop" }, new AppRef { Name = "Paint" }, new AppRef { Name = "Фотошоп" },
            new AppRef { Name = "Café Reader" }, new AppRef { Name = "My Photos" }, new AppRef { Name = "Telegram" },
        });
        Assert.Equal(6, idx.Search("").Count);
        Assert.Equal("Paint", idx.Search("PAI")[0].Name);
        Assert.Equal(new[] { "Фотошоп" }, idx.Search("фОТ").Select(a => a.Name).ToArray());
        Assert.Equal("Café Reader", idx.Search("cafe")[0].Name);                 // diacritics ignored
        var photo = idx.Search("photo").Select(a => a.Name).ToArray();
        Assert.Equal("My Photos", photo[1] == "My Photos" ? photo[1] : photo[0]);
        Assert.Equal("Adobe Photoshop", photo[0]);                                // word-prefix before substring
        Assert.Empty(idx.Search("zzz"));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", 0x3u, 0x20u)]
    [InlineData("ctrl + shift + alt + t", 0x7u, (uint)'T')]
    [InlineData("Win+F12", 0x8u, 0x7Bu)]
    [InlineData("Alt+Left", 0x1u, 0x25u)]
    public void Parses(string text, uint mods, uint vk)
    {
        Assert.True(HotkeyParser.TryParse(text, out var m, out var k));
        Assert.Equal(mods, m); Assert.Equal(vk, k);
    }

    [Theory]
    [InlineData("")] [InlineData("Space")] [InlineData("Ctrl+")] [InlineData("Ctrl+A+B")] [InlineData("Ctrl+Banana")] [InlineData("Ctrl+F99")]
    public void RejectsInvalid(string text) => Assert.False(HotkeyParser.TryParse(text, out _, out _));
}
