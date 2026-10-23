using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Icons;

/// <summary>Contents of manifest.json. Every field is optional; a folder of PNG files without manifest is a valid pack.</summary>
public sealed class IconPackManifest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Url { get; set; }
    /// <summary>name -> "file.png"  or  name -> { "file": "file.png", "aliases": ["..."] }</summary>
    public Dictionary<string, JsonElement>? Icons { get; set; }
}

public sealed record IconMatch(string Path, MatchRule Rule, string MatchedName);

public sealed class IconPack
{
    private sealed record Entry(string Name, string Norm, string Denoised, string Phonetic, string Path, bool Dark, bool FromManifest)
    {
        public bool Cyrillic { get; } = IconNameMatcher.HasCyrillic(Name);
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, List<Entry>> _byNorm = new(StringComparer.Ordinal);

    public string Id { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string Version { get; private init; } = "";
    public string Description { get; private init; } = "";
    public string Directory { get; private init; } = "";
    public bool IsBuiltIn { get; private init; }
    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = new();
    internal void AddWarning(string text) => _warnings.Add(text);
    public int IconCount => _entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public IEnumerable<(string Name, string Path)> Icons => _entries.Where(e => !e.Dark).Select(e => (e.Name, e.Path));

    public static readonly string[] ImageExtensions = { ".png", ".ico" };

    public static IconPack Load(string directory, bool builtIn = false, string? fallbackName = null)
    {
        var dirName = fallbackName ?? System.IO.Path.GetFileName(directory.TrimEnd('\\', '/'));
        IconPackManifest? manifest = null;
        var mf = System.IO.Path.Combine(directory, "manifest.json");
        string warn = "";
        if (File.Exists(mf))
        {
            try
            {
                if (new FileInfo(mf).Length > IconPackService.MaxTextEntryBytes) throw new InvalidDataException("The manifest exceeds 1 MiB.");
                manifest = JsonSerializer.Deserialize<IconPackManifest>(File.ReadAllText(mf), JsonFile.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { warn = "manifest.json is invalid: " + ex.Message; }
        }
        var pack = new IconPack
        {
            Id = SanitizeId(manifest?.Id) is { Length: > 0 } id ? id : dirName,
            Name = manifest?.Name ?? dirName,
            Version = manifest?.Version ?? "",
            Description = manifest?.Description ?? "",
            Directory = directory,
            IsBuiltIn = builtIn,
        };
        if (warn.Length > 0) pack._warnings.Add(warn);
        pack.Index(manifest);
        return pack;
    }

    public static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var chars = id.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.').ToArray();
        return new string(chars).Trim(' ', '.');
    }

    private void Index(IconPackManifest? manifest)
    {
        // 1. explicit manifest entries
        if (manifest?.Icons is not null)
        {
            foreach (var (name, value) in manifest.Icons)
            {
                if (_entries.Count >= IconLimits.MaxPackImages) { _warnings.Add("Too many manifest entries: the rest was ignored."); break; }
                string? file = null;
                var names = new List<string> { name };
                try
                {
                    if (value.ValueKind == JsonValueKind.String) file = value.GetString();
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        if (value.TryGetProperty("file", out var f)) file = f.GetString();
                        if (value.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                            foreach (var a in al.EnumerateArray())
                            {
                                if (names.Count >= 64) break;
                                if (a.GetString() is { Length: > 0 } s) names.Add(s);
                            }
                    }
                }
                catch (InvalidOperationException) { }
                if (string.IsNullOrWhiteSpace(file)) { _warnings.Add($"'{name}': no file"); continue; }
                var full = ResolveInside(file);
                if (full is null || !File.Exists(full)) { _warnings.Add($"'{name}': file '{file}' not found"); continue; }
                if (!IsAcceptableImageFile(full, out var why)) { _warnings.Add($"'{name}': {why}"); continue; }
                foreach (var n in names.Take(IconLimits.MaxPackImages - _entries.Count)) Add(n, full, dark: false, fromManifest: true);
            }
        }
        int scanned = 0;
        // 2. scan folders (root first, then sub-folders alphabetically, 2 levels deep)
        foreach (var dir in ScanDirs(Directory, 2))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!ImageExtensions.Contains(ext)) continue;
                if (!IsAcceptableImageFile(file, out var why)) { _warnings.Add($"'{System.IO.Path.GetFileName(file)}': {why}"); continue; }
                if (++scanned > IconLimits.MaxPackImages) { _warnings.Add("Too many icon files: the rest of the pack was ignored."); return; }
                var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                if (stem.EndsWith("_mask", StringComparison.OrdinalIgnoreCase)) continue;
                bool dark = stem.EndsWith("_dark", StringComparison.OrdinalIgnoreCase);
                if (dark) stem = stem[..^5];
                Add(stem, file, dark, fromManifest: false);
            }
        }
    }

    /// <summary>Only a cheap size check here; the header (dimensions) is checked by <see cref="ImageProbe"/> when the image is actually loaded or imported.</summary>
    private static bool IsAcceptableImageFile(string path, out string why)
    {
        why = "";
        try
        {
            var len = new FileInfo(path).Length;
            if (len > IconLimits.MaxFileBytes) { why = "the file is too large"; return false; }
            if (len == 0) { why = "the file is empty"; return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = ex.Message; return false; }
        return true;
    }

    private static IEnumerable<string> ScanDirs(string root, int depth)
    {
        yield return root;
        if (depth == 0) yield break;
        IEnumerable<string> subs;
        try { subs = System.IO.Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var s in subs) foreach (var d in ScanDirs(s, depth - 1)) yield return d;
    }

    private string? ResolveInside(string relative)
    {
        try
        {
            var root = System.IO.Path.GetFullPath(Directory) + System.IO.Path.DirectorySeparatorChar;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;     // manifest cannot point outside the pack
        }
        catch (ArgumentException) { return null; }
    }

    private void Add(string name, string path, bool dark, bool fromManifest)
    {
        var norm = IconNameMatcher.Normalize(name);
        if (norm.Length == 0) return;
        var e = new Entry(name, norm, IconNameMatcher.WithoutNoise(name), IconNameMatcher.Phonetic(name), path, dark, fromManifest);
        _entries.Add(e);
        if (!_byNorm.TryGetValue(norm, out var list)) _byNorm[norm] = list = new();
        list.Add(e);
    }

    private static Entry? Pick(IEnumerable<Entry> candidates, bool preferDark)
    {
        Entry? first = null, manifest = null, darkE = null, lightE = null;
        foreach (var c in candidates)
        {
            first ??= c;
            if (c.FromManifest) manifest ??= c;
            if (c.Dark) darkE ??= c; else lightE ??= c;
        }
        if (manifest is not null) return manifest;
        return preferDark ? darkE ?? lightE ?? first : lightE ?? darkE ?? first;
    }

    /// <summary>Finds the icon for an app given all its names.</summary>
    public IconMatch? Find(IEnumerable<string> appNames, bool preferDark = true)
    {
        if (_entries.Count == 0) return null;
        var names = IconNameMatcher.Expand(appNames).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0) return null;

        // 1. exact
        foreach (var n in names)
            if (_byNorm.TryGetValue(IconNameMatcher.Normalize(n), out var l) && Pick(l, preferDark) is { } e) return new(e.Path, MatchRule.Exact, n);
        // 2. without noise words
        foreach (var n in names)
        {
            var d = IconNameMatcher.WithoutNoise(n);
            if (IconNameMatcher.IsTooGeneric(d)) continue;
            var hit = Pick(_entries.Where(x => x.Denoised == d), preferDark);
            if (hit is not null) return new(hit.Path, MatchRule.WithoutNoise, n);
        }
        // 3. same pronunciation, also across alphabets
        foreach (var n in names)
        {
            var ph = IconNameMatcher.Phonetic(n);
            if (IconNameMatcher.IsTooGeneric(ph)) continue;
            var hit = Pick(_entries.Where(x => x.Phonetic == ph), preferDark);
            if (hit is not null) return new(hit.Path, MatchRule.SamePronunciation, n);
        }
        // 4. one name contains the other
        foreach (var n in names)
        {
            var d = IconNameMatcher.WithoutNoise(n);
            if (IconNameMatcher.IsTooGeneric(d)) continue;
            var hit = Pick(_entries.Where(x => !IconNameMatcher.IsTooGeneric(x.Denoised) && IconNameMatcher.ContainsMatch(d, x.Denoised)), preferDark);
            if (hit is not null) return new(hit.Path, MatchRule.Contains, n);
        }
        // 5. a one-letter difference in a Cyrillic name (transliteration is never exact)
        foreach (var n in names)
        {
            var ph = IconNameMatcher.Phonetic(n);
            bool nCyr = IconNameMatcher.HasCyrillic(n);
            var hit = Pick(_entries.Where(x => (nCyr || x.Cyrillic) && IconNameMatcher.OneLetterApart(ph, x.Phonetic)), preferDark);
            if (hit is not null) return new(hit.Path, MatchRule.OneLetterDifference, n);
        }
        return null;
    }
}
