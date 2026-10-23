using System.Text.Json;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

/// <summary>Only apps successfully launched from search contribute to suggestions. Queries and file paths are not stored.</summary>
public sealed class SearchUsage
{
    private const int MaxEntries=100,MaxCount=1000000;
    public sealed record Entry(int Count,DateTime LastUsed);
    private readonly Dictionary<string,Entry> _entries;
    private readonly object _gate=new();
    public bool HasEntries { get {lock(_gate)return _entries.Count>0;} }
    public SearchUsage(Dictionary<string,Entry>? entries=null)
    {
        _entries=new(StringComparer.OrdinalIgnoreCase);
        if(entries is null)return;
        foreach(var (key,entry) in entries.Where(p=>!string.IsNullOrWhiteSpace(p.Key)&&p.Value is {Count:>0})
            .OrderByDescending(p=>p.Value.LastUsed))
        {
            if(_entries.TryGetValue(key,out var existing))
                _entries[key]=new(Math.Max(existing.Count,Math.Min(MaxCount,entry.Count)),existing.LastUsed);
            else if(_entries.Count<MaxEntries)_entries[key]=entry with {Count=Math.Min(MaxCount,entry.Count)};
        }
    }
    public static SearchUsage Load(string path)=>new(JsonFile.Load(path,s=>JsonSerializer.Deserialize<Dictionary<string,Entry>>(s,JsonFile.Options)??throw new JsonException()).Value);
    public void Record(AppRef app,DateTime now)
    {
        lock(_gate)
        {
        _entries[app.Key]=new(Math.Min(MaxCount,(_entries.GetValueOrDefault(app.Key)?.Count??0)+1),now);
        if(_entries.Count>MaxEntries)foreach(var key in _entries.OrderByDescending(p=>p.Value.LastUsed).Skip(MaxEntries).Select(p=>p.Key).ToArray())_entries.Remove(key);
        }
    }
    public Dictionary<string,Entry> Snapshot(){lock(_gate)return new(_entries,StringComparer.OrdinalIgnoreCase);}
    public IReadOnlyList<AppRef> Suggestions(IEnumerable<AppRef> apps,int limit=8)
    {lock(_gate)return apps.Where(a=>_entries.ContainsKey(a.Key)&&PaletteAppPolicy.Include(a)).OrderByDescending(a=>_entries[a.Key].Count).ThenByDescending(a=>_entries[a.Key].LastUsed).Take(limit).ToArray();}
}
