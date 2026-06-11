using EasyGet.ViewModels;
using EasyGet.Services;
using EasyGet.Models;
using Xunit;

namespace EasyGet.Tests;

public class DownloadViewModelTests
{
    [Theory]
    [InlineData("https://youtu.be/abc123，", "https://youtu.be/abc123")]
    [InlineData("https://www.youtube.com/watch?v=abc123。", "https://www.youtube.com/watch?v=abc123")]
    [InlineData("https://v.douyin.com/i6EpMYVJgA8/）", "https://v.douyin.com/i6EpMYVJgA8/")]
    public void ExtractUrl_RemovesTrailingShareTextPunctuation(string input, string expected)
    {
        Assert.Equal(expected, DownloadViewModel.ExtractUrl(input));
    }

    [Fact]
    public void ExtractUrl_RemovesTrailingPunctuationFromMixedShareText()
    {
        var input = "复制打开： https://youtu.be/abc123，看看这个视频";

        Assert.Equal("https://youtu.be/abc123", DownloadViewModel.ExtractUrl(input));
    }

    [Fact]
    public void LogTextJoinsLogLinesForSelectableTextViewer()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        viewModel.LogLines.Add("[12:00:00] 开始下载");
        viewModel.LogLines.Add("[12:00:01] 下载完成");

        Assert.Equal(
            "[12:00:00] 开始下载" + Environment.NewLine + "[12:00:01] 下载完成",
            viewModel.LogText);

        viewModel.LogLines.Clear();

        Assert.Equal("", viewModel.LogText);
    }

    [Fact]
    public async Task ParseCommandShowsReadyPreviewWhenVideoInfoIsResolved()
    {
        using var context = CreateDownloadContext();
        var provider = context.VideoInfoProvider;
        var viewModel = context.ViewModel;
        provider.Enqueue(new VideoInfo
        {
            Title = "示例视频",
            Platform = "YouTube",
            Duration = 125,
            FileSize = 1024 * 1024 * 42,
            Thumbnail = "https://example.com/thumb.jpg",
            Url = "https://example.com/watch?v=demo"
        });

        viewModel.Url = " https://example.com/watch?v=demo ";

        await viewModel.ParseCommand.ExecuteAsync(null);

        Assert.Equal(DownloadPageState.Ready, viewModel.PageState);
        Assert.Equal("示例视频", viewModel.PreviewInfo?.Title);
        Assert.Equal("02:05", viewModel.PreviewDurationText);
        Assert.Equal("42 MB", viewModel.PreviewFileSizeText);
        Assert.Equal("https://example.com/watch?v=demo", provider.Calls.Single().Url);
    }

    [Fact]
    public async Task ParseCommandShowsFailedStateWhenVideoInfoCannotBeResolved()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;
        context.VideoInfoProvider.Enqueue(null);

        viewModel.Url = "https://example.com/missing";

        await viewModel.ParseCommand.ExecuteAsync(null);

        Assert.Equal(DownloadPageState.Failed, viewModel.PageState);
        Assert.Null(viewModel.PreviewInfo);
        Assert.Contains("解析失败", viewModel.ParseErrorMessage);
    }

    [Fact]
    public async Task ChangingUrlDuringParseCancelsOldRequestAndKeepsNewerPreview()
    {
        using var context = CreateDownloadContext();
        var provider = context.VideoInfoProvider;
        var viewModel = context.ViewModel;
        var first = provider.EnqueuePending();
        var second = provider.EnqueuePending();

        viewModel.Url = "https://example.com/first";
        var firstParse = viewModel.ParseCommand.ExecuteAsync(null);
        Assert.Equal(DownloadPageState.Parsing, viewModel.PageState);

        viewModel.Url = "https://example.com/second";
        var secondParse = viewModel.ParseCommand.ExecuteAsync(null);

        first.SetResult(new VideoInfo { Title = "旧结果", Url = "https://example.com/first" });
        second.SetResult(new VideoInfo { Title = "新结果", Url = "https://example.com/second" });

        await Task.WhenAll(firstParse, secondParse);

        Assert.True(provider.Calls[0].CancellationToken.IsCancellationRequested);
        Assert.Equal(DownloadPageState.Ready, viewModel.PageState);
        Assert.Equal("新结果", viewModel.PreviewInfo?.Title);
    }

    [Theory]
    [InlineData(DownloadPageState.Downloading, true, false, false)]
    [InlineData(DownloadPageState.Completed, true, true, false)]
    [InlineData(DownloadPageState.Failed, true, false, true)]
    [InlineData(DownloadPageState.Idle, false, false, false)]
    public void ProgressCardVisibilityFollowsFullLifecycleState(
        DownloadPageState state,
        bool isProgressVisible,
        bool isCompleted,
        bool isTaskFailed)
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        if (isProgressVisible)
            viewModel.CurrentTask = new DownloadTask();

        viewModel.PageState = state;

        Assert.Equal(isProgressVisible, viewModel.IsProgressCardVisible);
        Assert.Equal(isCompleted, viewModel.IsCompleted);
        Assert.Equal(isTaskFailed, viewModel.IsTaskFailed);
    }

    [Fact]
    public void UrlChangedOrClearedResetsProgressCard()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        // Scene 1: Completed state, change URL, progress card is hidden, state returns to Idle
        viewModel.CurrentTask = new DownloadTask();
        viewModel.PageState = DownloadPageState.Completed;
        viewModel.Url = "https://example.com/new-url";

        Assert.Null(viewModel.CurrentTask);
        Assert.Equal(DownloadPageState.Idle, viewModel.PageState);
        Assert.False(viewModel.IsProgressCardVisible);

        // Scene 2: Clear URL, progress card is hidden, state returns to Idle
        viewModel.CurrentTask = new DownloadTask();
        viewModel.PageState = DownloadPageState.Ready;
        viewModel.Url = "";

        Assert.Null(viewModel.CurrentTask);
        Assert.Equal(DownloadPageState.Idle, viewModel.PageState);
    }

    private static DownloadContext CreateDownloadContext()
    {
        var configService = new ConfigService();
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        var historyService = new HistoryService(dbPath);
        var ytDlp = new YtDlpService(configService, new EnvironmentService());
        var manager = new DownloadManager(ytDlp, historyService, configService);
        var videoInfoProvider = new FakeVideoInfoProvider();
        var viewModel = new DownloadViewModel(manager, configService, videoInfoProvider);

        return new DownloadContext(historyService, viewModel, videoInfoProvider);
    }

    private sealed record DownloadContext(
        HistoryService HistoryService,
        DownloadViewModel ViewModel,
        FakeVideoInfoProvider VideoInfoProvider) : IDisposable
    {
        public void Dispose() => HistoryService.Dispose();
    }

    private sealed class FakeVideoInfoProvider : IVideoInfoProvider
    {
        private readonly Queue<TaskCompletionSource<VideoInfo?>> _responses = [];

        public List<VideoInfoCall> Calls { get; } = [];

        public void Enqueue(VideoInfo? info)
        {
            var response = new TaskCompletionSource<VideoInfo?>();
            response.SetResult(info);
            _responses.Enqueue(response);
        }

        public TaskCompletionSource<VideoInfo?> EnqueuePending()
        {
            var response = new TaskCompletionSource<VideoInfo?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Enqueue(response);
            return response;
        }

        public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken)
        {
            Calls.Add(new VideoInfoCall(url, cancellationToken));
            return _responses.Dequeue().Task;
        }
    }

    private sealed record VideoInfoCall(string Url, CancellationToken CancellationToken);
}
