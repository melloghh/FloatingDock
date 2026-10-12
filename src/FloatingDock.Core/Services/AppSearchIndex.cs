using System.Globalization;
using System.Text;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

/// <summary>Case-insensitive, Unicode-aware, diacritic-insensitive search over app names. The index is built once; each keystroke is O(n) on short strings.</summary>
public sealed class AppSearchIndex
{
    private readonly List<(AppRef App, string Norm)> _items = new();

    public AppSearchIndex(IEnumerable<AppRef> apps) => Rebuild(apps);

    public int Count => _items.Count;

    public void Rebuild(IEnumerable<AppRef> apps)
    {
        _items.Clear();
        foreach (var a in apps) _items.Add((a, Normalize(a.Name)));
    }

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Empty query returns everything in the original order; otherwise prefix matches, then word-prefix, then substring.</summary>
    public IReadOnlyList<AppRef> Search(string? query)
    {
        var q = Normalize(query).Trim();
        if (q.Length == 0) return _items.Select(i => i.App).ToList();
        var scored = new List<(int Score, int Order, AppRef App)>();
        for (int i = 0; i < _items.Count; i++)
        {
            var (app, norm) = _items[i];
            int score;
            if (norm.StartsWith(q, StringComparison.Ordinal)) score = 0;
            else if (norm.Contains(' ' + q, StringComparison.Ordinal)) score = 1;
            else if (norm.Contains(q, StringComparison.Ordinal)) score = 2;
            else continue;
            scored.Add((score, i, app));
        }
        return scored.OrderBy(s => s.Score).ThenBy(s => s.Order).Select(s => s.App).ToList();
    }
}
