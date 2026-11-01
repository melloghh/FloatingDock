using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class SettingsPatchTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Applies_values_and_leaves_the_original_untouched()
    {
        var current = new DockSettings { IconSize = 48 };
        var next = SettingsPatch.Apply(current, Obj("""{"IconSize":40,"Edge":"Left","AccentSeed":"5F6368"}"""));
        Assert.Equal(40, next.IconSize);
        Assert.Equal(DockEdge.Left, next.Edge);
        Assert.Equal("5F6368", next.AccentSeed);
        Assert.Equal(48, current.IconSize);
        Assert.Equal(DockEdge.Bottom, current.Edge);
    }

    [Fact]
    public void Keys_are_case_insensitive_and_values_are_clamped()
    {
        var next = SettingsPatch.Apply(new DockSettings(), Obj("""{"iconsize":9999}"""));
        Assert.Equal(128, next.IconSize);
    }

    [Theory]
    [InlineData("""{"NoSuchSetting":1}""", "NoSuchSetting")]
    [InlineData("""{"SchemaVersion":7}""", "SchemaVersion")]
    [InlineData("""{"IconSize":"big"}""", "IconSize")]
    [InlineData("""{"Edge":"Top"}""", "Edge")]
    public void Rejects_unknown_read_only_and_wrongly_typed_values(string patch, string key)
    {
        var ex = Assert.Throws<SettingsPatchException>(() => SettingsPatch.Apply(new DockSettings(), Obj(patch)));
        Assert.Equal(key, ex.Key);
    }

    [Fact]
    public void A_rejected_patch_changes_nothing()
    {
        var current = new DockSettings { IconSize = 48 };
        Assert.Throws<SettingsPatchException>(() => SettingsPatch.Apply(current, Obj("""{"IconSize":30,"Edge":"Top"}""")));
        Assert.Equal(48, current.IconSize);
    }

    [Fact]
    public void CopyInto_copies_every_setting_including_the_keybinding_dictionary()
    {
        var source = new DockSettings { IconSize = 36, TilingKeybindings = new() { ["focus-left"] = "Alt+H" } };
        var target = new DockSettings();
        SettingsPatch.CopyInto(source, target);
        Assert.Equal(36, target.IconSize);
        Assert.Equal("Alt+H", target.TilingKeybindings["focus-left"]);
        Assert.NotSame(source.TilingKeybindings, target.TilingKeybindings);
    }
}
