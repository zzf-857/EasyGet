namespace EasyGet.Models;

public sealed record DouyinRecentAuthorItem(
    string AuthorName,
    int WorkCount,
    DateTime LatestDownloadTime)
{
    public string WorkCountText => WorkCount == 1
        ? "1 个作品"
        : $"{WorkCount} 个作品";
}
