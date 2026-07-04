using System.Globalization;

namespace EasyGet.Models;

public sealed record DouyinRecentAuthorItem(
    string AuthorName,
    int WorkCount,
    DateTime LatestDownloadTime)
{
    public string WorkCountText => WorkCount == 1
        ? "1 个作品"
        : $"{WorkCount} 个作品";

    public string LatestDownloadTimeText => LatestDownloadTime == DateTime.MinValue
        ? "最近 --"
        : $"最近 {LatestDownloadTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
}
