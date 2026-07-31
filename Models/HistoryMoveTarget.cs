namespace EasyGet.Models;

public enum HistoryMoveTargetKind
{
    Organizer,
    LocalDirectory
}

/// <summary>
/// A destination exposed by the history bulk-organize dropdown.
/// </summary>
public sealed class HistoryMoveTarget
{
    public HistoryMoveTargetKind Kind { get; init; }
    public long FolderId { get; init; }
    public string BatchId { get; init; } = "";
    public string BatchName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Directory { get; init; } = "";

    public bool IsOrganizer => Kind == HistoryMoveTargetKind.Organizer;
    public string DisplayName => IsOrganizer
        ? $"整理 · {Name}"
        : $"本地 · {Name}";
}
