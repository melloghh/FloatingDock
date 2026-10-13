using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Icons;

/// <summary>
/// Per-app icon overrides chosen by the user ("Change icon"). The chosen file is validated, copied into FloatingDock's data folder and registered in <c>icon-overrides.json</c>.
/// Order of operations in <see cref="Set"/>: validate -> copy to a temp file -> verify -> rename -> save the JSON atomically -> only then delete the previous icon.
/// A failure at any step leaves the previous icon and the JSON untouched.
/// </summary>
public sealed class IconOverrideStore
{
    public const int CurrentSchemaVersion = 1;
    private sealed class FileModel { public int SchemaVersion { get; set; } = CurrentSchemaVersion; public Dictionary<string, string> Overrides { get; set; } = new(); }

    public const long MaxFileBytes = IconLimits.MaxFileBytes;
    private readonly string _file;
    private readonly string _dir;
    private readonly string _dirPrefix;
    private readonly object _gate = new();
    private Dictionary<string, string> _map;

    public IconOverrideStore(string file, string userIconsDir)
    {
        _file = file;
        _dir = Path.GetFullPath(userIconsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _dirPrefix = _dir + Path.DirectorySeparatorChar;
        var result = JsonFile.Load(file, Parse);
        LoadStatus = result.Status;
        LoadDetail = result.Detail;
        _map = result.Value ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CleanTemp();
    }

    /// <summary>How the JSON was loaded (Corrupt = a damaged file was found and kept as *.corrupt; the store starts empty).</summary>
    public LoadStatus LoadStatus { get; }
    public string? LoadDetail { get; }

    public event Action? Changed;

    private static Dictionary<string, string> Parse(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new System.Text.Json.JsonException("The root is not an object.");
        int version = node["SchemaVersion"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        if (version > CurrentSchemaVersion) throw new NotSupportedException($"icon-overrides.json has a newer schema ({version}).");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node["Overrides"] is not JsonObject) throw new System.Text.Json.JsonException("The Overrides object is missing or invalid.");
        if (node["Overrides"] is JsonObject o)
            foreach (var (k, val) in o)
                if (!string.IsNullOrEmpty(k) && val is JsonValue jv && jv.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)) map[k] = path;
        return map;
    }

    /// <summary>The override file for the app, or null when there is none, it vanished, or it lies outside the user icon folder.</summary>
    public string? TryGet(string appKey)
    {
        string? p;
        lock (_gate) { if (!_map.TryGetValue(appKey, out p)) return null; }
        return IsOwnedFile(p) && File.Exists(p) ? p : null;
    }

    public bool Has(string appKey) => TryGet(appKey) is not null;

    private string[] KeysForApp(Models.AppRef app)
    {
        var keys = new List<string> { app.Key };
        // Explorer's AppsFolder pin has only an AppID; running windows often have only an executable.
        if (Models.AppMatching.IsExplorer(app))
        {
            keys.Add("aumid:" + Models.AppMatching.ExplorerAumid.ToLowerInvariant());
            lock (_gate) keys.AddRange(_map.Keys.Where(k => k.StartsWith("exe:", StringComparison.Ordinal) &&
                string.Equals(Models.WinPath.FileName(k[4..]), "explorer.exe", StringComparison.OrdinalIgnoreCase)).Order());
        }
        if (!string.IsNullOrWhiteSpace(app.ExePath))
        {
            string exe = "exe:" + Models.AppMatching.NormalizePath(app.ExePath);
            if (exe != app.Key) keys.Add(exe);
            string? familyExe = PackagedIconIdentity.FromExeKey(exe);
            if (familyExe is not null)
                lock (_gate) keys.AddRange(_map.Keys.Where(k => k != exe && PackagedIconIdentity.FromExeKey(k) == familyExe).OrderDescending());
        }
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    public string? TryGetForApp(Models.AppRef app)
    {
        foreach (var key in KeysForApp(app)) if (TryGet(key) is { } path) return path;
        return null;
    }

    public bool Has(Models.AppRef app) => TryGetForApp(app) is not null;

    public bool ResetForApp(Models.AppRef app)
    {
        EnsureWritable();
        var keys = KeysForApp(app);
        lock (_gate)
        {
            var next = new Dictionary<string, string>(_map, StringComparer.Ordinal);
            bool changed = false;
            foreach (var key in keys) changed |= next.Remove(key);
            if (!changed) return false;
            JsonFile.Save(_file, new FileModel { Overrides = next });
            _map = next;
        }
        CleanUnusedIcons(); Changed?.Invoke(); return true;
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>True when <paramref name="path"/> is a file directly inside the user icon folder (a hand-edited JSON cannot point anywhere else).</summary>
    public bool IsOwnedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(_dirPrefix, StringComparison.OrdinalIgnoreCase) && full.IndexOf(Path.DirectorySeparatorChar, _dirPrefix.Length) < 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <exception cref="InvalidDataException">The file is not an acceptable PNG / ICO.</exception>
    /// <exception cref="IOException">Copying or saving failed; nothing was changed.</exception>
    public void Set(string appKey, string sourceFile, byte[]? validatedBytes = null)
    {
        EnsureWritable();
        if (string.IsNullOrEmpty(appKey)) throw new ArgumentException("appKey");
        var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (ext is not (".png" or ".ico")) throw new InvalidDataException("Only PNG and ICO files can be used as an icon.");
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("Icon file not found.", sourceFile);
        if (ImageProbe.Probe(sourceFile, out var why) is not { } info) throw new InvalidDataException(why ?? "The file is not a valid icon.");
        if ((info.Format == ImageFormat.Png) != (ext == ".png")) throw new InvalidDataException("The file extension does not match its content.");
        lock (_gate) { if (!_map.ContainsKey(appKey) && _map.Count >= IconLimits.MaxOverrides) throw new InvalidDataException("Too many custom icons. Reset some first."); }

        Directory.CreateDirectory(_dir);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appKey)))[..16];
        var dest = Path.Combine(_dir, hash + "_" + Guid.NewGuid().ToString("N") + ext);
        var tmp = dest + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        bool destCreated = false;
        try
        {
            // 1. copy to a temp file and verify it
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (validatedBytes is not null)
                {
                    if (validatedBytes.LongLength > MaxFileBytes) throw new InvalidDataException("The icon is too large.");
                    dst.Write(validatedBytes);
                }
                else
                {
                    using var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (src.Length > MaxFileBytes) throw new InvalidDataException("The icon is too large.");
                    src.CopyTo(dst);
                }
                dst.Flush(true);
            }
            if (ImageProbe.Probe(tmp, out why) is null) throw new InvalidDataException(why ?? "The copied icon is not valid.");
            // 2. make it the final name (nothing refers to it yet)
            File.Move(tmp, dest);
            destCreated = true;

            // 3. the new JSON content is prepared from a copy and saved atomically; the live map changes only after the save succeeded
            string? old;
            lock (_gate)
            {
                var next = new Dictionary<string, string>(_map, StringComparer.Ordinal);
                if (!next.ContainsKey(appKey) && next.Count >= IconLimits.MaxOverrides) throw new InvalidDataException("Too many custom icons.");
                next.TryGetValue(appKey, out old);
                next[appKey] = dest;
                JsonFile.Save(_file, new FileModel { Overrides = next });
                _map = next;
            }
            // 4. only now the previous icon file may go
            CleanUnusedIcons();
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            if (destCreated) try { File.Delete(dest); } catch { /* ignore */ }
            throw;
        }
        Changed?.Invoke();
    }

    /// <summary>Removes the override. Returns false when there was none. Throws <see cref="IOException"/> when saving fails (the override is then kept).</summary>
    public bool Reset(string appKey)
    {
        EnsureWritable();
        string? old;
        lock (_gate)
        {
            if (!_map.TryGetValue(appKey, out old)) return false;
            var next = new Dictionary<string, string>(_map, StringComparer.Ordinal);
            next.Remove(appKey);
            JsonFile.Save(_file, new FileModel { Overrides = next });
            _map = next;
        }
        CleanUnusedIcons();
        Changed?.Invoke();
        return true;
    }

    private void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.EnumerateFiles(_dir, "*.tmp")) try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { /* ignore */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void TryDeleteOwned(string? p)
    {
        try { if (p is not null && IsOwnedFile(p) && File.Exists(p)) File.Delete(p); } catch { /* a leftover file is harmless */ }
    }

    private void CleanUnusedIcons()
    {
        lock (_gate)
        {
            var referenced = new HashSet<string>(_map.Values, StringComparer.OrdinalIgnoreCase);
            var backup = JsonFile.BackupPath(_file);
            try
            {
                if (File.Exists(backup)) referenced.UnionWith(Parse(File.ReadAllText(backup)).Values);
                if (!Directory.Exists(_dir)) return;
                foreach (var file in Directory.EnumerateFiles(_dir))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    // Only files created by this store, never unrelated user files.
                    if (name.Length >= 18 && name[16] == '_' && name[..16].All(Uri.IsHexDigit) &&
                        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".ico" && !referenced.Contains(file)) TryDeleteOwned(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException) { }
        }
    }

    private void EnsureWritable()
    {
        if (LoadStatus == LoadStatus.Corrupt)
            throw new IOException("The icon override list could not be read. Restore the JSON file or its backup before saving changes.");
    }
}
