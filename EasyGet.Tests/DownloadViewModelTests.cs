using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyGet.ViewModels;
using EasyGet.Services;
using EasyGet.Models;
using Xunit;

namespace EasyGet.Tests;

public class DownloadViewModelTests
{
    [Fact]
    public void ParseCommand_IsEnabledOnlyForAValidUrlOutsideParsingState()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        Assert.False(viewModel.ParseCommand.CanExecute(null));

        viewModel.Url = "不是链接";
        Assert.False(viewModel.ParseCommand.CanExecute(null));

        viewModel.Url = "https://example.com/video";
        Assert.True(viewModel.ParseCommand.CanExecute(null));

        viewModel.PageState = DownloadPageState.Parsing;
        Assert.False(viewModel.ParseCommand.CanExecute(null));
    }

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
    public void ExtractUrl_ExtractsDouyinShortUrlFromFullShareText()
    {
        var input = "8.25 复制打开抖音，看看【意联Idealink的作品】父母眼中的“享福”四件套 # AI工具 # AI短... https://v.douyin.com/vi3b7QpNklg/ mDu:/ :2pm q@R.kC 08/19";

        Assert.Equal("https://v.douyin.com/vi3b7QpNklg/", DownloadViewModel.ExtractUrl(input));
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
    public void DownloadLogLimit_UsesNamedConstant()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("ViewModels", "DownloadViewModel.cs")));

        Assert.Contains("MaxLogLines", source, StringComparison.Ordinal);
        Assert.Contains("while (LogLines.Count > MaxLogLines)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LogLines.Count > 200", source, StringComparison.Ordinal);
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
    public void BuildSourceFormatChoices_MapsRealVideoAndAudioStreamsToYtDlpSelectors()
    {
        var info = new VideoInfo
        {
            AvailableFormats =
            [
                new VideoFormatInfo(
                    "137", "mp4", "avc1.640028", "none", 1920, 1080, 30,
                    4500, 0, 104857600, "1080p"),
                new VideoFormatInfo(
                    "22", "mp4", "avc1.64001F", "mp4a.40.2", 1280, 720, 30,
                    1800, 128, 52428800, "720p"),
                new VideoFormatInfo(
                    "140", "m4a", "none", "mp4a.40.2", 0, 0, 0,
                    129, 129, 4194304, "medium")
            ]
        };

        var videoChoices = DownloadViewModel.BuildSourceFormatChoices(info, "mp4");
        var audioChoices = DownloadViewModel.BuildSourceFormatChoices(info, "mp3 (仅音频)");

        Assert.Equal("", videoChoices[0].Selector);
        Assert.Contains(videoChoices, choice =>
            choice.Selector == "137+ba/b"
            && choice.DisplayName.Contains("1080p", StringComparison.Ordinal)
            && choice.DisplayName.Contains("H.264", StringComparison.Ordinal)
            && choice.DisplayName.Contains("ID 137", StringComparison.Ordinal));
        Assert.Contains(videoChoices, choice => choice.Selector == "22");
        Assert.Equal(2, audioChoices.Count);
        Assert.Equal("140", audioChoices[1].Selector);
        Assert.Contains("AAC", audioChoices[1].DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingExactSourceFormat_DisablesAutomaticQualityLimit()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;
        var exact = new SourceFormatChoice("1080p · MP4 · ID 137", "137+ba/b");

        viewModel.SelectedSourceFormat = exact;
        Assert.False(viewModel.UsesAutomaticSourceFormat);

        viewModel.SelectedSourceFormat = new SourceFormatChoice("自动选择", "");
        Assert.True(viewModel.UsesAutomaticSourceFormat);
    }

    [Fact]
    public async Task StartDownloadReusesResolvedPreviewWithoutRequestingMetadataAgain()
    {
        var downloadService = new CompletingDownloadService();
        using var context = CreateDownloadContext(downloadService: downloadService);
        var viewModel = context.ViewModel;
        context.VideoInfoProvider.Enqueue(new VideoInfo
        {
            Title = "已解析标题",
            Platform = "TikTok",
            Duration = 42,
            Url = "https://www.tiktok.com/@creator/video/7524567890123456789"
        });
        viewModel.Url = "https://www.tiktok.com/@creator/video/7524567890123456789";

        await viewModel.ParseCommand.ExecuteAsync(null);
        await viewModel.StartDownloadCommand.ExecuteAsync(null);
        await context.Manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(context.VideoInfoProvider.Calls);
        Assert.Equal(0, downloadService.MetadataCallCount);
        Assert.Equal(1, downloadService.DownloadCallCount);
        Assert.Equal("已解析标题", viewModel.CurrentTask?.Title);
        Assert.Equal("TikTok", viewModel.CurrentTask?.Platform);
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
    [InlineData(DownloadPageState.Scheduled, true, false, false)]
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
    public void TryParseScheduledStart_RejectsPastAndAcceptsFutureLocalTime()
    {
        var now = DateTimeOffset.Now;
        var futureText = now.AddHours(2).ToString("yyyy-MM-dd HH:mm");
        var pastText = now.AddHours(-2).ToString("yyyy-MM-dd HH:mm");

        var futureResult = DownloadViewModel.TryParseScheduledStart(
            futureText,
            now,
            out var scheduled,
            out var futureError);
        var pastResult = DownloadViewModel.TryParseScheduledStart(
            pastText,
            now,
            out _,
            out var pastError);

        Assert.True(futureResult, futureError);
        Assert.True(scheduled > now);
        Assert.False(pastResult);
        Assert.Contains("晚于当前时间", pastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartDownload_WithFutureSchedule_QueuesWithoutStartingNetworkWork()
    {
        var downloadService = new CompletingDownloadService();
        using var context = CreateDownloadContext(downloadService: downloadService);
        var viewModel = context.ViewModel;
        context.VideoInfoProvider.Enqueue(new VideoInfo
        {
            Title = "计划视频",
            Platform = "YouTube",
            Url = "https://example.test/scheduled"
        });
        viewModel.Url = "https://example.test/scheduled";
        await viewModel.ParseCommand.ExecuteAsync(null);
        viewModel.IsScheduledDownloadEnabled = true;
        viewModel.ScheduledStartText = DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:mm");

        await viewModel.StartDownloadCommand.ExecuteAsync(null);

        var task = Assert.Single(context.Manager.Tasks);
        Assert.Equal(DownloadStatus.Scheduled, task.Status);
        Assert.NotNull(task.ScheduledStartTimeUtc);
        Assert.Equal("计划视频", task.Title);
        Assert.Equal(DownloadPageState.Scheduled, viewModel.PageState);
        Assert.False(viewModel.IsDownloading);
        Assert.Equal(0, downloadService.DownloadCallCount);
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

    [Fact]
    public async Task ParseAndDownloadValidationSetsUrlErrorAndDoesNotWriteToLog()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        // Scene 1: Empty URL, start parsing
        viewModel.Url = "";
        viewModel.UrlError = null;
        viewModel.LogLines.Clear();

        await viewModel.ParseCommand.ExecuteAsync(null);

        Assert.Equal("未能从输入中识别出有效链接", viewModel.UrlError);
        Assert.Empty(viewModel.LogLines);

        // Scene 2: Invalid URL, start parsing
        viewModel.Url = "invalid-url";
        viewModel.UrlError = null;
        viewModel.LogLines.Clear();

        await viewModel.ParseCommand.ExecuteAsync(null);

        Assert.Equal("未能从输入中识别出有效链接", viewModel.UrlError);
        Assert.Empty(viewModel.LogLines);

        // Scene 3: Empty URL, start downloading
        viewModel.Url = "";
        viewModel.UrlError = null;
        viewModel.LogLines.Clear();

        await viewModel.StartDownloadCommand.ExecuteAsync(null);

        Assert.Equal("请输入视频链接", viewModel.UrlError);
        Assert.Empty(viewModel.LogLines);

        // Scene 4: Input valid URL, UrlError is automatically cleared
        viewModel.Url = "https://example.com/video";
        Assert.Null(viewModel.UrlError);
    }

    [Fact]
    public void IsValidClipboardUrl_FiltersInvalidScenariosCorrectly()
    {
        // 1. Empty/Null text
        Assert.False(DownloadViewModel.IsValidClipboardUrl("", "https://a.com", "https://b.com"));
        Assert.False(DownloadViewModel.IsValidClipboardUrl(null!, "https://a.com", "https://b.com"));

        // 2. Non-URL text
        Assert.False(DownloadViewModel.IsValidClipboardUrl("hello world", "https://a.com", "https://b.com"));

        // 3. FTP/Invalid Scheme URL
        Assert.False(DownloadViewModel.IsValidClipboardUrl("ftp://example.com", "https://a.com", "https://b.com"));

        // 4. Same as current URL
        Assert.False(DownloadViewModel.IsValidClipboardUrl("https://a.com", "https://a.com", "https://b.com"));
        Assert.False(DownloadViewModel.IsValidClipboardUrl(" https://a.com ", "https://a.com", "https://b.com"));

        // 5. Same as last prompted URL
        Assert.False(DownloadViewModel.IsValidClipboardUrl("https://b.com", "https://a.com", "https://b.com"));

        // 6. Valid URL from share text
        Assert.True(DownloadViewModel.IsValidClipboardUrl("分享链接 https://c.com/video 给你", "https://a.com", "https://b.com"));
    }

    [Fact]
    public void CheckClipboardAndPrompt_RaisesDetectedEventOnceForANewUrl()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;
        viewModel.Url = "https://current.com";
        var detectedUrls = new List<string>();
        viewModel.ClipboardLinkDetected += detectedUrls.Add;

        viewModel.CheckClipboardAndPrompt("Check this: https://new.com");
        viewModel.CheckClipboardAndPrompt("Check this: https://new.com");

        Assert.Equal("https://new.com", viewModel.ClipboardPromptUrl);
        Assert.Equal(["https://new.com"], detectedUrls);
    }

    [Fact]
    public void PasteUrlCommand_WhenClipboardIsBusy_LeavesCurrentUrlUnchanged()
    {
        using var context = CreateDownloadContext(
            readClipboardText: () => throw new COMException("Clipboard is busy"));
        context.ViewModel.Url = "https://example.com/current";

        var exception = Record.Exception(() =>
            context.ViewModel.PasteUrlCommand.Execute(null));

        Assert.Null(exception);
        Assert.Equal("https://example.com/current", context.ViewModel.Url);
    }

    [Fact]
    public void CancelParseCommand_ResetsPageStateToIdleAndClearsCts()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;
        viewModel.PageState = DownloadPageState.Parsing;

        viewModel.CancelParseCommand.Execute(null);

        Assert.Equal(DownloadPageState.Idle, viewModel.PageState);
    }

    [Fact]
    public void UrlChangedDuringDownload_DetachesTaskWithoutCancellingIt()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;

        using var taskCts = new CancellationTokenSource();
        var task = new DownloadTask { Cts = taskCts };
        viewModel.CurrentTask = task;
        viewModel.IsDownloading = true;
        viewModel.PageState = DownloadPageState.Downloading;

        viewModel.Url = "https://example.com/changed-during-download";

        Assert.Equal(DownloadPageState.Idle, viewModel.PageState);
        Assert.Null(viewModel.CurrentTask);
        Assert.False(viewModel.IsDownloading);
        Assert.False(taskCts.IsCancellationRequested);
    }

    [Fact]
    public async Task ParseDuringDownload_DetachesOldTaskAndEnablesNewDownload()
    {
        using var context = CreateDownloadContext();
        var viewModel = context.ViewModel;
        context.VideoInfoProvider.Enqueue(new VideoInfo
        {
            Title = "新任务",
            Url = "https://example.com/current"
        });
        viewModel.Url = "https://example.com/current";
        using var taskCts = new CancellationTokenSource();
        viewModel.CurrentTask = new DownloadTask { Cts = taskCts };
        viewModel.IsDownloading = true;
        viewModel.PageState = DownloadPageState.Downloading;

        await viewModel.ParseCommand.ExecuteAsync(null);

        Assert.Equal(DownloadPageState.Ready, viewModel.PageState);
        Assert.Equal("新任务", viewModel.PreviewInfo?.Title);
        Assert.Null(viewModel.CurrentTask);
        Assert.False(viewModel.IsDownloading);
        Assert.True(viewModel.CanEditDownloadDestination);
        Assert.False(taskCts.IsCancellationRequested);
    }

    [Fact]
    public async Task ActiveDownload_DoesNotBlockParsingAndEnqueuingSecondTask()
    {
        var downloadService = new BlockingDownloadService();
        using var context = CreateDownloadContext(downloadService: downloadService);
        var viewModel = context.ViewModel;
        context.VideoInfoProvider.Enqueue(new VideoInfo
        {
            Title = "第一个任务",
            Url = "https://example.com/first"
        });
        context.VideoInfoProvider.Enqueue(new VideoInfo
        {
            Title = "第二个任务",
            Url = "https://example.com/second"
        });

        try
        {
            viewModel.Url = "https://example.com/first";
            await viewModel.ParseCommand.ExecuteAsync(null);
            await viewModel.StartDownloadCommand.ExecuteAsync(null);
            await downloadService.FirstDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var firstTask = Assert.Single(context.Manager.Tasks);
            var firstToken = Assert.IsType<CancellationTokenSource>(firstTask.Cts);

            viewModel.Url = "https://example.com/second";
            await viewModel.ParseCommand.ExecuteAsync(null);

            Assert.Equal(DownloadPageState.Ready, viewModel.PageState);
            Assert.False(viewModel.IsDownloading);
            Assert.False(firstToken.IsCancellationRequested);

            await viewModel.StartDownloadCommand.ExecuteAsync(null);
            await downloadService.TwoDownloadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, context.Manager.Tasks.Count);
            Assert.Equal("第二个任务", viewModel.CurrentTask?.Title);
            Assert.False(firstToken.IsCancellationRequested);
        }
        finally
        {
            downloadService.Release();
            await context.Manager.WaitForIdleAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task SharedRootChangeDuringPreparation_DoesNotRestoreStalePageDirectory()
    {
        using var root = new TestDirectory();
        var initialRoot = root.Path("initial-root");
        var updatedRoot = root.Path("updated-root");
        var config = new ConfigService(root.Path("config"));
        config.Config.DefaultDownloadPath = initialRoot;
        using var history = new HistoryService(root.Path("history.db"));
        const string url = "https://example.com/video";
        await history.AddAsync(new DownloadHistory
        {
            Url = url,
            Title = "已存在的任务"
        });
        var service = new CompletingDownloadService();
        using var manager = new DownloadManager(service, history, config);
        var provider = new FakeVideoInfoProvider();
        provider.Enqueue(new VideoInfo { Url = url, Title = "新任务" });
        var viewModel = new DownloadViewModel(
            manager,
            config,
            provider,
            preflightService: new DownloadPreflightService(),
            historyService: history,
            duplicateDetector: new DownloadDuplicateDetector());
        viewModel.ConfirmFunc = (_, _) =>
        {
            config.UpdateDefaultDownloadPath(updatedRoot);
            return true;
        };
        viewModel.Url = url;
        await viewModel.ParseCommand.ExecuteAsync(null);

        await viewModel.StartDownloadCommand.ExecuteAsync(null);
        await manager.WaitForIdleAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(updatedRoot, config.Config.DefaultDownloadPath);
        Assert.Equal(updatedRoot, viewModel.DownloadDirectory);
        Assert.Equal(Path.GetFullPath(initialRoot), Assert.Single(manager.Tasks).OutputDirectory);
    }

    [Fact]
    public async Task OpenCurrentFolderCommand_SelectsExistingOutputFileWithInjectedLauncher()
    {
        var startedProcesses = new List<ProcessStartInfo>();
        using var context = CreateDownloadContext(startedProcesses.Add);
        var directory = Path.Combine(Path.GetTempPath(), $"easyget-open-folder-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "video.mp4");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(filePath, "video");
            context.ViewModel.CurrentTask = new DownloadTask
            {
                OutputFilePath = filePath,
                OutputDirectory = directory
            };

            await context.ViewModel.OpenCurrentFolderCommand.ExecuteAsync(null);

            var startInfo = Assert.Single(startedProcesses);
            Assert.Equal("explorer.exe", startInfo.FileName);
            Assert.Equal($"/select,\"{filePath}\"", startInfo.Arguments);
            Assert.True(startInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task PlayCurrentFileCommand_OpensResolvedFileWithInjectedLauncher()
    {
        var startedProcesses = new List<ProcessStartInfo>();
        using var context = CreateDownloadContext(startedProcesses.Add);
        var directory = Path.Combine(Path.GetTempPath(), $"easyget-play-file-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "video.mp4");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(filePath, "video");
            context.ViewModel.CurrentTask = new DownloadTask { OutputFilePath = filePath };

            await context.ViewModel.PlayCurrentFileCommand.ExecuteAsync(null);

            var startInfo = Assert.Single(startedProcesses);
            Assert.Equal(filePath, startInfo.FileName);
            Assert.True(startInfo.UseShellExecute);
            Assert.Equal(directory, startInfo.WorkingDirectory);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static DownloadContext CreateDownloadContext(
        Action<ProcessStartInfo>? startProcess = null,
        Func<string?>? readClipboardText = null,
        IYtDlpDownloadService? downloadService = null)
    {
        var root = new TestDirectory();
        var configService = new ConfigService(root.Path("config"));
        configService.Config.DefaultDownloadPath = root.Path("downloads");
        var historyService = new HistoryService(root.Path("history.db"));
        var manager = downloadService is null
            ? new DownloadManager(
                new YtDlpService(configService, new EnvironmentService()),
                historyService,
                configService)
            : new DownloadManager(downloadService, historyService, configService);
        var videoInfoProvider = new FakeVideoInfoProvider();
        var viewModel = startProcess is null && readClipboardText is null
            ? new DownloadViewModel(manager, configService, videoInfoProvider)
            : new DownloadViewModel(
                manager,
                configService,
                videoInfoProvider,
                startProcess ?? (_ => { }),
                readClipboardText);
        viewModel.Initialize();

        return new DownloadContext(root, historyService, manager, viewModel, videoInfoProvider);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record DownloadContext(
        TestDirectory Root,
        HistoryService HistoryService,
        DownloadManager Manager,
        DownloadViewModel ViewModel,
        FakeVideoInfoProvider VideoInfoProvider) : IDisposable
    {
        public void Dispose()
        {
            Manager.Dispose();
            HistoryService.Dispose();
            Root.Dispose();
        }
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

    private sealed class CompletingDownloadService : IYtDlpDownloadService
    {
        public int MetadataCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            MetadataCallCount++;
            return Task.FromResult<VideoInfo?>(null);
        }

        public Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            task.Status = DownloadStatus.Completed;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDownloadService : IYtDlpDownloadService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downloadCount;

        public TaskCompletionSource FirstDownloadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoDownloadsStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VideoInfo?> GetVideoInfoAsync(
            string url,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VideoInfo?>(null);

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            task.Status = DownloadStatus.Downloading;
            var count = Interlocked.Increment(ref _downloadCount);
            FirstDownloadStarted.TrySetResult();
            if (count >= 2)
                TwoDownloadsStarted.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);
            task.Status = DownloadStatus.Completed;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed record VideoInfoCall(string Url, CancellationToken CancellationToken);
}
