using System.IO.Compression;
using System.Text.Json;
using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Icons;

public sealed class IconPackImportException : Exception
{
    public IconPackImportException(string message) : base(message) { }
}

public sealed record ZipPreview(string Name, string Id, string Version, string Description, int IconCount, IReadOnlyList<string> IconNames, IReadOnlyList<string> Problems);

/// <summary>Manages the folders below %LOCALAPPDATA%\FloatingDock\IconPacks: list, load, import / export ZIP, delete.</summary>
public sealed class IconPackService
{
    public const string DefaultPackId = "Default";
    public const string CustomPackId = "Custom";
    public static bool IsBuiltInId(string id) => id is DefaultPackId or CustomPackId;
    public const long MaxEntryBytes = 32L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const long MaxTextEntryBytes = 1024 * 1024;       // manifest.json, readme

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".ico", ".json", ".md", ".txt" };

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msi", ".msp", ".scr", ".com", ".lnk", ".url", ".hta", ".jar", ".cpl", ".reg", ".sys", ".pif", ".appx", ".msix" };

    private readonly string _root;
    private readonly string _tempRoot;

    public IconPackService(string iconPacksDir, string tempDir)
    {
        _root = Path.GetFullPath(iconPacksDir);
        _tempRoot = Path.GetFullPath(tempDir);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, DefaultPackId));
        Directory.CreateDirectory(Path.Combine(_root, CustomPackId));
    }

    public string RootDirectory => _root;

    // Only remove the three identifiable packs supplied by earlier releases.
    // UserIcons/overrides and independently imported packs are separate and untouched.
    public void RemoveLegacyBundledPacks()
    {
        foreach (var id in new[] { "DockIcons1", "DockIcons2", "DockIcons3" })
        {
            var dir = Path.GetFullPath(Path.Combine(_root, id));
            if (!IsInside(_root, dir) || !Directory.Exists(dir)) continue;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var pack = IconPack.Load(dir);
                if (!IsLegacyBundled(pack)) continue;
                // Never recurse through junctions or symbolic links.
                var entries = Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
                if (entries.Any(e => (File.GetAttributes(e) & FileAttributes.ReparsePoint) != 0)) continue;
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsLegacyBundled(IconPack pack) =>
        pack.Id is "DockIcons1" or "DockIcons2" or "DockIcons3" &&
        pack.Description.Contains("из архива владельца проекта", StringComparison.Ordinal);

    public IReadOnlyList<IconPack> ListPacks()
    {
        var list = new List<IconPack>();
        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            try { var pack = IconPack.Load(dir, builtIn: IsBuiltInId(name)); if (!IsLegacyBundled(pack)) list.Add(pack); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    public IconPack? Load(string id)
    {
        var dir = Path.Combine(_root, id);
        // the id is a folder name but users may rename; fall back to scanning ids
        if (Directory.Exists(dir) && IsInside(_root, dir))
        {
            var pack = IconPack.Load(dir, builtIn: IsBuiltInId(id));
            return IsLegacyBundled(pack) ? null : pack;
        }
        return ListPacks().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public bool Delete(string id)
    {
        if (IsBuiltInId(id)) return false;
        var pack = ListPacks().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(p.Directory), id, StringComparison.OrdinalIgnoreCase));
        if (pack is null || pack.IsBuiltIn || !IsInside(_root, pack.Directory)) return false;
        Directory.Delete(pack.Directory, true);
        return true;
    }

    // ------------------------------------------------------------------ ZIP: inspect

    /// <summary>Validates the archive without extracting anything and reports what it contains.</summary>
    public ZipPreview Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var problems = new List<string>();
        var entries = ValidateEntries(zip, problems, out var prefix);
        IconPackManifest? manifest = null;
        var mfEntry = entries.FirstOrDefault(e => e.Rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (mfEntry.Entry is not null)
        {
            try
            {
                using var s = mfEntry.Entry.Open();
                using var r = new StreamReader(s);
                manifest = JsonSerializer.Deserialize<IconPackManifest>(r.ReadToEnd(), JsonFile.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { problems.Add("manifest.json is invalid: " + ex.Message); }
        }
        var icons = entries.Where(e => IconPack.ImageExtensions.Contains(Path.GetExtension(e.Rel).ToLowerInvariant())).Select(e => Path.GetFileNameWithoutExtension(e.Rel)).ToList();
        if (icons.Count == 0) problems.Add("The archive contains no PNG or ICO files.");
        var fallbackName = Path.GetFileNameWithoutExtension(zipPath);
        var id = IconPack.SanitizeId(manifest?.Id);
        if (id.Length == 0) id = IconPack.SanitizeId(prefix.Length > 0 ? prefix.TrimEnd('/') : fallbackName);
        return new ZipPreview(manifest?.Name ?? (prefix.Length > 0 ? prefix.TrimEnd('/') : fallbackName), id, manifest?.Version ?? "", manifest?.Description ?? "", icons.Count, icons, problems);
    }

    // ------------------------------------------------------------------ ZIP: import

    public IconPack Import(string zipPath, bool overwrite = true)
    {
        Directory.CreateDirectory(_tempRoot);
        var staging = Path.Combine(_tempRoot, "import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var problems = new List<string>();
                var entries = ValidateEntries(zip, problems, out _);
                if (problems.Any(IsFatal)) throw new IconPackImportException(problems.First(IsFatal));
                if (!entries.Any(e => IconPack.ImageExtensions.Contains(Path.GetExtension(e.Rel).ToLowerInvariant())))
                    throw new IconPackImportException("The archive contains no PNG or ICO files.");
                Directory.CreateDirectory(staging);
                var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                long total = 0;
                foreach (var (entry, rel) in entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) throw new IconPackImportException($"Unsafe path in archive: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    // copy with a hard limit: the sizes in the zip header cannot be trusted
                    using var src = entry.Open();
                    using var dst = File.Create(dest);
                    var buf = new byte[81920];
                    long written = 0;
                    long entryLimit = IconPack.ImageExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant()) ? IconLimits.MaxFileBytes : MaxTextEntryBytes;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        written += n; total += n;
                        if (written > entryLimit || total > MaxTotalBytes) throw new IconPackImportException("The archive is too large.");
                        dst.Write(buf, 0, n);
                    }
                }
            }

            var skipped = RemoveInvalidImages(staging);
            if (!Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(f => IconPack.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                throw new IconPackImportException("No usable icons: " + (skipped.Count > 0 ? skipped[0] : "the archive contains no PNG or ICO files."));
            var pack = IconPack.Load(staging);
            var manifestId = IconPack.SanitizeId(pack.Id);
            if (manifestId.Length == 0) manifestId = IconPack.SanitizeId(Path.GetFileNameWithoutExtension(zipPath));
            if (manifestId.Length == 0) manifestId = "Imported";
            if (manifestId.Equals(DefaultPackId, StringComparison.OrdinalIgnoreCase) || manifestId.Equals(CustomPackId, StringComparison.OrdinalIgnoreCase)) manifestId += "-imported";
            if (!string.Equals(manifestId, pack.Id, StringComparison.Ordinal)) RewriteManifestId(staging, manifestId);
            var target = Path.GetFullPath(Path.Combine(_root, manifestId));
            if (!IsInside(_root, target)) throw new IconPackImportException("Invalid pack id.");
            if (!Directory.Exists(target) && Directory.EnumerateDirectories(_root).Count() >= IconLimits.MaxPacks)
                throw new IconPackImportException("Too many icon packs are installed. Delete one first.");
            if (Directory.Exists(target))
            {
                if (!overwrite) throw new IconPackImportException($"A pack named '{manifestId}' already exists.");
                var backup = Path.Combine(_root, ".old-" + manifestId + "-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, backup);
                try { Directory.Move(staging, target); }
                catch { Directory.Move(backup, target); throw; }
                try { Directory.Delete(backup, true); } catch { /* leftover is harmless */ }
            }
            else Directory.Move(staging, target);
            var imported = IconPack.Load(target);
            foreach (var w in skipped) imported.AddWarning(w);
            return imported;
        }
        catch (InvalidDataException ex) { throw new IconPackImportException("The file is not a valid ZIP archive: " + ex.Message); }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Deletes images whose header violates <see cref="IconLimits"/> (too many pixels, not really a PNG / ICO). Returns one message per rejected file.</summary>
    private static List<string> RemoveInvalidImages(string dir)
    {
        var skipped = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            if (!IconPack.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            if (ImageProbe.Probe(f, out var err) is null)
            {
                skipped.Add($"Skipped {Path.GetRelativePath(dir, f).Replace('\\', '/')}: {err}");
                try { File.Delete(f); } catch (IOException) { }
            }
        }
        return skipped;
    }

    private static void RewriteManifestId(string dir, string newId)
    {
        var mf = Path.Combine(dir, "manifest.json");
        System.Text.Json.Nodes.JsonObject obj;
        try { obj = File.Exists(mf) ? (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mf)) as System.Text.Json.Nodes.JsonObject ?? new()) : new(); }
        catch (JsonException) { obj = new(); }
        obj["id"] = newId;
        File.WriteAllText(mf, obj.ToJsonString(JsonFile.Options));
    }

    private static bool IsFatal(string problem) => problem.StartsWith("Unsafe", StringComparison.Ordinal) || problem.StartsWith("Forbidden", StringComparison.Ordinal) ||
                                                    problem.StartsWith("The archive is too", StringComparison.Ordinal) || problem.StartsWith("Too many", StringComparison.Ordinal);

    private static List<(ZipArchiveEntry Entry, string Rel)> ValidateEntries(ZipArchive zip, List<string> problems, out string prefix)
    {
        prefix = "";
        var result = new List<(ZipArchiveEntry, string)>();
        if (zip.Entries.Count > MaxEntries) { problems.Add("Too many files in the archive."); return result; }
        long declared = 0;
        int imageCount = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;                                  // directory entry
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || Path.GetFileName(name) is ".DS_Store" or "Thumbs.db") continue;
            if (!IsSafeRelativePath(name)) { problems.Add($"Unsafe path in archive: {e.FullName}"); continue; }
            if (!paths.Add(name)) { problems.Add($"Unsafe duplicate path in archive: {e.FullName}"); continue; }
            var ext = Path.GetExtension(name);
            if (ForbiddenExtensions.Contains(ext)) { problems.Add($"Forbidden file type in archive: {e.FullName}"); continue; }
            if (!AllowedExtensions.Contains(ext)) { problems.Add($"Skipped unsupported file: {e.FullName}"); continue; }
            bool isImage = IconPack.ImageExtensions.Contains(ext.ToLowerInvariant());
            if (e.Length > (isImage ? IconLimits.MaxFileBytes : MaxTextEntryBytes)) { problems.Add($"The archive is too large ({e.FullName} exceeds the size limit for icons)."); continue; }
            if (isImage && ++imageCount > IconLimits.MaxPackImages) { problems.Add("Too many icons in the archive."); return result; }
            declared += e.Length;
            result.Add((e, name));
        }
        if (declared > MaxTotalBytes) problems.Add("The archive is too large.");
        // strip a single top-level folder ("MyPack/manifest.json" -> "manifest.json")
        if (result.Count > 0 && result.All(r => r.Item2.Contains('/')))
        {
            var first = result[0].Item2[..(result[0].Item2.IndexOf('/') + 1)];
            if (result.All(r => r.Item2.StartsWith(first, StringComparison.Ordinal)))
            {
                prefix = first;
                for (int i = 0; i < result.Count; i++) result[i] = (result[i].Item1, result[i].Item2[first.Length..]);
            }
        }
        return result;
    }

    internal static bool IsSafeRelativePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260) return false;
        if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0')) return false;
        foreach (var seg in name.Split('/'))
        {
            if (seg is ".." or "." || seg.Length == 0) return false;
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return false;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = seg.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem.Length == 4 && "123456789¹²³".Contains(stem[3]))) return false;
        }
        return true;
    }

    private static bool IsInside(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ ZIP: export

    public void Export(string id, string zipPath)
    {
        var pack = ListPacks().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(p.Directory), id, StringComparison.OrdinalIgnoreCase))
                   ?? throw new IconPackImportException($"Pack '{id}' not found.");
        var destination = Path.GetFullPath(zipPath);
        var staging = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
        using (var zip = ZipFile.Open(staging, ZipArchiveMode.Create))
        {
        bool hasManifest = false;
        var rootFull = Path.GetFullPath(pack.Directory);
        foreach (var file in Directory.EnumerateFiles(pack.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            var ext = Path.GetExtension(file);
            if (!AllowedExtensions.Contains(ext)) continue;
            var rel = Path.GetRelativePath(rootFull, Path.GetFullPath(file)).Replace('\\', '/');
            if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) hasManifest = true;
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }
        if (!hasManifest)
        {
            var manifest = new IconPackManifest { Id = pack.Id, Name = pack.Name, Version = string.IsNullOrEmpty(pack.Version) ? "1.0.0" : pack.Version, Description = pack.Description };
            var e = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open());
            w.Write(JsonSerializer.Serialize(manifest, JsonFile.Options));
        }
        }
        File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}

/// <summary>Priority: user override &gt; active icon pack &gt; (caller falls back to the Windows icon).</summary>
public sealed class IconResolver
{
    private readonly IconOverrideStore _overrides;
    private readonly IconPackService _packs;
    private IconPack? _active;
    private bool _useIconPack = true;
    private bool _preferDark = true;
    private string _activeId = IconPackService.DefaultPackId;

    public IconResolver(IconOverrideStore overrides, IconPackService packs)
    {
        _overrides = overrides;
        _packs = packs;
    }

    public IconPack? ActivePack => _active;
    public string ActivePackId => _activeId;

    public void Configure(bool useIconPack, string activePackId, bool preferDark)
    {
        _useIconPack = useIconPack;
        _preferDark = preferDark;
        _activeId = activePackId;
        Reload();
    }

    /// <summary>Re-reads the active pack from disk (after import, file changes, or switching).</summary>
    public void Reload() => _active = _useIconPack ? _packs.Load(_activeId) : null;

    public IconMatch? Resolve(Models.AppRef app)
    {
        var ov = _overrides.TryGetForApp(app);
        if (ov is not null) return new IconMatch(ov, MatchRule.Override, "override");
        return _active?.Find(app.IconNames(), _preferDark);
    }
}
