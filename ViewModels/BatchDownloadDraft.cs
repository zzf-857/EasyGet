using CommunityToolkit.Mvvm.ComponentModel;
using EasyGet.Services;

namespace EasyGet.ViewModels;

public partial class BatchDownloadDraft : ObservableObject
{
    public BatchDownloadDraft(
        string url,
        string title,
        bool hasProvidedTitle,
        int collectionItemIndex = 0,
        int collectionItemCount = 0)
    {
        Url = url;
        _title = title;
        HasProvidedTitle = hasProvidedTitle;
        CollectionItemIndex = collectionItemIndex;
        CollectionItemCount = collectionItemCount;
    }

    public string Url { get; }

    public bool HasProvidedTitle { get; }

    public int CollectionItemIndex { get; }

    public int CollectionItemCount { get; }

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
