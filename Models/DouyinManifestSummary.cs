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
    IReadOnlyList<DouyinManifestItem> Items);

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
}
