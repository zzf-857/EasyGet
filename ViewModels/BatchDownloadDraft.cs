using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Models;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class BatchDownloadDraft : ObservableObject
{
    public BatchDownloadDraft(
        string url,
        string title,
        bool hasProvidedTitle,
        int collectionItemIndex = 0,
        int collectionItemCount = 0,
        IReadOnlyList<MediaResourceInfo>? resources = null)
    {
        Url = url;
        _title = title;
        HasProvidedTitle = hasProvidedTitle;
        CollectionItemIndex = collectionItemIndex;
        CollectionItemCount = collectionItemCount;
        Resources = resources ?? [];
    }

    public string Url { get; }

    public bool HasProvidedTitle { get; }

    public int CollectionItemIndex { get; }

    public int CollectionItemCount { get; }

    public IReadOnlyList<MediaResourceInfo> Resources { get; }

    public bool HasResources => Resources.Count > 0;

    internal VideoInfo? ResolvedInfo { get; set; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolutionMessage))]
    private string _resolutionMessage = "";

    [ObservableProperty]
    private bool _isResolving;

    public bool HasResolutionMessage => !string.IsNullOrWhiteSpace(ResolutionMessage);
}
