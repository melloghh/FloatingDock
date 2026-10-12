using System.Text.Json;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

/// <summary>Only apps successfully launched from search contribute to suggestions. Queries and file paths are not stored.</summary>
public sealed class SearchUsage
{
    public sealed record Entry(int Count,DateTime LastUsed);
    private readonly Dictionary<string,Entry> _entries;
    private readonly object _gate=new();
    public bool HasEntries { get {lock(_gate)return _entries.Count>0;} }
    public SearchUsage(Dictionary<string,Entry>? entries=null)=>_entries=entries??new(StringComparer.OrdinalIgnoreCase);
    public static SearchUsage Load(string path)=>new(JsonFile.Load(path,s=>JsonSerializer.Deserialize<Dictionary<string,Entry>>(s,JsonFile.Options)??throw new JsonException()).Value);
    public void Record(AppRef app,DateTime now)
    {
        lock(_gate)
        {
        _entries[app.Key]=new(Math.Min(1000000,_entries.GetValueOrDefault(app.Key)?.Count+1??1),now);
        if(_entries.Count>100)foreach(var key in _entries.OrderByDescending(p=>p.Value.LastUsed).Skip(100).Select(p=>p.Key).ToArray())_entries.Remove(key);
        }
    }
    public Dictionary<string,Entry> Snapshot(){lock(_gate)return new(_entries);}
    public IReadOnlyList<AppRef> Suggestions(IEnumerable<AppRef> apps,int limit=8)
    {lock(_gate)return apps.Where(a=>_entries.ContainsKey(a.Key)&&PaletteAppPolicy.Include(a)).OrderByDescending(a=>_entries[a.Key].Count).ThenByDescending(a=>_entries[a.Key].LastUsed).Take(limit).ToArray();}
}
