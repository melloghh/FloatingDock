using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Taskbar;

public enum TaskbarPhase
{
    /// <summary>Intent recorded; the change may or may not have been (fully) applied. Restoring is idempotent.</summary>
    Pending,
    /// <summary>All requested changes were applied and verified.</summary>
    Applied,
    /// <summary>Everything was restored but the file could not be deleted; it must be ignored.</summary>
    Restored,
}

/// <summary>One taskbar window (main taskbar or the taskbar of a secondary monitor) as it was before FloatingDock touched it.</summary>
public sealed class TaskbarWindowRecord
{
    public long Handle { get; set; }
    public string ClassName { get; set; } = "";
    /// <summary>Monitor device name (e.g. \\.\DISPLAY2) the window was on; used to find the window again after Explorer restarted.</summary>
    public string MonitorId { get; set; } = "";
    public bool OriginalVisible { get; set; }
    public bool HiddenByUs { get; set; }
    public bool RegionCaptured { get; set; }
    public byte[]? OriginalRegion { get; set; }
    public bool RegionChangedByUs { get; set; }
}

/// <summary>
/// The recovery record <c>taskbar-state.json</c>: what the taskbar looked like BEFORE FloatingDock changed it. It is written (and verified) before the first change.
/// </summary>
public sealed class TaskbarStateRecord
{
    public const int CurrentSchemaVersion = 2;
    public const int MaxWindows = 64;
    public const int MaxRegionBytes = 65536;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public TaskbarPhase Phase { get; set; } = TaskbarPhase.Pending;
    /// <summary>The mode FloatingDock applied (or was about to apply).</summary>
    public TaskbarMode AppliedMode { get; set; } = TaskbarMode.Leave;
    public int OwnerPid { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>ABM_GETSTATE flags before the change (ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2).</summary>
    public int OriginalAppBarFlags { get; set; }
    /// <summary>True when FloatingDock switched auto-hide on (so it has to switch it off again). False when it was on already: it is never touched on restore.</summary>
    public bool AutoHideChangedByUs { get; set; }
    public List<TaskbarWindowRecord> Windows { get; set; } = new();

    public bool OriginalAutoHide => (OriginalAppBarFlags & TaskbarFlags.AutoHide) != 0;

    /// <summary>Returns an error text when the record cannot be trusted, otherwise null.</summary>
    public string? Validate()
    {
        if (SchemaVersion < 1) return "missing schema version";
        if (SchemaVersion > CurrentSchemaVersion) return $"written by a newer version (schema {SchemaVersion})";
        if (!Enum.IsDefined(Phase)) return "unknown phase";
        if (!Enum.IsDefined(AppliedMode)) return "unknown mode";
        if (Windows is null) return "no window list";
        if (Windows.Count > MaxWindows) return "too many windows";
        if (OriginalAppBarFlags < 0 || OriginalAppBarFlags > 0xF) return "invalid flags";
        if (Windows.Any(w => w is null)) return "invalid window entry";
        if (Windows.Any(w => w.Handle == 0 || w.ClassName is not (TaskbarFlags.MainClass or TaskbarFlags.SecondaryClass) || w.MonitorId is null)) return "invalid taskbar window identity";
        if (Windows.Select(w => w.Handle).Distinct().Count() != Windows.Count) return "duplicate taskbar handles";
        if (Windows.Any(w => w.OriginalRegion is { } bytes && (bytes.Length < 32 || bytes.Length > MaxRegionBytes))) return "invalid taskbar region data";
        if (Windows.Any(w => w.RegionChangedByUs && !w.RegionCaptured)) return "missing original taskbar region";
        return null;
    }
}

public static class TaskbarFlags
{
    public const int AutoHide = 1;
    public const int AlwaysOnTop = 2;
    public const string MainClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";
}

public enum TaskbarStateStatus { Missing, Ok, Corrupt }

public sealed record TaskbarStateLoad(TaskbarStateStatus Status, TaskbarStateRecord? Record, string? Detail);

/// <summary>Reads / writes the recovery record: atomic, verified, schema-checked. A damaged file is kept as *.corrupt.</summary>
public sealed class TaskbarStateStore
{
    private readonly string _path;
    public TaskbarStateStore(string path) { _path = path; }
    public string Path => _path;

    public TaskbarStateLoad Load()
    {
        var r = JsonFile.Load(_path, text =>
        {
            var rec = System.Text.Json.JsonSerializer.Deserialize<TaskbarStateRecord>(text, JsonFile.Options) ?? throw new System.Text.Json.JsonException("empty");
            if (rec.SchemaVersion > TaskbarStateRecord.CurrentSchemaVersion) throw new NotSupportedException("The recovery record was written by a newer version.");
            if (rec.Validate() is { } problem) throw new InvalidDataException(problem);
            return rec;
        });
        return r.Status switch
        {
            LoadStatus.Missing => new(TaskbarStateStatus.Missing, null, null),
            LoadStatus.Corrupt => new(TaskbarStateStatus.Corrupt, null, r.Detail),
            _ => new(TaskbarStateStatus.Ok, r.Value, r.Detail),
        };
    }

    /// <summary>True only when the record is on disk and was read back successfully.</summary>
    public bool TrySave(TaskbarStateRecord record, out string? error)
    {
        try
        {
            if (record.Validate() is { } problem) throw new IOException("Invalid recovery record: " + problem);
            JsonFile.Save(_path, record);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Deletes the record (and its *.bak). True when no record remains.</summary>
    public bool TryDelete()
    {
        try
        {
            var bak = JsonFile.BackupPath(_path);
            if (File.Exists(bak)) File.Delete(bak);
            if (File.Exists(_path)) File.Delete(_path);
            return !File.Exists(_path) && !File.Exists(bak);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
