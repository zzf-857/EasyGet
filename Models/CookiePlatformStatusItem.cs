using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace EasyGet.Models;

public sealed partial class CookiePlatformStatusItem : ObservableObject
{
    public required string PlatformId { get; init; }
    public required string StorageKey { get; init; }
    public required string DisplayName { get; init; }

    public string PlatformMark => PlatformId.ToLowerInvariant() switch
    {
        "youtube" => "YT",
        "bilibili" => "BI",
        "douyin" => "DY",
        "tiktok" => "TK",
        "twitter" => "X",
        "instagram" => "IG",
        "facebook" => "FB",
        "kuaishou" => "KS",
        "xiaohongshu" => "XHS",
        "weibo" => "WB",
        "twitch" => "TW",
        _ => "WEB"
    };

    public ImageSource? PlatformIcon => PlatformIconCatalog.Get(PlatformId);

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isDetected;
    [ObservableProperty] private bool _hasAuthenticatedSession;

    [ObservableProperty] private string _statusText = "尚未检测";
    [ObservableProperty] private bool _needsLogin;
    [ObservableProperty] private bool _isOperating;
}
