namespace EasyGet.Models;

/// <summary>
/// A previously downloaded collection directory projected from download history.
/// </summary>
public sealed class ExistingCollectionFolder
{
    public string BatchId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Directory { get; init; } = "";
    public int ExistingItemCount { get; init; }
    public DateTime LastDownloadTime { get; init; } = DateTime.MinValue;
    public string DisplayName => string.IsNullOrWhiteSpace(Directory)
        ? Name
        : $"{Name} · {Directory}";
}
