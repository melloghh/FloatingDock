namespace FloatingDock.Core.Models;

public enum DockEntryKind { Start, Launchpad, App, Separator }

/// <summary>One slot of the dock as the model sees it (no UI state, no animation state).</summary>
public sealed class DockEntry
{
    public required DockEntryKind Kind { get; init; }
    public required string Key { get; init; }
    public string Name { get; init; } = "";
    public AppRef? App { get; init; }
    public bool IsPinned { get; init; }
    public bool IsRunning => Windows.Count > 0;
    public bool IsActive { get; init; }
    public IReadOnlyList<WindowInfo> Windows { get; init; } = Array.Empty<WindowInfo>();

    public bool SameVisualState(DockEntry o) =>
        Kind == o.Kind && Key == o.Key && Name == o.Name && IsPinned == o.IsPinned && IsRunning == o.IsRunning && IsActive == o.IsActive;
}
