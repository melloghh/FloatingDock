using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Taskbar;

/// <summary>A taskbar window as reported by the operating system.</summary>
public sealed record TaskbarWindow(long Handle, string ClassName, string MonitorId, bool Visible);

/// <summary>The few operations on the Windows taskbar FloatingDock needs. Every method reports success; implementations must verify the effect.</summary>
public interface ITaskbarSystem
{
    /// <summary>ABM_GETSTATE flags. False when there is no taskbar (Explorer is not running).</summary>
    bool TryGetAppBarState(out int flags);
    /// <summary>ABM_SETSTATE; true only when the new state was read back.</summary>
    bool TrySetAppBarState(int flags);
    IReadOnlyList<TaskbarWindow> GetTaskbarWindows();
    bool IsWindow(long handle);
    /// <summary>Shows / hides the window; true only when the new visibility was read back.</summary>
    bool TrySetVisible(long handle, bool visible);
    bool TryGetRegion(long handle, out byte[]? data);
    bool TrySetRegion(long handle, byte[]? data);
    bool TryClipEmpty(long handle);
    bool IsClippedEmpty(long handle);
}

public enum TaskbarOutcome
{
    /// <summary>Done and verified.</summary>
    Ok,
    /// <summary>Nothing had to be done.</summary>
    NothingToDo,
    /// <summary>The requested mode could not be applied completely; a safer mode is active (see message).</summary>
    Degraded,
    /// <summary>Nothing was changed (or the change was rolled back) because it could not be done safely.</summary>
    Refused,
    /// <summary>Restoring did not finish; the recovery record was kept so it can be tried again.</summary>
    RetryNeeded,
    /// <summary>The recovery record is missing / unreadable but the taskbar looks modified: the user has to confirm a forced restore.</summary>
    NeedsConfirmation,
}

public sealed record TaskbarResult(TaskbarOutcome Outcome, string Message)
{
    public bool Success => Outcome is TaskbarOutcome.Ok or TaskbarOutcome.NothingToDo or TaskbarOutcome.Degraded;
    /// <summary>True when the user should be told about the result (anything but a plain success).</summary>
    public bool NeedsAttention => Outcome is not (TaskbarOutcome.Ok or TaskbarOutcome.NothingToDo);
}

/// <summary>
/// The safety logic around the original Windows taskbar, independent of Win32 so it can be unit-tested.
/// <list type="bullet">
/// <item>The taskbar is never changed unless its original state was captured AND the recovery record was saved and read back.</item>
/// <item>Restore performs the inverse operations, verifies them, and deletes the record only after the verification succeeded.</item>
/// <item>Restore only touches what FloatingDock changed: an auto-hide taskbar stays auto-hide, nothing is done when nothing was recorded.</item>
/// </list>
/// </summary>
public sealed class TaskbarCoordinator
{
    private readonly ITaskbarSystem _sys;
    private readonly TaskbarStateStore _store;
    private readonly Action<string> _log;
    private readonly Func<int> _pid;
    private readonly object _gate = new();

    public TaskbarCoordinator(ITaskbarSystem system, TaskbarStateStore store, Action<string>? log = null, Func<int>? currentPid = null)
    {
        _sys = system;
        _store = store;
        _log = log ?? (_ => { });
        _pid = currentPid ?? (() => Environment.ProcessId);
    }

    /// <summary>True when a record of a possibly modified taskbar exists on disk (or exists but is damaged).</summary>
    public bool HasPendingRestore
    {
        get
        {
            lock (_gate)
            {
                var l = _store.Load();
                return l.Status == TaskbarStateStatus.Corrupt || l is { Status: TaskbarStateStatus.Ok, Record.Phase: not TaskbarPhase.Restored };
            }
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Applies <paramref name="mode"/>. Calling it again (settings change, Explorer restart, monitor hot-plug) is safe and re-uses the originally captured state.</summary>
    public TaskbarResult Apply(TaskbarMode mode)
    {
        lock (_gate)
        {
            if (mode == TaskbarMode.Leave) return RestoreLocked("mode set to Leave");

            var load = _store.Load();
            TaskbarStateRecord? record = load.Record is { Phase: not TaskbarPhase.Restored } r ? r : null;

            if (!_sys.TryGetAppBarState(out var flags))
                return new(TaskbarOutcome.Refused, "The Windows taskbar was not found (is Explorer running?). Nothing was changed.");
            var current = _sys.GetTaskbarWindows();
            if (current.Count == 0)
                return new(TaskbarOutcome.Refused, "No taskbar window was found. Nothing was changed.");

            if (load.Status == TaskbarStateStatus.Corrupt)
                return new(TaskbarOutcome.NeedsConfirmation, "The taskbar recovery file cannot be read. Nothing was changed; restore the taskbar first.");

            bool fresh = record is null;
            if (record is null)
            {
                record = new TaskbarStateRecord
                {
                    Phase = TaskbarPhase.Pending,
                    OriginalAppBarFlags = flags,
                    AutoHideChangedByUs = (flags & TaskbarFlags.AutoHide) == 0,
                    CreatedUtc = DateTime.UtcNow,
                    Windows = current.Select(w => new TaskbarWindowRecord { Handle = w.Handle, ClassName = w.ClassName, MonitorId = w.MonitorId, OriginalVisible = w.Visible }).ToList(),
                };
            }
            else Reconcile(record, current);
            record.SchemaVersion = TaskbarStateRecord.CurrentSchemaVersion;
            if (mode == TaskbarMode.HideCompletely)
            {
                foreach (var rec in record.Windows)
                {
                    if (!current.Any(w => w.Handle == rec.Handle)) continue;
                    if (!rec.RegionCaptured)
                    {
                        if (!_sys.TryGetRegion(rec.Handle, out var region))
                            return new(TaskbarOutcome.Refused, "The original taskbar window region could not be captured. Nothing was changed.");
                        rec.OriginalRegion = region;
                        rec.RegionCaptured = true;
                    }
                    // Persist the intent before changing the region, including crash recovery.
                    rec.RegionChangedByUs = true;
                }
            }
            record.OwnerPid = _pid();
            record.AppliedMode = mode;
            record.Phase = TaskbarPhase.Pending;

            // 1. the record must be safely on disk BEFORE the first change
            if (!_store.TrySave(record, out var saveError))
            {
                _log("taskbar: cannot save the recovery record: " + saveError);
                return new(TaskbarOutcome.Refused, "FloatingDock could not save the taskbar recovery file, so the Windows taskbar was NOT changed. (" + saveError + ")");
            }

            // 2. auto-hide
            if ((flags & TaskbarFlags.AutoHide) == 0 && !_sys.TrySetAppBarState(flags | TaskbarFlags.AutoHide))
            {
                _log("taskbar: could not switch auto-hide on");
                var undo = RestoreLocked("auto-hide could not be applied");
                return new(TaskbarOutcome.Refused, "The taskbar auto-hide setting could not be changed; the taskbar was left as it was." + (undo.Success ? "" : " " + undo.Message));
            }

            // 3. visibility of the taskbar windows
            var failures = new List<string>();
            current = _sys.GetTaskbarWindows();
            foreach (var rec in record.Windows)
            {
                var w = FindCurrent(rec, current, new HashSet<long>());
                if (w is null) continue;
                rec.Handle = w.Handle;
                if (mode == TaskbarMode.HideCompletely)
                {
                    if (!_sys.TryClipEmpty(w.Handle)) { failures.Add($"{w.ClassName} region on {w.MonitorId}"); continue; }
                    if (!rec.OriginalVisible || !w.Visible) { if (rec.OriginalVisible) rec.HiddenByUs = true; continue; }
                    rec.HiddenByUs = true;                       // set first: if the hide succeeds half-way a restore still knows
                    // Explorer can immediately show the HWND again; its empty region still prevents drawing/hit testing.
                    _sys.TrySetVisible(w.Handle, false);
                }
                else
                {
                    if (rec.RegionChangedByUs)
                    {
                        if (_sys.TrySetRegion(w.Handle, rec.OriginalRegion)) rec.RegionChangedByUs = false;
                        else failures.Add($"{w.ClassName} region on {w.MonitorId}");
                    }
                    if (rec.HiddenByUs || (rec.OriginalVisible && !w.Visible))
                    {
                        if (_sys.TrySetVisible(w.Handle, true)) rec.HiddenByUs = false; else failures.Add($"{w.ClassName} on {w.MonitorId}");
                    }
                }
            }

            var effective = mode;
            string? degraded = null;
            if (failures.Count > 0)
            {
                _log("taskbar: could not change window visibility: " + string.Join(", ", failures));
                if (mode == TaskbarMode.HideCompletely)
                {
                    // safe fallback: put the windows back, keep the (already verified) auto-hide
                    var back = ShowHiddenWindows(record);
                    effective = TaskbarMode.AutoHide;
                    degraded = back ? "The taskbar could not be hidden completely; it is set to auto-hide instead."
                                    : "The taskbar could not be hidden completely and some taskbar windows could not be shown again. Use 'Restore Windows taskbar'.";
                }
                else degraded = "Some taskbar windows could not be shown again. Use 'Restore Windows taskbar'.";
            }
            record.AppliedMode = effective;
            record.Phase = TaskbarPhase.Applied;
            if (!_store.TrySave(record, out var finalError))
                _log("taskbar: the final recovery record could not be saved (the intent record is on disk): " + finalError);
            _log($"taskbar: applied {effective} (requested {mode}, {(fresh ? "new" : "re-applied")})");
            return degraded is null ? new(TaskbarOutcome.Ok, $"Taskbar mode {effective} applied.") : new(TaskbarOutcome.Degraded, degraded);
        }
    }

    /// <summary>Cheap steady-state check; only saves/re-applies if Explorer undid a change.</summary>
    public TaskbarResult Maintain(TaskbarMode mode)
    {
        lock (_gate)
        {
            if (mode == TaskbarMode.Leave) return new(TaskbarOutcome.NothingToDo, "No taskbar maintenance requested.");
            var windows = _sys.GetTaskbarWindows();
            if (!_sys.TryGetAppBarState(out var flags)) return new(TaskbarOutcome.NothingToDo, "Explorer is not running.");
            bool needsApply = (flags & TaskbarFlags.AutoHide) == 0 ||
                mode == TaskbarMode.HideCompletely && windows.Any(w => !_sys.IsClippedEmpty(w.Handle));
            if (!needsApply) return new(TaskbarOutcome.NothingToDo, "Taskbar mode is still active.");
            var saved = _store.Load();
            if (saved.Record is not { Phase: TaskbarPhase.Applied } record || record.OwnerPid != _pid())
                return new(TaskbarOutcome.Refused, "Taskbar maintenance needs this instance's recovery record.");
            return Apply(mode);
        }
    }

    private bool ShowHiddenWindows(TaskbarStateRecord record)
    {
        bool allOk = true;
        var current = _sys.GetTaskbarWindows();
        var used = new HashSet<long>();
        foreach (var rec in record.Windows)
        {
            var w = FindCurrent(rec, current, used);
            if (w is null) continue;
            used.Add(w.Handle);
            if (rec.RegionChangedByUs)
            {
                if (_sys.TrySetRegion(w.Handle, rec.OriginalRegion)) rec.RegionChangedByUs = false; else allOk = false;
            }
            if (!rec.OriginalVisible || w.Visible) { rec.HiddenByUs = false; continue; }
            if (_sys.TrySetVisible(w.Handle, true)) rec.HiddenByUs = false; else allOk = false;
        }
        return allOk;
    }

    /// <summary>Brings the recorded windows in line with what exists now (Explorer restart gives new handles; a new monitor adds a taskbar).</summary>
    private static void Reconcile(TaskbarStateRecord record, IReadOnlyList<TaskbarWindow> current)
    {
        var used = new HashSet<long>();
        foreach (var rec in record.Windows)
            if (FindCurrent(rec, current, used) is { } w)
            {
                if (rec.Handle != w.Handle) { rec.RegionCaptured = false; rec.RegionChangedByUs = false; rec.OriginalRegion = null; }
                rec.Handle = w.Handle;
                used.Add(w.Handle);
            }
        foreach (var w in current)
            if (!used.Contains(w.Handle) && record.Windows.Count < TaskbarStateRecord.MaxWindows)
                record.Windows.Add(new TaskbarWindowRecord { Handle = w.Handle, ClassName = w.ClassName, MonitorId = w.MonitorId, OriginalVisible = w.Visible });
    }

    private static TaskbarWindow? FindCurrent(TaskbarWindowRecord rec, IReadOnlyList<TaskbarWindow> current, HashSet<long> used)
    {
        var byHandle = current.FirstOrDefault(w => w.Handle == rec.Handle && w.ClassName == rec.ClassName && !used.Contains(w.Handle));
        if (byHandle is not null) return byHandle;
        var byMonitor = current.FirstOrDefault(w => !used.Contains(w.Handle) && w.ClassName == rec.ClassName && rec.MonitorId.Length > 0 && w.MonitorId == rec.MonitorId);
        if (byMonitor is not null) return byMonitor;
        if (rec.ClassName == TaskbarFlags.MainClass) return current.FirstOrDefault(w => !used.Contains(w.Handle) && w.ClassName == rec.ClassName);
        return null;
    }

    // ------------------------------------------------------------------ restore

    /// <summary>Restores what FloatingDock changed (normal exit, mode Leave, crash recovery, tray command). Does nothing when nothing was recorded.</summary>
    public TaskbarResult Restore()
    {
        lock (_gate) return RestoreLocked("restore requested");
    }

    private TaskbarResult RestoreLocked(string reason)
    {
        var load = _store.Load();
        if (load.Status == TaskbarStateStatus.Missing) return new(TaskbarOutcome.NothingToDo, "The taskbar was not changed by FloatingDock.");
        if (load.Status == TaskbarStateStatus.Corrupt)
            return new(TaskbarOutcome.NeedsConfirmation, "The taskbar recovery file is damaged, so FloatingDock cannot know the original taskbar state.");
        var rec = load.Record!;
        if (rec.Phase == TaskbarPhase.Restored || rec.AppliedMode == TaskbarMode.Leave)
        {
            _store.TryDelete();
            return new(TaskbarOutcome.NothingToDo, "The taskbar was not changed by FloatingDock.");
        }

        if (!_sys.TryGetAppBarState(out var flags))
            return new(TaskbarOutcome.RetryNeeded, "The Windows taskbar was not found (is Explorer running?). The recovery record was kept; try again when it is back.");

        var problems = new List<string>();
        var current = _sys.GetTaskbarWindows();
        var used = new HashSet<long>();

        // windows first: a visible taskbar is what the user needs most
        foreach (var w in rec.Windows)
        {
            var cur = FindCurrent(w, current, used);
            if (cur is null) continue;                       // the window no longer exists (Explorer restarted / monitor removed): nothing to restore
            used.Add(cur.Handle);
            if (w.RegionChangedByUs && cur.Handle == w.Handle && !_sys.TrySetRegion(cur.Handle, w.OriginalRegion))
                problems.Add($"{cur.ClassName} original region could not be restored");
            if (w.OriginalVisible && !cur.Visible && !_sys.TrySetVisible(cur.Handle, true)) problems.Add($"{cur.ClassName} on {cur.MonitorId} could not be shown");
        }
        // auto-hide: only when FloatingDock switched it on
        if (rec.AutoHideChangedByUs && (flags & TaskbarFlags.AutoHide) != 0 != rec.OriginalAutoHide)
        {
            var target = rec.OriginalAutoHide ? flags | TaskbarFlags.AutoHide : flags & ~TaskbarFlags.AutoHide;
            if (!_sys.TrySetAppBarState(target)) problems.Add("the taskbar auto-hide setting could not be restored");
        }

        // verify the result with fresh reads
        if (!_sys.TryGetAppBarState(out var after)) problems.Add("the taskbar state could not be read back");
        else if (rec.AutoHideChangedByUs && ((after & TaskbarFlags.AutoHide) != 0) != rec.OriginalAutoHide) problems.Add("auto-hide is still different from the original");
        current = _sys.GetTaskbarWindows();
        used.Clear();
        foreach (var w in rec.Windows)
        {
            var cur = FindCurrent(w, current, used);
            if (cur is null) continue;
            used.Add(cur.Handle);
            if (w.OriginalVisible && !cur.Visible) problems.Add($"{cur.ClassName} on {cur.MonitorId} is still hidden");
            if (w.RegionChangedByUs && cur.Handle == w.Handle &&
                (!_sys.TryGetRegion(cur.Handle, out var restoredRegion) || !SameRegion(w.OriginalRegion, restoredRegion)))
                problems.Add($"{cur.ClassName} original region is still different");
        }

        if (problems.Count > 0)
        {
            _log("taskbar: restore incomplete: " + string.Join("; ", problems));
            return new(TaskbarOutcome.RetryNeeded, "The Windows taskbar could not be fully restored (" + string.Join("; ", problems.Distinct()) + "). The recovery record was kept so it can be tried again.");
        }

        if (!_store.TryDelete())
        {
            rec.Phase = TaskbarPhase.Restored;
            if (!_store.TrySave(rec, out _)) _log("taskbar: restored, but the recovery record could not be deleted or marked");
        }
        _log($"taskbar: restored ({reason})");
        return new(TaskbarOutcome.Ok, "The Windows taskbar was restored.");
    }

    private static bool SameRegion(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    // ------------------------------------------------------------------ crash recovery / forced restore

    /// <summary>Call once at start, BEFORE the configured mode is applied: undoes what a crashed / killed previous run left behind.</summary>
    public TaskbarResult RecoverFromPreviousRun()
    {
        lock (_gate)
        {
            var load = _store.Load();
            if (load.Status == TaskbarStateStatus.Missing) return new(TaskbarOutcome.NothingToDo, "No previous taskbar changes.");
            if (load.Status == TaskbarStateStatus.Corrupt)
            {
                return new(TaskbarOutcome.NeedsConfirmation, "The taskbar recovery file cannot be read; the original state is unknown. No settings were changed.");
            }
            _log("taskbar: restoring changes left over from a previous run");
            return RestoreLocked("previous run did not exit cleanly");
        }
    }

    /// <summary>True when any taskbar window is hidden right now (used to decide whether a forced restore is worth offering).</summary>
    public bool AnyTaskbarWindowHidden()
    {
        lock (_gate) return _sys.GetTaskbarWindows().Any(w => !w.Visible || _sys.IsClippedEmpty(w.Handle));
    }

    /// <summary>
    /// Explicit, user-confirmed restore for the case where the original state is unknown (record missing or damaged): shows every taskbar window and,
    /// when <paramref name="disableAutoHide"/> is true, switches auto-hide off (the Windows default). Never called without the user's consent.
    /// </summary>
    public TaskbarResult ForceRestore(bool disableAutoHide)
    {
        lock (_gate)
        {
            if (!_sys.TryGetAppBarState(out var flags))
                return new(TaskbarOutcome.RetryNeeded, "The Windows taskbar was not found (is Explorer running?).");
            var problems = new List<string>();
            foreach (var w in _sys.GetTaskbarWindows())
            {
                if (_sys.IsClippedEmpty(w.Handle) && !_sys.TrySetRegion(w.Handle, null)) problems.Add($"{w.ClassName} region could not be cleared");
                if (!w.Visible && !_sys.TrySetVisible(w.Handle, true)) problems.Add($"{w.ClassName} on {w.MonitorId} could not be shown");
            }
            if (disableAutoHide && (flags & TaskbarFlags.AutoHide) != 0 && !_sys.TrySetAppBarState(flags & ~TaskbarFlags.AutoHide)) problems.Add("auto-hide could not be switched off");
            if (_sys.GetTaskbarWindows().Any(w => !w.Visible || _sys.IsClippedEmpty(w.Handle))) problems.Add("a taskbar window is still hidden");
            if (problems.Count > 0) return new(TaskbarOutcome.RetryNeeded, "The taskbar could not be restored (" + string.Join("; ", problems.Distinct()) + ").");
            if (!_store.TryDelete()) _log("taskbar: forced restore done but the recovery file could not be deleted");
            _log("taskbar: forced restore done");
            return new(TaskbarOutcome.Ok, "The Windows taskbar was restored.");
        }
    }
}
