using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingDock.Core.Configuration;

public enum LoadStatus
{
    /// <summary>The file does not exist (first run).</summary>
    Missing,
    /// <summary>Loaded and valid.</summary>
    Ok,
    /// <summary>The main file was damaged or unsupported; the last good copy (*.bak) was used.</summary>
    RecoveredFromBackup,
    /// <summary>The file exists but could not be used and no good backup exists. A copy is kept as *.corrupt.</summary>
    Corrupt,
}

public sealed record LoadResult<T>(T? Value, LoadStatus Status, string? Detail = null) where T : class
{
    /// <summary>True when a file existed but nothing usable could be read from it (callers must not treat this like "empty").</summary>
    public bool Damaged => Status == LoadStatus.Corrupt;
}

/// <summary>Atomic, verified JSON persistence shared by all stores.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// Writes <paramref name="bytes"/> so that a crash or a full disk never leaves a half-written file: unique temp file in the same folder, flushed to disk and read back,
    /// then swapped in with an atomic replace (the previous version becomes <c>*.bak</c>), then verified again.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when the data could not be stored - callers must handle that.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes, bool preserveBackup = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);                                  // to the disk, not just to the OS cache
            }
            if (!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(bytes)) throw new IOException("The temporary file could not be verified.");
            if (File.Exists(path)) File.Replace(tmp, path, preserveBackup ? null : BackupPath(path), true);
            else File.Move(tmp, path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("The saved file could not be verified.");
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    public static void WriteAtomic(string path, string content) => WriteAtomic(path, new UTF8Encoding(false).GetBytes(content));

    public static void Save<T>(string path, T value) => WriteAtomic(path, JsonSerializer.Serialize(value, Options));

    /// <summary>Removes temp files that a crash left next to <paramref name="path"/>.</summary>
    public static void CleanStaleTemp(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*.tmp"))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { /* ignore */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Loads <paramref name="path"/> with <paramref name="parse"/> (which must throw when the content is invalid or unsupported).
    /// If the file is damaged the last good copy is tried; the damaged file is kept as <c>*.corrupt</c> and is never silently replaced by an empty value.
    /// </summary>
    public static LoadResult<T> Load<T>(string path, Func<string, T> parse) where T : class
    {
        CleanStaleTemp(path);
        string? error = null;
        bool exists = false;
        try
        {
            var text = File.ReadAllText(path);
            exists = true;
            return new LoadResult<T>(parse(text), LoadStatus.Ok);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (NotSupportedException ex) { KeepCorruptCopy(path); return new LoadResult<T>(null, LoadStatus.Corrupt, ex.Message); }
        catch (Exception ex) when (IsLoadError(ex)) { exists = true; error = ex.Message; }

        var bak = BackupPath(path);
        try
        {
            if (File.Exists(bak))
            {
                var text = File.ReadAllText(bak);
                var value = parse(text);
                if (exists) KeepCorruptCopy(path);
                // Repair the main file without rotating the damaged file over the good backup.
                try { WriteAtomic(path, new UTF8Encoding(false).GetBytes(text), preserveBackup: true); }
                catch (Exception ex) when (IsLoadError(ex)) { error = (error + " Recovery could not be written: " + ex.Message).Trim(); }
                return new LoadResult<T>(value, LoadStatus.RecoveredFromBackup, error);
            }
        }
        catch (Exception ex) when (IsLoadError(ex)) { exists = true; error ??= ex.Message; }

        if (!exists) return new LoadResult<T>(null, LoadStatus.Missing);
        KeepCorruptCopy(path);
        return new LoadResult<T>(null, LoadStatus.Corrupt, error);
    }

    public static LoadResult<T> Load<T>(string path) where T : class =>
        Load(path, text => JsonSerializer.Deserialize<T>(text, Options) ?? throw new JsonException("The file is empty."));

    /// <summary>Returns null when the file is missing or unusable (a damaged file is kept as *.corrupt).</summary>
    public static T? TryLoad<T>(string path) where T : class => Load<T>(path).Value;

    private static bool IsLoadError(Exception ex) => ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or InvalidOperationException or FormatException;

    private static void KeepCorruptCopy(string path)
    {
        try { File.Copy(path, path + ".corrupt", true); } catch { /* ignore */ }
    }
}
