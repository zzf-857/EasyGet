using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyGet.Models;

public sealed partial class CookiePlatformStatusItem : ObservableObject
{
    public required string PlatformId { get; init; }
    public required string StorageKey { get; init; }
    public required string DisplayName { get; init; }

    [ObservableProperty] private string _statusText = "尚未检测";
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _needsLogin;
    [ObservableProperty] private bool _isOperating;
}
