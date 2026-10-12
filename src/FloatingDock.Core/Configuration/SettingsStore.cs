using System.Text.Json;
using System.Text.Json.Nodes;

namespace FloatingDock.Core.Configuration;

/// <summary>
/// Loads, migrates, mutates and saves <see cref="DockSettings"/>. Changes are applied immediately (event) and persisted with a short debounce.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private DockSettings _current;
    private bool _dirty;
    private bool _disposed;

    public SettingsStore(string path)
    {
        _path = path;
        var loaded = JsonFile.Load(path, Parse);
        LoadStatus = loaded.Status;
        LoadDetail = loaded.Detail;
        _current = loaded.Value ?? new DockSettings().Normalize();
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised when the settings could not be written (the change stays in memory and the save is retried every 5 s).</summary>
    public event Action<Exception>? SaveFailed;
    public LoadStatus LoadStatus { get; }
    public string? LoadDetail { get; }

    public DockSettings Current { get { lock (_gate) return _current; } }

    /// <summary>Raised after every change with a fresh immutable-by-convention snapshot.</summary>
    public event Action<DockSettings>? Changed;

    public void Update(Action<DockSettings> mutate)
    {
        DockSettings snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = _current.Clone();
            mutate(next);
            next.Normalize();
            _current = next;
            snapshot = next;
            _dirty = true;
            _saveTimer.Change(400, Timeout.Infinite);
        }
        Changed?.Invoke(snapshot);
    }

    public void Flush()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                if (LoadStatus == LoadStatus.Corrupt) throw new IOException("The settings file could not be read. Restore the file or its backup before saving changes.");
                JsonFile.Save(_path, _current);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                if (!_disposed) _saveTimer.Change(5000, Timeout.Infinite);
            }
        }
        if (failure is not null) SaveFailed?.Invoke(failure);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Dispose();
        }
        Flush();
    }

    /// <summary>Loads the settings; a damaged file is kept as *.corrupt and the last good copy (*.bak) is used before falling back to defaults.</summary>
    public static DockSettings Load(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return new DockSettings().Normalize();
        return JsonFile.Load(path, Parse).Value ?? new DockSettings().Normalize();
    }

    private static DockSettings Parse(string text)
    {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (node is null) throw new JsonException("root is not an object");
            var version = node["SchemaVersion"]?.GetValue<int>() ?? node["schemaVersion"]?.GetValue<int>() ?? 0;
            if (version > DockSettings.CurrentSchemaVersion) throw new NotSupportedException("The settings were written by a newer version.");
            if (version < DockSettings.CurrentSchemaVersion) node = SettingsMigrator.Migrate(node, version);
            var settings = node.Deserialize<DockSettings>(JsonFile.Options) ?? new DockSettings();
            return settings.Normalize();
    }
}

/// <summary>Step-wise schema migration; every step only touches the keys it knows about.</summary>
public static class SettingsMigrator
{
    public static JsonObject Migrate(JsonObject node, int fromVersion)
    {
        if (fromVersion < 1)
        {
            // v0 (pre-release) stored the Launchpad button flag as ShowLaunchpad and the opacity as 0..1.
            Rename(node, "ShowLaunchpad", "ShowLaunchpadButton");
            if (node["Opacity"] is JsonValue v && v.TryGetValue<double>(out var d) && d is > 0 and <= 1) node["Opacity"] = (int)Math.Round(d * 100);
        }
        if (fromVersion < 2)
        {
            // v2 replaced the boolean HideTaskbar with the TaskbarMode enum.
            if (node["HideTaskbar"] is JsonValue h && h.TryGetValue<bool>(out var hide))
            {
                node["TaskbarMode"] = hide ? nameof(TaskbarMode.AutoHide) : nameof(TaskbarMode.Leave);
                node.Remove("HideTaskbar");
            }
        }
        node["SchemaVersion"] = DockSettings.CurrentSchemaVersion;
        return node;
    }

    private static void Rename(JsonObject node, string from, string to)
    {
        if (node.TryGetPropertyValue(from, out var value) && !node.ContainsKey(to))
        {
            node.Remove(from);
            node[to] = value;
        }
    }
}
