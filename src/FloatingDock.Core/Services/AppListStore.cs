using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

/// <summary>
/// Ordered, persisted list of apps: the dock's pinned apps and the Launchpad's user tiles share this implementation. Persisted in FloatingDock's own data folder (never in Windows' taskbar registry).
/// Every change is saved atomically; if saving fails the change is rolled back (the list in memory always equals the list on disk), <see cref="LastError"/> is set and
/// <see cref="SaveFailed"/> is raised so the UI can tell the user.
/// </summary>
public sealed class AppListStore
{
    public const int CurrentSchemaVersion = 2;
    private sealed class FileModel { public int SchemaVersion { get; set; } = CurrentSchemaVersion; public List<AppRef> Apps { get; set; } = new(); }

    private readonly string _path;
    private readonly object _gate = new();
    private List<AppRef> _apps;

    public AppListStore(string path)
    {
        _path = path;
        var result = JsonFile.Load(path, Parse);
        LoadStatus = result.Status;
        LoadDetail = result.Detail;
        _apps = Dedupe(result.Value ?? new List<AppRef>());
    }

    /// <summary>How the file was loaded. Corrupt: a damaged file was found and kept as *.corrupt (the list starts empty).</summary>
    public LoadStatus LoadStatus { get; }
    public string? LoadDetail { get; }
    public string? LastError { get; private set; }

    public event Action? Changed;
    /// <summary>Raised (on the calling thread, outside the lock) when a change could not be saved and was rolled back.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>Reads the file; understands the current schema, the legacy bare array and a missing SchemaVersion. A newer schema is refused (never overwritten silently).</summary>
    internal static List<AppRef> Parse(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new JsonException("The file is empty.");
        JsonNode? apps;
        if (node is JsonArray) apps = node;                                   // schema 0: a plain array
        else if (node is JsonObject o)
        {
            int version = o["SchemaVersion"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
            if (version > CurrentSchemaVersion) throw new NotSupportedException($"The list was written by a newer version (schema {version}).");
            apps = o["Apps"];
        }
        else throw new JsonException("Unexpected file content.");
        if (apps is not JsonArray) throw new JsonException("The Apps array is missing or invalid.");
        var list = apps.Deserialize<List<AppRef>>(JsonFile.Options) ?? throw new JsonException("The Apps array is invalid.");
        return list.Where(a => a is not null && !string.IsNullOrEmpty(a.Key)).ToList();
    }

    public IReadOnlyList<AppRef> Snapshot() { lock (_gate) return _apps.Select(a => a.Clone()).ToList(); }
    public bool Contains(string key) { lock (_gate) return _apps.Any(a => a.Key == key); }

    /// <returns>True when the app was added and saved. False when it was already in the list or saving failed (see <see cref="LastError"/>).</returns>
    public bool Add(AppRef app, int? index = null)
    {
        Exception? failure;
        lock (_gate)
        {
            if (_apps.Any(a => a.Key == app.Key)) return false;
            var next = new List<AppRef>(_apps);
            var copy = app.Clone();
            if (index is int i && i >= 0 && i <= next.Count) next.Insert(i, copy); else next.Add(copy);
            failure = Commit(next);
        }
        return Finish(failure);
    }

    public bool Remove(string key)
    {
        Exception? failure;
        lock (_gate)
        {
            var next = _apps.Where(a => a.Key != key).ToList();
            if (next.Count == _apps.Count) return false;
            failure = Commit(next);
        }
        return Finish(failure);
    }

    /// <summary>Moves the app so that it ends up at <paramref name="newIndex"/>.</summary>
    public bool Move(string key, int newIndex)
    {
        Exception? failure;
        lock (_gate)
        {
            var from = _apps.FindIndex(a => a.Key == key);
            if (from < 0) return false;
            newIndex = Math.Clamp(newIndex, 0, _apps.Count - 1);
            if (from == newIndex) return false;
            var next = new List<AppRef>(_apps);
            var item = next[from];
            next.RemoveAt(from);
            next.Insert(newIndex, item);
            failure = Commit(next);
        }
        return Finish(failure);
    }

    /// <summary>Moves within the visible app list, keeping persisted divider slots in place.</summary>
    public bool MoveApplication(string key,int newIndex)
    {
        Exception? failure;
        lock(_gate)
        {
            var apps=_apps.Where(a=>!a.IsSeparator).ToList();
            int from=apps.FindIndex(a=>a.Key==key);
            if(from<0)return false;
            newIndex=Math.Clamp(newIndex,0,apps.Count-1);
            if(from==newIndex)return false;
            var item=apps[from];apps.RemoveAt(from);apps.Insert(newIndex,item);
            var next=new List<AppRef>(_apps);int appIndex=0;
            for(int i=0;i<next.Count;i++)if(!next[i].IsSeparator)next[i]=apps[appIndex++];
            failure=Commit(next);
        }
        return Finish(failure);
    }

    public bool ReplaceAll(IEnumerable<AppRef> apps)
    {
        Exception? failure;
        lock (_gate) failure = Commit(Dedupe(apps.Select(a => a.Clone()).ToList()));
        return Finish(failure);
    }

    private static List<AppRef> Dedupe(List<AppRef> list)
    {
        var seen = new HashSet<string>();
        return list.Where(a => seen.Add(a.Key)).ToList();
    }

    /// <summary>Saves <paramref name="next"/>; it becomes the live list only when the save succeeded. Must be called inside the lock.</summary>
    private Exception? Commit(List<AppRef> next)
    {
        try
        {
            if (LoadStatus == LoadStatus.Corrupt)
                throw new IOException("The app list could not be read. Restore the JSON file or its backup before saving changes.");
            JsonFile.Save(_path, new FileModel { Apps = next });
            _apps = next;
            LastError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return ex;
        }
    }

    private bool Finish(Exception? failure)
    {
        if (failure is not null) { SaveFailed?.Invoke(failure); return false; }
        Changed?.Invoke();
        return true;
    }
}
