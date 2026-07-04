namespace EasyGet.Models;

public sealed record DouyinManifestSummary(
    int ItemCount,
    int UniqueWorkCount,
    int VideoCount,
    int GalleryCount,
    int MusicCount,
    int UnknownCount,
    int FileCount,
    bool IsTruncated,
    IReadOnlyList<DouyinManifestItem> Items,
    string SearchText = "")
{
    public IReadOnlyList<DouyinManifestAuthorSummary> Authors { get; init; } = [];
}

public sealed record DouyinManifestAuthorSummary(
    string AuthorName,
    int WorkCount)
{
    public string WorkCountText => WorkCount == 1
        ? "1 个作品"
        : $"{WorkCount} 个作品";
}

public sealed record DouyinManifestItem(
    string AwemeId,
    string MediaType,
    string MediaTypeText,
    string Description,
    string AuthorName,
    string DateText,
    string RecordedAtText,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> FileNames)
{
    public int FileCount => FileNames.Count;

    public string FileCountText => FileCount == 1
        ? "1 个文件"
        : $"{FileCount} 个文件";

    public string TagCountText => Tags.Count == 1
        ? "1 个标签"
        : $"{Tags.Count} 个标签";

    public string TagsText => string.Join("、 ", Tags);

    public string FileNamesText => string.Join(", ", FileNames);

    public IReadOnlyList<string> FileRoleLabels
    {
        get
        {
            var labels = new List<string>();
            foreach (var fileName in FileNames)
            {
                AddUniqueLabel(labels, ResolveFileRoleLabel(fileName));
            }

            return labels;
        }
    }

    public string FileRoleSummaryText => string.Join("、 ", FileRoleLabels);

    private string ResolveFileRoleLabel(string path)
    {
        var fileName = GetFileName(path).ToLowerInvariant();
        if (fileName.Length == 0)
            return "";

        if (IsNamedFile(fileName, "_cover", ImageExtensions))
            return "封面";
        if (IsNamedFile(fileName, "_music", AudioExtensions))
            return "音频";
        if (IsNamedFile(fileName, "_avatar", ImageExtensions))
            return "头像";
        if (IsNamedFile(fileName, "_comments", CommentExtensions))
            return "评论";
        if (fileName.EndsWith("_data.json", StringComparison.Ordinal))
            return "数据";
        if (fileName.EndsWith("_room.json", StringComparison.Ordinal))
            return "直播元数据";
        if (IsLiveMomentVideo(fileName))
            return "实况";
        if (IsTranscriptFile(fileName))
            return "转写";
        if (MediaType.Equals("gallery", StringComparison.OrdinalIgnoreCase) && IsImageFile(fileName))
            return "图片";
        if (MediaType.Equals("music", StringComparison.OrdinalIgnoreCase) && IsAudioFile(fileName))
            return "音乐";
        if (MediaType.Equals("video", StringComparison.OrdinalIgnoreCase) && IsVideoFile(fileName))
            return "视频";

        return "";
    }

    private static bool IsNamedFile(string fileName, string marker, IReadOnlyList<string> extensions)
        => extensions.Any(extension => fileName.EndsWith($"{marker}{extension}", StringComparison.Ordinal));

    private static bool IsImageFile(string fileName)
        => ImageExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsAudioFile(string fileName)
        => AudioExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsVideoFile(string fileName)
        => VideoExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsTranscriptFile(string fileName)
        => TranscriptExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsLiveMomentVideo(string fileName)
    {
        if (!IsVideoFile(fileName))
            return false;

        var extensionIndex = fileName.LastIndexOf('.');
        var stem = extensionIndex > 0 ? fileName[..extensionIndex] : fileName;
        var markerIndex = stem.LastIndexOf("_live_", StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var indexText = stem[(markerIndex + "_live_".Length)..];
        return indexText.Length > 0 && indexText.All(char.IsDigit);
    }

    private static string GetFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path.Trim().Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 && separatorIndex < normalized.Length - 1
            ? normalized[(separatorIndex + 1)..]
            : normalized;
    }

    private static void AddUniqueLabel(List<string> labels, string label)
    {
        if (!string.IsNullOrWhiteSpace(label) && !labels.Contains(label, StringComparer.Ordinal))
            labels.Add(label);
    }

    private static readonly IReadOnlyList<string> ImageExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    ];

    private static readonly IReadOnlyList<string> AudioExtensions =
    [
        ".mp3",
        ".m4a",
        ".wav",
        ".flac",
        ".aac",
        ".opus",
        ".ogg"
    ];

    private static readonly IReadOnlyList<string> VideoExtensions =
    [
        ".mp4",
        ".mov",
        ".m4v",
        ".webm",
        ".flv"
    ];

    private static readonly IReadOnlyList<string> CommentExtensions =
    [
        ".json",
        ".txt"
    ];

    private static readonly IReadOnlyList<string> TranscriptExtensions =
    [
        ".transcript.txt",
        ".transcript.json"
    ];
}
