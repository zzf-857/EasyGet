namespace EasyGet.Models;

/// <summary>
/// 一行可虚拟化的下载历史卡片。将二维卡片网格拆成少量纵向行，
/// 让 WPF 只创建视口附近的卡片，而不是一次实例化整个大型合集。
/// </summary>
public sealed class HistoryCardRow
{
    public IReadOnlyList<DownloadHistory> Items { get; init; } = [];
}
