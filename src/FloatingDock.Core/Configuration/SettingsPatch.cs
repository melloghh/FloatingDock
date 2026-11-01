using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FloatingDock.Core.Configuration;

/// <summary>A patch named a setting that does not exist, is read-only, or carried a value of the wrong type.</summary>
public sealed class SettingsPatchException(string key, string message) : Exception(message)
{
    public string Key { get; } = key;
}

/// <summary>
/// Applies a partial update (<c>{"IconSize": 40}</c>) to <see cref="DockSettings"/>. Keys are the property names stored in config.json,
/// compared case-insensitively. Used by the engine so that any UI can change settings without knowing the whole object.
/// </summary>
public static class SettingsPatch
{
    private static readonly PropertyInfo[] Props = typeof(DockSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true })
        .ToArray();
    private static readonly Dictionary<string, PropertyInfo> ByName = Props.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a normalized copy of <paramref name="current"/> with <paramref name="patch"/> applied; <paramref name="current"/> is not modified.</summary>
    public static DockSettings Apply(DockSettings current, JsonObject patch)
    {
        var node = JsonSerializer.SerializeToNode(current, JsonFile.Options) as JsonObject
            ?? throw new InvalidOperationException("Settings did not serialize to an object.");
        foreach (var (key, value) in patch)
        {
            if (!ByName.TryGetValue(key, out var prop)) throw new SettingsPatchException(key, "Unknown setting.");
            if (prop.Name == nameof(DockSettings.SchemaVersion)) throw new SettingsPatchException(key, "This setting is read-only.");
            try { JsonSerializer.Deserialize(value, prop.PropertyType, JsonFile.Options); }
            catch (JsonException ex) { throw new SettingsPatchException(key, "Wrong value type: " + ex.Message); }
            node[prop.Name] = value?.DeepClone();
        }
        try { return (node.Deserialize<DockSettings>(JsonFile.Options) ?? new DockSettings()).Normalize(); }
        catch (JsonException ex) { throw new SettingsPatchException("", ex.Message); }
    }

    /// <summary>Copies every setting from <paramref name="source"/> into <paramref name="target"/> (for <see cref="SettingsStore.Update"/>).</summary>
    public static void CopyInto(DockSettings source, DockSettings target)
    {
        foreach (var p in Props)
        {
            var v = p.GetValue(source);
            if (v is Dictionary<string, string> d) v = new Dictionary<string, string>(d, StringComparer.Ordinal);
            p.SetValue(target, v);
        }
    }
}
