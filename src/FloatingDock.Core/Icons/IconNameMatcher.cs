using System.Globalization;
using System.Text;

namespace FloatingDock.Core.Icons;

public enum MatchRule { None, Exact, WithoutNoise, SamePronunciation, Contains, OneLetterDifference, Override }

/// <summary>
/// Name normalisation used to connect an app (exe name, AUMID parts, display name, shortcut name, aliases) with a file or manifest key of an icon pack.
/// Rules are applied in order; the first rule that finds an entry wins.
/// </summary>
public static class IconNameMatcher
{
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "adobe", "microsoft", "google", "app", "apps", "desktop", "pro", "cc", "cs", "x64", "x86", "win", "windows10", "windows11", "for", "the", "inc", "ltd", "official", "icon", "flat", "dark", "light", "mask",
    };

    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "desktop", "windows", "file", "files", "launcher", "setup", "update", "updater", "installer", "helper", "host", "service", "client", "main", "default", "program", "application",
    };

    // well-known real-name aliases: key = normalised name, value = other names of the same program
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = new[] { "edge", "microsoft edge" },
        ["edge"] = new[] { "msedge" },
        ["calculatorapp"] = new[] { "calculator", "calc" },
        ["calculator"] = new[] { "calculatorapp", "calc" },
        ["calc"] = new[] { "calculator" },
        ["windowsterminal"] = new[] { "terminal", "wt" },
        ["wt"] = new[] { "terminal", "windows terminal" },
        ["code"] = new[] { "visual studio code", "vscode" },
        ["explorer"] = new[] { "file explorer", "files" },
        ["winword"] = new[] { "word" },
        ["excel"] = new[] { "microsoft excel" },
        ["powerpnt"] = new[] { "powerpoint" },
        ["outlook"] = new[] { "microsoft outlook" },
        ["ms-teams"] = new[] { "teams", "msteams" },
        ["msteams"] = new[] { "teams" },
        ["notepad"] = new[] { "blocnotes" },
        ["mspaint"] = new[] { "paint" },
        ["systemsettings"] = new[] { "settings" },
        ["photoshop"] = new[] { "adobe photoshop" },
        ["afterfx"] = new[] { "after effects", "adobe after effects" },
        ["discord"] = new[] { "discordptb", "discordcanary" },
        ["chrome"] = new[] { "google chrome" },
        ["firefox"] = new[] { "mozilla firefox" },
        ["steamwebhelper"] = new[] { "steam" },
    };

    /// <summary>lower-case letters and digits only: "Ms-Teams.png" -> "msteams".</summary>
    public static string Normalize(string name)
    {
        var s = StripExtension(name);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public static string StripExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Length is >= 2 and <= 5 && ext.Skip(1).All(char.IsLetterOrDigit) && IsKnownExtension(ext) ? name[..^ext.Length] : name;
    }

    private static bool IsKnownExtension(string ext) => ext.ToLowerInvariant() is ".png" or ".ico" or ".exe" or ".lnk" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    /// <summary>Removes vendor / edition words, versions and years: "Adobe Photoshop 2024" -> "photoshop".</summary>
    public static string WithoutNoise(string name)
    {
        var tokens = Tokenize(StripExtension(name));
        var kept = new List<string>();
        foreach (var t in tokens)
        {
            if (NoiseWords.Contains(t)) continue;
            if (t.All(char.IsDigit)) continue;                                       // 2024, 11
            if (t.Length > 1 && t[0] == 'v' && t.Skip(1).All(c => char.IsDigit(c) || c == '.')) continue;   // v2
            kept.Add(t);
        }
        var s = string.Concat(kept);
        return s.Length == 0 ? Normalize(name) : s;
    }

    private static List<string> Tokenize(string s)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (var ch0 in s.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch0) == UnicodeCategory.NonSpacingMark) continue;
            if (!char.IsLetterOrDigit(ch0)) { Flush(); prev = '\0'; continue; }
            // camelCase boundary: "AfterEffects" -> after, effects ; letter/digit boundary: "cc2024"
            if (sb.Length > 0 && ((char.IsUpper(ch0) && char.IsLower(prev)) || (char.IsDigit(ch0) != char.IsDigit(prev)))) Flush();
            sb.Append(char.ToLowerInvariant(ch0));
            prev = ch0;
        }
        Flush();
        return res;
        void Flush() { if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); } }
    }

    private static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "i",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f",
        ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "i", ['ь'] = "", ['э'] = "e", ['ю'] = "iu", ['я'] = "ia",
        ['і'] = "i", ['ї'] = "i", ['є'] = "e", ['ґ'] = "g",
    };

    public static bool HasCyrillic(string s) => s.Any(c => c is >= '\u0400' and <= '\u04FF');

    /// <summary>Rough phonetic key shared by Latin and Cyrillic spellings: "Photoshop", "Фотошоп" -> "fotoshop".</summary>
    public static string Phonetic(string name)
    {
        var s = WithoutNoise(name).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(Cyr.TryGetValue(ch, out var lat) ? lat : ch.ToString());
        var t = sb.ToString();
        t = t.Replace("ph", "f").Replace("ck", "k").Replace("kh", "h").Replace("ts", "ts").Replace("q", "k").Replace("x", "ks").Replace("w", "v").Replace("y", "i").Replace("j", "i");
        var o = new StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == 'c') c = i + 1 < t.Length && t[i + 1] is 'e' or 'i' ? 's' : 'k';
            if (o.Length > 0 && o[^1] == c) continue;   // double letters
            o.Append(c);
        }
        return o.ToString();
    }

    public static bool IsTooGeneric(string normalized) => normalized.Length < 3 || GenericNames.Contains(normalized);

    public static IEnumerable<string> Expand(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n) || !seen.Add(n)) continue;
            yield return n;
            if (Aliases.TryGetValue(Normalize(n), out var alts))
                foreach (var a in alts) if (seen.Add(a)) yield return a;
        }
    }

    public static bool ContainsMatch(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 4 && shorter.Length * 2 >= longer.Length && longer.Contains(shorter, StringComparison.Ordinal);
    }

    public static bool OneLetterApart(string a, string b)
    {
        if (a.Length < 5 || b.Length < 5 || Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, diff = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++diff > 1) return false;
            if (a.Length == b.Length) { i++; j++; }
            else if (a.Length > b.Length) i++;
            else j++;
        }
        diff += (a.Length - i) + (b.Length - j);
        return diff <= 1;
    }
}
