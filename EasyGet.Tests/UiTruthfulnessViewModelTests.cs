using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class UiTruthfulnessViewModelTests
{
    [Fact]
    public void MainViewModelUsesChineseBatchPageTitleAndAssemblyVersion()
    {
        using var context = CreateViewModelContext();

        context.Main.SelectedNavIndex = 1;

        Assert.Equal("批量下载", context.Main.CurrentPageTitle);
        Assert.Matches(@"^v\d+\.\d+\.\d+", context.Main.AppVersion);
    }

    [Fact]
    public void MainViewModelNavigatesToDouyinWorkspace()
    {
        using var context = CreateViewModelContext();

        context.Main.NavigateCommand.Execute("douyin");

        Assert.Equal(2, context.Main.SelectedNavIndex);
        Assert.Equal("抖音工作台", context.Main.CurrentPageTitle);
        Assert.Same(context.Douyin, context.Main.CurrentPage);
    }

    [Fact]
    public void DownloadViewModelSummariesComeFromRuntimeConfig()
    {
        var configService = new ConfigService();
        configService.Config.UseProxy = false;
        configService.Config.ProxyAddress = "socks5://127.0.0.1:7890";
        configService.Config.ConcurrentFragments = 8;

        Assert.Equal("未启用", DownloadViewModel.DescribeProxyStatus(configService.Config));
        Assert.Equal("8 分片", DownloadViewModel.DescribeConcurrentFragments(configService.Config));

        configService.Config.UseProxy = true;

        Assert.Equal("socks5://127.0.0.1:7890", DownloadViewModel.DescribeProxyStatus(configService.Config));
    }

    [Fact]
    public void DouyinViewModelSummarizesQuickDownloadRuntimeState()
    {
        using var context = CreateViewModelContext();
        context.Settings.EnableDouyinSpecialEngine = false;
        context.Settings.DouyinMode = "post";
        context.Settings.CookieContent = "";
        context.Settings.UseProxy = false;
        context.Settings.ProxyAddress = "socks5://127.0.0.1:7890";

        Assert.Equal("专项引擎未启用", context.Douyin.DouyinQuickDownloadEngineStatusText);
        Assert.Equal("Cookie 未配置", context.Douyin.DouyinQuickDownloadCookieStatusText);
        Assert.Equal("代理未启用", context.Douyin.DouyinQuickDownloadProxyStatusText);

        context.Settings.EnableDouyinSpecialEngine = true;
        context.Settings.DouyinMode = "mix";
        context.Settings.CookieContent = "ttwid=abc";
        context.Settings.UseProxy = true;

        Assert.Equal("专项引擎已启用 · mix", context.Douyin.DouyinQuickDownloadEngineStatusText);
        Assert.Equal("Cookie 缺少 odin_tt、passport_csrf_token", context.Douyin.DouyinQuickDownloadCookieStatusText);
        Assert.Equal("代理 socks5://127.0.0.1:7890", context.Douyin.DouyinQuickDownloadProxyStatusText);
    }

    [Fact]
    public void DouyinViewModelReportsDouyinCookieHealth()
    {
        using var context = CreateViewModelContext();

        context.Settings.CookieContent = "ttwid=abc; odin_tt=def; passport_csrf_token=ghi";

        Assert.Equal("Cookie 关键项完整 · msToken 可自动生成", context.Douyin.DouyinQuickDownloadCookieStatusText);

        context.Settings.CookieContent = "ttwid=abc; odin_tt=def; passport_csrf_token=ghi; msToken=jkl";

        Assert.Equal("Cookie 关键项完整", context.Douyin.DouyinQuickDownloadCookieStatusText);

        context.Settings.CookieContent = """
        {"ttwid":"abc","odin_tt":"def","passport_csrf_token":"ghi"}
        """;

        Assert.Equal("Cookie 关键项完整 · msToken 可自动生成", context.Douyin.DouyinQuickDownloadCookieStatusText);

        context.Settings.CookieContent = "not-a-cookie-header";

        Assert.Equal("Cookie 格式未识别", context.Douyin.DouyinQuickDownloadCookieStatusText);
    }

    [Fact]
    public void DouyinViewModelSwitchesQuickDownloadMode()
    {
        using var context = CreateViewModelContext();
        context.Settings.EnableDouyinSpecialEngine = false;
        context.Settings.DouyinMode = "post";

        Assert.Equal("当前内容：作品", context.Douyin.DouyinQuickDownloadModeLabelText);

        context.Douyin.SetDouyinQuickDownloadModeCommand.Execute("mix");

        Assert.True(context.Settings.EnableDouyinSpecialEngine);
        Assert.Equal("mix", context.Settings.DouyinMode);
        Assert.Equal("专项引擎已启用 · mix", context.Douyin.DouyinQuickDownloadEngineStatusText);
        Assert.Equal("当前内容：合集", context.Douyin.DouyinQuickDownloadModeLabelText);

        context.Douyin.SetDouyinQuickDownloadModeCommand.Execute(" post,like,mix,music ");

        Assert.Equal("post,like,mix,music", context.Settings.DouyinMode);
        Assert.Equal("专项引擎已启用 · post,like,mix,music", context.Douyin.DouyinQuickDownloadEngineStatusText);
        Assert.Equal("当前内容：全量", context.Douyin.DouyinQuickDownloadModeLabelText);

        context.Douyin.SetDouyinQuickDownloadModeCommand.Execute("unknown");

        Assert.Equal("post,like,mix,music", context.Settings.DouyinMode);
    }

    [Fact]
    public void DouyinViewModelSummarizesQuickDownloadLinkKind()
    {
        using var context = CreateViewModelContext();

        Assert.Equal("等待抖音链接", context.Douyin.DouyinQuickDownloadLinkInsightText);

        context.Download.Url = "https://www.douyin.com/video/7621772413184822582";

        Assert.Contains("单作品", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);
        Assert.Contains("7621772413184822582", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);

        context.Download.Url = "复制这条内容 https://v.douyin.com/iABC123/ 打开抖音";

        Assert.Contains("短链", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);
        Assert.Contains("需要解析展开", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);

        context.Download.Url = "https://www.douyin.com/user/MS4wLjABAAAAsec_uid-test_123";

        Assert.Contains("用户主页", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);
        Assert.Contains("MS4wLjABAAAAsec_uid-test_123", context.Douyin.DouyinQuickDownloadLinkInsightText, StringComparison.Ordinal);

        context.Download.Url = "https://example.com/watch?v=1";

        Assert.Equal("未识别为抖音专项链接", context.Douyin.DouyinQuickDownloadLinkInsightText);
    }

    [Fact]
    public void BatchDownloadViewModelCountsOnlyActiveDownloads()
    {
        using var context = CreateBatchContext();
        var waiting = new DownloadTask { Status = DownloadStatus.Waiting };
        var downloading = new DownloadTask { Status = DownloadStatus.Downloading };
        var merging = new DownloadTask { Status = DownloadStatus.Merging };
        var completed = new DownloadTask { Status = DownloadStatus.Completed };

        context.Manager.Tasks.Add(waiting);
        context.Manager.Tasks.Add(downloading);
        context.Manager.Tasks.Add(merging);
        context.Manager.Tasks.Add(completed);

        Assert.Equal(1, context.Batch.ActiveDownloadCount);

        waiting.Status = DownloadStatus.Downloading;

        Assert.Equal(2, context.Batch.ActiveDownloadCount);
    }

    [Fact]
    public void DouyinViewModelCountsOnlyDouyinTasks()
    {
        using var context = CreateViewModelContext();

        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Platform = "",
            Status = DownloadStatus.Downloading
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "Douyin",
            Status = DownloadStatus.Completed
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "YouTube",
            Status = DownloadStatus.Failed
        });

        Assert.Equal(2, context.Douyin.DouyinTaskCount);
        Assert.Equal(1, context.Douyin.ActiveDouyinTaskCount);
        Assert.Equal(1, context.Douyin.CompletedDouyinTaskCount);
        Assert.Equal(0, context.Douyin.FailedDouyinTaskCount);
    }

    [Fact]
    public void DouyinViewModelMaintainsFilteredTaskCenterItems()
    {
        using var context = CreateViewModelContext();
        var douyinTask = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Platform = "",
            Status = DownloadStatus.Waiting,
            Title = "douyin task"
        };
        var nonDouyinTask = new DownloadTask
        {
            Url = "https://example.com/video",
            Platform = "YouTube",
            Status = DownloadStatus.Waiting,
            Title = "other task"
        };

        context.BatchContext.Manager.Tasks.Add(douyinTask);
        context.BatchContext.Manager.Tasks.Add(nonDouyinTask);

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("douyin task", item.Title));

        nonDouyinTask.Platform = "Douyin";

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("douyin task", item.Title),
            item => Assert.Equal("other task", item.Title));

        context.BatchContext.Manager.Tasks.Remove(douyinTask);

        Assert.Collection(
            context.Douyin.DouyinTaskItems,
            item => Assert.Equal("other task", item.Title));
    }

    [Fact]
    public void DouyinViewModelFiltersTaskCenterByStatusAndSearchKeyword()
    {
        using var context = CreateViewModelContext();
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/active",
            Platform = "",
            Status = DownloadStatus.Downloading,
            Title = "猫咪进行中"
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/dance",
            Platform = "",
            Status = DownloadStatus.Completed,
            Title = "舞蹈完成"
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/fail",
            Platform = "",
            Status = DownloadStatus.Failed,
            Title = "失败作品"
        });
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://youtube.com/watch?v=abc",
            Platform = "YouTube",
            Status = DownloadStatus.Completed,
            Title = "非抖音"
        });

        Assert.Equal(["全部", "进行中", "已完成", "失败", "已暂停", "已取消"], context.Douyin.DouyinTaskFilterOptions);
        Assert.Equal(3, context.Douyin.DouyinTaskCount);
        Assert.Equal(3, context.Douyin.FilteredDouyinTaskCount);

        context.Douyin.SetDouyinTaskFilterCommand.Execute("已完成");

        Assert.Equal("已完成", context.Douyin.SelectedDouyinTaskFilter);
        Assert.Equal(3, context.Douyin.DouyinTaskCount);
        Assert.Equal(1, context.Douyin.FilteredDouyinTaskCount);
        var completed = Assert.Single(context.Douyin.DouyinTaskItems);
        Assert.Equal("舞蹈完成", completed.Title);

        context.Douyin.DouyinTaskSearchKeyword = "fail";
        context.Douyin.SetDouyinTaskFilterCommand.Execute("全部");

        var failed = Assert.Single(context.Douyin.DouyinTaskItems);
        Assert.Equal("失败作品", failed.Title);
        Assert.Equal(1, context.Douyin.FilteredDouyinTaskCount);
        Assert.Equal(3, context.Douyin.DouyinTaskCount);
    }

    [Fact]
    public void DouyinViewModelReappliesTaskCenterFilterWhenTaskStatusChanges()
    {
        using var context = CreateViewModelContext();
        var activeTask = new DownloadTask
        {
            Url = "https://www.douyin.com/video/active",
            Status = DownloadStatus.Downloading,
            Title = "active task"
        };
        context.BatchContext.Manager.Tasks.Add(activeTask);

        context.Douyin.SetDouyinTaskFilterCommand.Execute("进行中");

        Assert.Equal(1, context.Douyin.FilteredDouyinTaskCount);

        activeTask.Status = DownloadStatus.Completed;

        Assert.Equal(0, context.Douyin.FilteredDouyinTaskCount);
        Assert.Empty(context.Douyin.DouyinTaskItems);
        Assert.Equal(1, context.Douyin.CompletedDouyinTaskCount);
    }

    [Fact]
    public void DouyinViewModelIgnoresClearedTaskStateChanges()
    {
        using var context = CreateViewModelContext();
        var clearedTask = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Status = DownloadStatus.Downloading
        };
        context.BatchContext.Manager.Tasks.Add(clearedTask);
        context.BatchContext.Manager.Tasks.Clear();

        var notificationCount = 0;
        context.Douyin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DouyinViewModel.DouyinTaskCount))
                notificationCount++;
        };

        clearedTask.Status = DownloadStatus.Completed;

        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public void DouyinViewModelFiltersArchiveHistoryToDouyinItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin by platform",
            Platform = "Douyin",
            Url = "https://example.com/video"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin by url",
            Platform = "",
            Url = "https://v.douyin.com/abc123"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "not douyin",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "lookalike domain",
            Platform = "",
            Url = "https://notdouyin.com/video/123"
        });

        Assert.Collection(
            context.Douyin.DouyinHistoryItems,
            item => Assert.Equal("douyin by platform", item.Title),
            item => Assert.Equal("douyin by url", item.Title));
    }

    [Fact]
    public void DouyinViewModelFiltersArchiveByKeywordAndMediaType()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "城市视频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/video-1",
            Format = "mp4",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                1,
                0,
                0,
                0,
                1,
                false,
                [
                    new DouyinManifestItem(
                        "video-1",
                        "video",
                        "视频",
                        "城市夜景",
                        "作者 A",
                        "2026-07-01",
                        "",
                        ["城市"],
                        ["video.mp4"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "旅行图文",
            Platform = "Douyin",
            Url = "https://www.douyin.com/note/gallery-1",
            Format = "jpg",
            DouyinManifestSummaryText = "作品 1 / 图文 1 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                0,
                1,
                0,
                0,
                2,
                false,
                [
                    new DouyinManifestItem(
                        "gallery-1",
                        "gallery",
                        "图文",
                        "海边旅行",
                        "作者 B",
                        "2026-07-02",
                        "",
                        ["旅行", "海边"],
                        ["gallery_1.jpg", "gallery_2.jpg"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "原声音频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/music/music-1",
            Format = "mp3",
            DouyinManifestSummaryText = "作品 1 / 音乐 1 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                0,
                0,
                1,
                0,
                1,
                false,
                [
                    new DouyinManifestItem(
                        "music-1",
                        "music",
                        "音乐",
                        "夏日原声",
                        "作者 C",
                        "2026-07-03",
                        "",
                        ["音乐"],
                        ["music.mp3"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "not douyin",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc",
            Format = "mp4"
        });

        Assert.Equal(["全部", "视频", "图文", "音乐"], context.Douyin.DouyinArchiveTypeFilterOptions);
        Assert.Equal(3, context.Douyin.DouyinArchiveCount);
        Assert.Equal(3, context.Douyin.FilteredDouyinArchiveCount);

        context.Douyin.SelectedDouyinArchiveTypeFilter = "图文";

        var gallery = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("旅行图文", gallery.Title);
        Assert.Equal(1, context.Douyin.FilteredDouyinArchiveCount);
        Assert.Equal(3, context.Douyin.DouyinArchiveCount);

        context.Douyin.SelectedDouyinArchiveTypeFilter = "全部";
        context.Douyin.DouyinArchiveSearchKeyword = "作者 C";

        var music = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("原声音频", music.Title);
        Assert.Equal(1, context.Douyin.FilteredDouyinArchiveCount);
    }

    [Fact]
    public void DouyinViewModelArchiveTypeFilterFallsBackToAllForUnknownValues()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "抖音视频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/123",
            Format = "mp4"
        });

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("不存在的类型");

        Assert.Equal("全部", context.Douyin.SelectedDouyinArchiveTypeFilter);
        var item = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("抖音视频", item.Title);
        Assert.Equal(1, context.Douyin.FilteredDouyinArchiveCount);
    }

    [Fact]
    public void DouyinViewModelClearsArchiveFiltersAndSearchKeyword()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "城市视频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/video-1",
            Format = "mp4"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "旅行图文",
            Platform = "Douyin",
            Url = "https://www.douyin.com/note/gallery-1",
            Format = "jpg"
        });

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("视频");
        context.Douyin.DouyinArchiveSearchKeyword = "城市";

        Assert.True(context.Douyin.IsDouyinArchiveFilterActive);
        var filtered = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("城市视频", filtered.Title);

        context.Douyin.ClearDouyinArchiveFiltersCommand.Execute(null);

        Assert.Equal("", context.Douyin.DouyinArchiveSearchKeyword);
        Assert.Equal("全部", context.Douyin.SelectedDouyinArchiveTypeFilter);
        Assert.False(context.Douyin.IsDouyinArchiveFilterActive);
        Assert.Collection(
            context.Douyin.DouyinHistoryItems,
            item => Assert.Equal("城市视频", item.Title),
            item => Assert.Equal("旅行图文", item.Title));
    }

    [Fact]
    public void DouyinViewModelBuildsRecentAuthorShortcutsFromManifestItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "较早批量",
            Platform = "Douyin",
            Url = "https://www.douyin.com/user/older",
            DownloadTime = new DateTime(2026, 7, 1, 10, 0, 0),
            DouyinManifestSummaryText = "作品 3 / 视频 3 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                3,
                3,
                3,
                0,
                0,
                0,
                3,
                false,
                [
                    new DouyinManifestItem("older-a1", "video", "视频", "作者 A 的视频 1", "作者 A", "2026-07-01", "", [], ["a1.mp4"]),
                    new DouyinManifestItem("older-a2", "video", "视频", "作者 A 的视频 2", "作者 A", "2026-07-01", "", [], ["a2.mp4"]),
                    new DouyinManifestItem("older-b1", "video", "视频", "作者 B 的视频 1", "作者 B", "2026-07-01", "", [], ["b1.mp4"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "较新批量",
            Platform = "Douyin",
            Url = "https://www.douyin.com/user/newer",
            DownloadTime = new DateTime(2026, 7, 3, 10, 0, 0),
            DouyinManifestSummaryText = "作品 2 / 视频 2 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                2,
                2,
                2,
                0,
                0,
                0,
                2,
                false,
                [
                    new DouyinManifestItem("newer-b1", "video", "视频", "作者 B 的视频 2", "作者 B", "2026-07-03", "", [], ["b2.mp4"]),
                    new DouyinManifestItem("newer-c1", "video", "视频", "作者 C 的视频 1", "作者 C", "2026-07-03", "", [], ["c1.mp4"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "非抖音作者",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc",
            DownloadTime = new DateTime(2026, 7, 4, 10, 0, 0),
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                1,
                0,
                0,
                0,
                1,
                false,
                [
                    new DouyinManifestItem("ignored", "video", "视频", "不应纳入", "作者 Z", "2026-07-04", "", [], ["z.mp4"])
                ])
        });

        Assert.True(context.Douyin.HasDouyinRecentAuthorItems);
        Assert.Collection(
            context.Douyin.DouyinRecentAuthorItems,
            item =>
            {
                Assert.Equal("作者 B", item.AuthorName);
                Assert.Equal(2, item.WorkCount);
                Assert.Equal("2 个作品", item.WorkCountText);
            },
            item =>
            {
                Assert.Equal("作者 A", item.AuthorName);
                Assert.Equal(2, item.WorkCount);
            },
            item =>
            {
                Assert.Equal("作者 C", item.AuthorName);
                Assert.Equal(1, item.WorkCount);
                Assert.Equal("1 个作品", item.WorkCountText);
            });
    }

    [Fact]
    public void DouyinViewModelBuildsRecentAuthorShortcutsFromFullManifestAuthorSummary()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "截断批量",
            Platform = "Douyin",
            Url = "https://www.douyin.com/user/truncated",
            DownloadTime = new DateTime(2026, 7, 3, 10, 0, 0),
            DouyinManifestSummaryText = "作品 25 / 视频 20 / 图文 5 / 附属 25",
            DouyinManifestSummary = new DouyinManifestSummary(
                25,
                25,
                20,
                5,
                0,
                0,
                25,
                true,
                [
                    new DouyinManifestItem("video-1", "video", "视频", "展示视频", "作者 A", "2026-07-03", "", [], ["video_1.mp4"])
                ])
            {
                Authors =
                [
                    new DouyinManifestAuthorSummary("作者 A", 20),
                    new DouyinManifestAuthorSummary("作者 Z", 5)
                ]
            }
        });

        Assert.Collection(
            context.Douyin.DouyinRecentAuthorItems,
            item =>
            {
                Assert.Equal("作者 A", item.AuthorName);
                Assert.Equal(20, item.WorkCount);
            },
            item =>
            {
                Assert.Equal("作者 Z", item.AuthorName);
                Assert.Equal(5, item.WorkCount);
            });
    }

    [Fact]
    public async Task DouyinViewModelLoadsWorkspaceHistoryIndependentlyFromHistoryPageFilters()
    {
        using var context = CreateViewModelContext();
        await context.BatchContext.History.AddAsync(new DownloadHistory
        {
            Title = "数据库里的抖音视频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/db-video",
            Format = "mp4",
            DownloadTime = new DateTime(2026, 7, 3, 10, 0, 0)
        });
        await context.BatchContext.History.AddAsync(new DownloadHistory
        {
            Title = "数据库里的非抖音音频",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc",
            Format = "mp3",
            DownloadTime = new DateTime(2026, 7, 3, 11, 0, 0)
        });
        context.History.SearchKeyword = "no-match";
        context.History.SelectedMediaFilter = "音频";

        await context.Douyin.LoadDouyinWorkspaceCommand.ExecuteAsync(null);

        Assert.Equal("no-match", context.History.SearchKeyword);
        Assert.Equal("音频", context.History.SelectedMediaFilter);
        Assert.Equal(1, context.Douyin.DouyinArchiveCount);
        var item = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("数据库里的抖音视频", item.Title);
    }

    [Fact]
    public void DouyinViewModelSetsArchiveSearchFromRecentAuthorShortcut()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "作者作品",
            Platform = "Douyin",
            Url = "https://www.douyin.com/user/author",
            Format = "mp4",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                1,
                0,
                0,
                0,
                1,
                false,
                [
                    new DouyinManifestItem("author-video", "video", "视频", "作者作品", "作者 B", "2026-07-03", "", [], ["b.mp4"])
                ])
        });

        context.Douyin.SelectedDouyinArchiveTypeFilter = "音乐";

        context.Douyin.SetDouyinArchiveAuthorFilterCommand.Execute("作者 B");

        Assert.Equal("作者 B", context.Douyin.DouyinArchiveSearchKeyword);
        Assert.Equal("全部", context.Douyin.SelectedDouyinArchiveTypeFilter);
        var item = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("作者作品", item.Title);
    }

    [Fact]
    public void DouyinViewModelClassifiesLegacyArchiveItemsByFormatWhenManifestIsMissing()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "旧视频",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/legacy-video",
            Format = "mp4"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "旧图文",
            Platform = "Douyin",
            Url = "https://www.douyin.com/note/legacy-gallery",
            Format = "jpg"
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "旧音乐",
            Platform = "Douyin",
            Url = "https://www.douyin.com/music/legacy-music",
            Format = "mp3"
        });

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("视频");
        var video = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("旧视频", video.Title);

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("图文");
        var gallery = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("旧图文", gallery.Title);

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("音乐");
        var music = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("旧音乐", music.Title);
    }

    [Fact]
    public void DouyinViewModelDoesNotUseFormatFallbackWhenArchiveManifestExists()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "图文任务",
            Platform = "Douyin",
            Url = "https://www.douyin.com/note/gallery-task",
            Format = "mp4",
            DouyinManifestSummaryText = "作品 1 / 图文 1 / 附属 0",
            DouyinManifestSummary = new DouyinManifestSummary(
                1,
                1,
                0,
                1,
                0,
                0,
                2,
                false,
                [
                    new DouyinManifestItem(
                        "gallery-task",
                        "gallery",
                        "图文",
                        "这是真正的图文作品",
                        "作者 D",
                        "2026-07-04",
                        "",
                        ["图文"],
                        ["gallery_1.jpg", "gallery_2.jpg"])
                ])
        });

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("视频");

        Assert.Empty(context.Douyin.DouyinHistoryItems);
        Assert.Equal(0, context.Douyin.FilteredDouyinArchiveCount);
        Assert.Equal(1, context.Douyin.DouyinArchiveCount);
        Assert.True(context.Douyin.HasDouyinArchiveItems);
        Assert.False(context.Douyin.HasFilteredDouyinArchiveItems);
    }

    [Fact]
    public void DouyinViewModelUsesManifestSummaryCountsAndSearchTextBeyondDisplayedItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "批量主页",
            Platform = "Douyin",
            Url = "https://www.douyin.com/user/example",
            Format = "mp4",
            DouyinManifestSummaryText = "作品 25 / 视频 20 / 图文 5 / 附属 30",
            DouyinManifestSummary = new DouyinManifestSummary(
                25,
                25,
                20,
                5,
                0,
                0,
                30,
                true,
                [
                    new DouyinManifestItem(
                        "video-1",
                        "video",
                        "视频",
                        "展示列表里的视频",
                        "作者 E",
                        "2026-07-05",
                        "",
                        ["视频"],
                        ["video_1.mp4"])
                ],
                "hidden-gallery-25 第 25 条图文 作者 Z 隐藏标签")
        });

        context.Douyin.SetDouyinArchiveTypeFilterCommand.Execute("图文");

        var gallery = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("批量主页", gallery.Title);

        context.Douyin.DouyinArchiveSearchKeyword = "隐藏标签";

        var hiddenKeywordMatch = Assert.Single(context.Douyin.DouyinHistoryItems);
        Assert.Equal("批量主页", hiddenKeywordMatch.Title);
    }

    [Fact]
    public void DouyinViewModelExposesOnlyDouyinManifestSummaryItems()
    {
        using var context = CreateViewModelContext();

        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/123",
            DouyinManifestSummaryText = "作品 3 / 视频 1 / 图文 1 / 音乐 1 / 附属 2",
            DouyinManifestSummary = new DouyinManifestSummary(
                3,
                2,
                1,
                1,
                1,
                0,
                4,
                false,
                [
                    new DouyinManifestItem(
                        "v1",
                        "video",
                        "视频",
                        "视频标题",
                        "作者 A",
                        "2026-07-01",
                        "2026-07-03T10:00:00",
                        ["旅行", "美食"],
                        ["video.mp4"])
                ])
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "douyin without manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/456",
            DouyinManifestSummaryText = ""
        });
        context.History.HistoryItems.Add(new DownloadHistory
        {
            Title = "other manifest",
            Platform = "YouTube",
            Url = "https://youtube.com/watch?v=abc",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0"
        });

        Assert.Collection(
            context.Douyin.DouyinManifestSummaryItems,
            item =>
            {
                Assert.Equal("douyin manifest", item.Title);
                Assert.Equal("作品 3 / 视频 1 / 图文 1 / 音乐 1 / 附属 2", item.DouyinManifestSummaryText);
                Assert.True(item.HasDouyinManifestDetails);
                var detail = Assert.Single(item.DouyinManifestItems);
                Assert.Equal("视频", detail.MediaTypeText);
                Assert.Equal("视频标题", detail.Description);
                Assert.Equal("作者 A", detail.AuthorName);
                Assert.Equal("video.mp4", detail.FileNamesText);
            });
    }

    [Fact]
    public void DownloadHistoryNotifiesWhenStructuredDouyinManifestSummaryChanges()
    {
        var item = new DownloadHistory();
        var propertyNames = new List<string>();
        item.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName ?? "");

        item.DouyinManifestSummary = new DouyinManifestSummary(
            1,
            1,
            1,
            0,
            0,
            0,
            1,
            false,
            [
                new DouyinManifestItem(
                    "v1",
                    "video",
                    "视频",
                    "视频标题",
                    "作者 A",
                    "2026-07-01",
                    "",
                    [],
                    ["video.mp4"])
            ]);

        Assert.Contains(nameof(DownloadHistory.DouyinManifestSummary), propertyNames);
        Assert.Contains(nameof(DownloadHistory.DouyinManifestItems), propertyNames);
        Assert.Contains(nameof(DownloadHistory.HasDouyinManifestDetails), propertyNames);
        Assert.True(item.HasDouyinManifestDetails);
    }

    [Fact]
    public void DouyinViewModelNotifiesManifestSummaryCountChanges()
    {
        using var context = CreateViewModelContext();
        var notificationCount = 0;
        context.Douyin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DouyinViewModel.DouyinManifestSummaryCount))
                notificationCount++;
        };

        var item = new DownloadHistory
        {
            Title = "douyin manifest",
            Platform = "Douyin",
            Url = "https://www.douyin.com/video/123",
            DouyinManifestSummaryText = "作品 1 / 视频 1 / 附属 0"
        };

        context.History.HistoryItems.Add(item);

        Assert.Equal(1, context.Douyin.DouyinManifestSummaryCount);
        Assert.Equal(1, notificationCount);

        context.History.HistoryItems.Clear();

        Assert.Equal(0, context.Douyin.DouyinManifestSummaryCount);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public async Task DouyinViewModelLoadsHotBoardDiscoveryResults()
    {
        var discovery = new FakeDouyinDiscoveryService(
            new DouyinDiscoveryResult(
                "hot_board",
                @"D:\Videos\hot_board\20260704_120000.jsonl",
                1,
                [
                    new DouyinDiscoveryItem(Word: "猫咪", HotValue: 123)
                ],
                Limit: 30));
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.DefaultDownloadPath = @"D:\Videos";

        await context.Douyin.LoadDouyinHotBoardCommand.ExecuteAsync(null);

        Assert.NotNull(discovery.LastRequest);
        Assert.Equal(DouyinDiscoveryType.HotBoard, discovery.LastRequest.Type);
        Assert.Equal(@"D:\Videos", discovery.LastRequest.OutputDirectory);
        Assert.Equal(30, discovery.LastRequest.Limit);
        Assert.Equal(1, context.Douyin.DouyinDiscoveryResultCount);
        Assert.True(context.Douyin.HasDouyinDiscoveryItems);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        var item = Assert.Single(context.Douyin.DouyinDiscoveryItems);
        Assert.Equal("猫咪", item.Word);
        Assert.Equal(123, item.HotValue);
    }

    [Fact]
    public async Task DouyinViewModelSearchesDiscoveryKeyword()
    {
        var discovery = new FakeDouyinDiscoveryService(
            new DouyinDiscoveryResult(
                "search",
                @"D:\Videos\search\cat.jsonl",
                1,
                [
                    new DouyinDiscoveryItem(
                        AwemeId: "aweme-1",
                        Description: "猫咪晒太阳",
                        AuthorNickname: "Alice",
                        SecUid: "sec-a",
                        Url: "https://www.douyin.com/video/aweme-1")
                ],
                Keyword: "猫咪",
                SearchMax: 50));
        using var context = CreateViewModelContext(discovery);
        context.Douyin.DouyinDiscoveryKeyword = " 猫咪 ";
        context.Douyin.DouyinDiscoverySearchMax = 50;

        await context.Douyin.SearchDouyinDiscoveryCommand.ExecuteAsync(null);

        Assert.NotNull(discovery.LastRequest);
        Assert.Equal(DouyinDiscoveryType.Search, discovery.LastRequest.Type);
        Assert.Equal("猫咪", discovery.LastRequest.Keyword);
        Assert.Equal(50, discovery.LastRequest.SearchMax);
        var item = Assert.Single(context.Douyin.DouyinDiscoveryItems);
        Assert.Equal("aweme-1", item.AwemeId);
        Assert.Equal("猫咪晒太阳", item.Description);
        Assert.Equal("Alice", item.AuthorNickname);
    }

    [Fact]
    public async Task DouyinViewModelSearchesDiscoveryItemWord()
    {
        var discovery = new FakeDouyinDiscoveryService(
            new DouyinDiscoveryResult(
                "search",
                @"D:\Videos\search\cat.jsonl",
                1,
                [
                    new DouyinDiscoveryItem(
                        AwemeId: "7604129988555574538",
                        Description: "猫咪晒太阳",
                        Url: "https://www.douyin.com/video/7604129988555574538")
                ],
                Keyword: "猫咪",
                SearchMax: 12));
        using var context = CreateViewModelContext(discovery);
        context.Douyin.DouyinDiscoverySearchMax = 12;
        var hotWord = new DouyinDiscoveryItem(Word: " 猫咪 ", HotValue: 123);

        await context.Douyin.SearchDouyinDiscoveryItemWordCommand.ExecuteAsync(hotWord);

        Assert.Equal("猫咪", context.Douyin.DouyinDiscoveryKeyword);
        Assert.NotNull(discovery.LastRequest);
        Assert.Equal(DouyinDiscoveryType.Search, discovery.LastRequest.Type);
        Assert.Equal("猫咪", discovery.LastRequest.Keyword);
        Assert.Equal(12, discovery.LastRequest.SearchMax);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
        var item = Assert.Single(context.Douyin.DouyinDiscoveryItems);
        Assert.Equal("7604129988555574538", item.AwemeId);
    }

    [Fact]
    public async Task DouyinViewModelRejectsDiscoveryItemSearchWithoutWord()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var item = new DouyinDiscoveryItem(AwemeId: "7604129988555574538");

        await context.Douyin.SearchDouyinDiscoveryItemWordCommand.ExecuteAsync(item);

        Assert.Null(discovery.LastRequest);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("关键词", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
        Assert.Contains("无法搜索", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelReportsDiscoveryErrors()
    {
        var discovery = new FakeDouyinDiscoveryService(null, new InvalidOperationException("请先登录"));
        using var context = CreateViewModelContext(discovery);

        await context.Douyin.LoadDouyinHotBoardCommand.ExecuteAsync(null);

        Assert.Empty(context.Douyin.DouyinDiscoveryItems);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("请先登录", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelAddsSearchDiscoveryResultToDownloadQueue()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.EnableDouyinSpecialEngine = true;
        context.BatchContext.Config.Config.DefaultDownloadPath = @"D:\Videos";
        var item = new DouyinDiscoveryItem(
            AwemeId: "aweme-1",
            Description: "猫咪晒太阳",
            AuthorNickname: "Alice",
            SecUid: "sec-a",
            Url: "https://www.douyin.com/video/aweme-1");

        await context.Douyin.AddDouyinDiscoveryItemToQueueCommand.ExecuteAsync(item);

        var task = Assert.Single(context.BatchContext.Manager.Tasks);
        Assert.Equal("https://www.douyin.com/video/aweme-1", task.Url);
        Assert.Equal("Douyin", task.Platform);
        Assert.Equal("猫咪晒太阳", task.Title);
        Assert.Equal("mp4", task.Format);
        Assert.Equal("best", task.Quality);
        Assert.StartsWith(@"D:\Videos", task.OutputDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("已加入", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
    }

    [Fact]
    public async Task DouyinViewModelBuildsDownloadUrlFromDiscoveryAwemeId()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.EnableDouyinSpecialEngine = true;
        var item = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "只有作品 ID 的搜索结果");

        await context.Douyin.AddDouyinDiscoveryItemToQueueCommand.ExecuteAsync(item);

        var task = Assert.Single(context.BatchContext.Manager.Tasks);
        Assert.Equal("https://www.douyin.com/video/7604129988555574538", task.Url);
    }

    [Fact]
    public async Task DouyinViewModelAddsAllDownloadableDiscoveryResultsToQueue()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.EnableDouyinSpecialEngine = true;
        context.BatchContext.Config.Config.DefaultDownloadPath = @"D:\Videos";
        context.BatchContext.Manager.Tasks.Add(new DownloadTask
        {
            Url = "https://www.douyin.com/video/duplicate",
            Platform = "Douyin"
        });
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "duplicate",
            Description: "重复作品",
            Url: "https://www.douyin.com/video/duplicate"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪晒太阳"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "小狗散步",
            Url: "https://www.douyin.com/video/7604129988555574539"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(Word: "热词", HotValue: 123));

        await context.Douyin.AddAllDouyinDiscoveryItemsToQueueCommand.ExecuteAsync(null);

        Assert.Equal(3, context.BatchContext.Manager.Tasks.Count);
        Assert.Contains(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574538");
        Assert.Contains(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574539");
        Assert.Contains("已加入 2", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("跳过 1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("失败 1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("缺少可下载链接", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelAddsFilteredDiscoveryResultsToQueue()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.EnableDouyinSpecialEngine = true;
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪晒太阳"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "小狗散步",
            Url: "https://www.douyin.com/video/7604129988555574539"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(Word: "猫咪热词", HotValue: 123));

        context.Douyin.DouyinDiscoveryFilterKeyword = "猫咪";

        await context.Douyin.AddFilteredDouyinDiscoveryItemsToQueueCommand.ExecuteAsync(null);

        var task = Assert.Single(context.BatchContext.Manager.Tasks);
        Assert.Equal("https://www.douyin.com/video/7604129988555574538", task.Url);
        Assert.DoesNotContain(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574539");
        Assert.Contains("筛选入队完成", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("已加入 1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("失败 1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("缺少可下载链接", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelAddsSelectedDownloadableDiscoveryResultsToQueue()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.BatchContext.Config.Config.EnableDouyinSpecialEngine = true;
        context.BatchContext.Config.Config.DefaultDownloadPath = @"D:\Videos";
        var selectedByUrl = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪晒太阳",
            Url: "https://www.douyin.com/video/7604129988555574538");
        var selectedByAwemeId = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "小狗散步");
        var unselected = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574540",
            Description: "不应入队");
        context.Douyin.DouyinDiscoveryItems.Add(selectedByUrl);
        context.Douyin.DouyinDiscoveryItems.Add(selectedByAwemeId);
        context.Douyin.DouyinDiscoveryItems.Add(unselected);

        selectedByUrl.IsSelected = true;
        selectedByAwemeId.IsSelected = true;

        Assert.Equal(2, context.Douyin.SelectedDouyinDiscoveryItemCount);
        Assert.True(context.Douyin.HasSelectedDouyinDiscoveryItems);

        await context.Douyin.AddSelectedDouyinDiscoveryItemsToQueueCommand.ExecuteAsync(null);

        Assert.Equal(2, context.BatchContext.Manager.Tasks.Count);
        Assert.Contains(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574538");
        Assert.Contains(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574539");
        Assert.DoesNotContain(context.BatchContext.Manager.Tasks, task => task.Url == "https://www.douyin.com/video/7604129988555574540");
        Assert.Contains("选中入队完成", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Contains("已加入 2", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
    }

    [Fact]
    public async Task DouyinViewModelRejectsSelectedDiscoveryQueueWhenNothingIsSelected()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "未勾选作品"));

        await context.Douyin.AddSelectedDouyinDiscoveryItemsToQueueCommand.ExecuteAsync(null);

        Assert.Empty(context.BatchContext.Manager.Tasks);
        Assert.Equal(0, context.Douyin.SelectedDouyinDiscoveryItemCount);
        Assert.False(context.Douyin.HasSelectedDouyinDiscoveryItems);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("请先选择", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void DouyinViewModelSelectsOnlyDownloadableDiscoveryResults()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var withUrl = new DouyinDiscoveryItem(
            AwemeId: "not-numeric-but-url-is-usable",
            Description: "有链接作品",
            Url: "https://www.douyin.com/video/7604129988555574538");
        var withNumericAwemeId = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "只有数字作品 ID");
        var hotWord = new DouyinDiscoveryItem(Word: "热榜词", HotValue: 123);
        var nonNumericAwemeId = new DouyinDiscoveryItem(
            AwemeId: "not-a-video-id",
            Description: "不可构造链接");
        context.Douyin.DouyinDiscoveryItems.Add(withUrl);
        context.Douyin.DouyinDiscoveryItems.Add(withNumericAwemeId);
        context.Douyin.DouyinDiscoveryItems.Add(hotWord);
        context.Douyin.DouyinDiscoveryItems.Add(nonNumericAwemeId);

        context.Douyin.SelectDownloadableDouyinDiscoveryItemsCommand.Execute(null);

        Assert.True(withUrl.IsSelected);
        Assert.True(withNumericAwemeId.IsSelected);
        Assert.False(hotWord.IsSelected);
        Assert.False(nonNumericAwemeId.IsSelected);
        Assert.Equal(2, context.Douyin.SelectedDouyinDiscoveryItemCount);
        Assert.Contains("已选择 2", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void DouyinViewModelSelectsOnlyDownloadableFilteredDiscoveryResults()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var filteredDownloadable = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪视频",
            Url: "https://www.douyin.com/video/7604129988555574538");
        var filteredHotWord = new DouyinDiscoveryItem(
            Word: "猫咪热词",
            HotValue: 123);
        var outsideFilter = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "小狗视频",
            Url: "https://www.douyin.com/video/7604129988555574539")
        {
            IsSelected = true
        };
        context.Douyin.DouyinDiscoveryItems.Add(filteredDownloadable);
        context.Douyin.DouyinDiscoveryItems.Add(filteredHotWord);
        context.Douyin.DouyinDiscoveryItems.Add(outsideFilter);
        context.Douyin.DouyinDiscoveryFilterKeyword = "猫咪";

        context.Douyin.SelectDownloadableDouyinDiscoveryItemsCommand.Execute(null);

        Assert.True(filteredDownloadable.IsSelected);
        Assert.False(filteredHotWord.IsSelected);
        Assert.False(outsideFilter.IsSelected);
        Assert.Equal(1, context.Douyin.SelectedDouyinDiscoveryItemCount);
        Assert.Contains("已选择 1", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
    }

    [Fact]
    public void DouyinViewModelMarksDiscoveryItemsQueuedWhenMatchingTasksChange()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var directUrl = new DouyinDiscoveryItem(
            AwemeId: "not-numeric-but-url-is-usable",
            Description: "直接链接作品",
            Url: "https://www.douyin.com/video/7604129988555574538");
        var numericAweme = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "数字作品 ID");
        var hotWord = new DouyinDiscoveryItem(Word: "猫咪热词", HotValue: 123);
        context.Douyin.DouyinDiscoveryItems.Add(directUrl);
        context.Douyin.DouyinDiscoveryItems.Add(numericAweme);
        context.Douyin.DouyinDiscoveryItems.Add(hotWord);

        Assert.Equal("未入队", directUrl.QueueStateText);
        Assert.Equal("未入队", numericAweme.QueueStateText);
        Assert.Equal("不可入队", hotWord.QueueStateText);

        var existingTask = new DownloadTask { Url = "https://www.douyin.com/video/7604129988555574538" };
        context.BatchContext.Manager.Tasks.Add(existingTask);

        Assert.Equal("已在队列", directUrl.QueueStateText);
        Assert.Equal("未入队", numericAweme.QueueStateText);

        var generatedUrlTask = new DownloadTask { Url = "https://www.douyin.com/video/7604129988555574539" };
        context.BatchContext.Manager.Tasks.Add(generatedUrlTask);

        Assert.Equal("已在队列", numericAweme.QueueStateText);

        context.BatchContext.Manager.Tasks.Remove(existingTask);

        Assert.Equal("未入队", directUrl.QueueStateText);
        Assert.Equal("已在队列", numericAweme.QueueStateText);
    }

    [Fact]
    public void DouyinViewModelFiltersDiscoveryResultsLocally()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪晒太阳",
            AuthorNickname: "Alice",
            Url: "https://www.douyin.com/video/7604129988555574538"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "小狗散步",
            AuthorNickname: "Bob",
            Url: "https://www.douyin.com/video/7604129988555574539"));
        context.Douyin.DouyinDiscoveryItems.Add(new DouyinDiscoveryItem(Word: "热榜旅行", HotValue: 123));

        Assert.Equal(3, context.Douyin.DouyinDiscoveryResultCount);
        Assert.Equal(3, context.Douyin.FilteredDouyinDiscoveryResultCount);
        Assert.True(context.Douyin.HasFilteredDouyinDiscoveryItems);

        context.Douyin.DouyinDiscoveryFilterKeyword = "bob";

        var item = Assert.Single(context.Douyin.FilteredDouyinDiscoveryItems);
        Assert.Equal("小狗散步", item.Description);
        Assert.Equal(1, context.Douyin.FilteredDouyinDiscoveryResultCount);
        Assert.True(context.Douyin.IsDouyinDiscoveryFilterActive);

        context.Douyin.DouyinDiscoveryFilterKeyword = "旅行";

        var hotWord = Assert.Single(context.Douyin.FilteredDouyinDiscoveryItems);
        Assert.Equal("热榜旅行", hotWord.Word);

        context.Douyin.ClearDouyinDiscoveryFilterCommand.Execute(null);

        Assert.Equal("", context.Douyin.DouyinDiscoveryFilterKeyword);
        Assert.False(context.Douyin.IsDouyinDiscoveryFilterActive);
        Assert.Equal(3, context.Douyin.FilteredDouyinDiscoveryResultCount);
    }

    [Fact]
    public void DouyinViewModelSelectsFilteredDiscoveryResults()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var filteredByDescription = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "猫咪晒太阳",
            AuthorNickname: "Alice",
            Url: "https://www.douyin.com/video/7604129988555574538");
        var filteredByAuthor = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "午后散步",
            AuthorNickname: "猫咪观察员",
            Url: "https://www.douyin.com/video/7604129988555574539");
        var outsideFilter = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574540",
            Description: "小狗散步",
            AuthorNickname: "Bob",
            Url: "https://www.douyin.com/video/7604129988555574540")
        {
            IsSelected = true
        };
        context.Douyin.DouyinDiscoveryItems.Add(filteredByDescription);
        context.Douyin.DouyinDiscoveryItems.Add(outsideFilter);
        context.Douyin.DouyinDiscoveryItems.Add(filteredByAuthor);
        context.Douyin.DouyinDiscoveryFilterKeyword = "猫咪";

        context.Douyin.SelectFilteredDouyinDiscoveryItemsCommand.Execute(null);

        Assert.True(filteredByDescription.IsSelected);
        Assert.True(filteredByAuthor.IsSelected);
        Assert.False(outsideFilter.IsSelected);
        Assert.Equal(2, context.Douyin.SelectedDouyinDiscoveryItemCount);
        Assert.True(context.Douyin.HasSelectedDouyinDiscoveryItems);
        Assert.Contains("已选择 2", context.Douyin.DouyinDiscoveryStatusText, StringComparison.Ordinal);
        Assert.False(context.Douyin.HasDouyinDiscoveryError);
    }

    [Fact]
    public void DouyinViewModelSortsFilteredDiscoveryResultsLocally()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var hotItem = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574538",
            Description: "Beta video",
            AuthorNickname: "Charlie",
            HotValue: 300,
            Url: "https://www.douyin.com/video/7604129988555574538");
        var authorItem = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574539",
            Description: "Gamma video",
            AuthorNickname: "Alice",
            HotValue: 100,
            Url: "https://www.douyin.com/video/7604129988555574539");
        var descriptionItem = new DouyinDiscoveryItem(
            AwemeId: "7604129988555574540",
            Description: "Alpha video",
            AuthorNickname: "Bob",
            HotValue: 200,
            Url: "https://www.douyin.com/video/7604129988555574540");
        context.Douyin.DouyinDiscoveryItems.Add(authorItem);
        context.Douyin.DouyinDiscoveryItems.Add(descriptionItem);
        context.Douyin.DouyinDiscoveryItems.Add(hotItem);

        Assert.Equal(["默认", "热度高到低", "作者", "描述"], context.Douyin.DouyinDiscoverySortOptions);

        context.Douyin.SelectedDouyinDiscoverySortOption = "热度高到低";
        Assert.Equal(
            ["Beta video", "Alpha video", "Gamma video"],
            context.Douyin.FilteredDouyinDiscoveryItems.Select(item => item.Description).ToArray());

        context.Douyin.SelectedDouyinDiscoverySortOption = "作者";
        Assert.Equal(
            ["Alice", "Bob", "Charlie"],
            context.Douyin.FilteredDouyinDiscoveryItems.Select(item => item.AuthorNickname).ToArray());

        context.Douyin.DouyinDiscoveryFilterKeyword = "video";
        context.Douyin.SelectedDouyinDiscoverySortOption = "描述";
        Assert.Equal(
            ["Alpha video", "Beta video", "Gamma video"],
            context.Douyin.FilteredDouyinDiscoveryItems.Select(item => item.Description).ToArray());

        context.Douyin.SelectedDouyinDiscoverySortOption = "未知排序";
        Assert.Equal("默认", context.Douyin.SelectedDouyinDiscoverySortOption);
        Assert.Equal(
            ["Gamma video", "Alpha video", "Beta video"],
            context.Douyin.FilteredDouyinDiscoveryItems.Select(item => item.Description).ToArray());
    }

    [Fact]
    public async Task DouyinViewModelRejectsAddingAllDiscoveryResultsWhenListIsEmpty()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);

        await context.Douyin.AddAllDouyinDiscoveryItemsToQueueCommand.ExecuteAsync(null);

        Assert.Empty(context.BatchContext.Manager.Tasks);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("暂无发现结果", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelRejectsDiscoveryItemWithoutDownloadUrl()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var item = new DouyinDiscoveryItem(Word: "猫咪", HotValue: 123);

        await context.Douyin.AddDouyinDiscoveryItemToQueueCommand.ExecuteAsync(item);

        Assert.Empty(context.BatchContext.Manager.Tasks);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("缺少可下载链接", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DouyinViewModelRejectsDiscoveryItemWithNonNumericAwemeId()
    {
        var discovery = new FakeDouyinDiscoveryService();
        using var context = CreateViewModelContext(discovery);
        var item = new DouyinDiscoveryItem(AwemeId: "not-a-video-id");

        await context.Douyin.AddDouyinDiscoveryItemToQueueCommand.ExecuteAsync(item);

        Assert.Empty(context.BatchContext.Manager.Tasks);
        Assert.True(context.Douyin.HasDouyinDiscoveryError);
        Assert.Contains("缺少可下载链接", context.Douyin.DouyinDiscoveryErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1024L, "1 KB 可用")]
    [InlineData(1024L * 1024L * 1536L, "1.5 GB 可用")]
    public void HistoryViewModelFormatsDownloadDriveFreeSpace(long bytes, string expected)
    {
        Assert.Equal(expected, HistoryViewModel.FormatAvailableSpace(bytes));
    }

    private static ViewModelContext CreateViewModelContext(IDouyinSpecialDownloadService? douyinSpecialDownloadService = null)
    {
        var batchContext = CreateBatchContext(douyinSpecialDownloadService);
        var settings = new SettingsViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            new TelegramDownloadService(batchContext.Config));
        var download = new DownloadViewModel(
            batchContext.Manager,
            batchContext.Config,
            new YtDlpVideoInfoProvider(batchContext.YtDlp));
        var history = new HistoryViewModel(batchContext.History, batchContext.Config);
        var douyin = new DouyinViewModel(
            batchContext.Config,
            batchContext.Manager,
            download,
            batchContext.Batch,
            history,
            settings,
            douyinSpecialDownloadService);
        var main = new MainViewModel(
            batchContext.Config,
            batchContext.Environment,
            batchContext.Manager,
            download,
            batchContext.Batch,
            history,
            douyin,
            settings);

        return new ViewModelContext(batchContext, download, history, douyin, settings, main);
    }

    private sealed class FakeDouyinDiscoveryService(
        DouyinDiscoveryResult? result = null,
        Exception? exception = null) : IDouyinSpecialDownloadService
    {
        public DouyinDiscoveryRequest? LastRequest { get; private set; }
        public List<DownloadTask> DownloadedTasks { get; } = [];

        public Task DownloadAsync(
            DownloadTask task,
            AppConfig config,
            IProgress<DownloadProgress>? progress = null,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedTasks.Add(task);
            task.Progress = 100;
            task.Status = DownloadStatus.Completed;
            return Task.CompletedTask;
        }

        public Task<DouyinDiscoveryResult> DiscoverAsync(
            DouyinDiscoveryRequest request,
            AppConfig config,
            Action<string>? logCallback = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (exception is not null)
                throw exception;

            return Task.FromResult(result!);
        }
    }

    private static BatchContext CreateBatchContext(IDouyinSpecialDownloadService? douyinSpecialDownloadService = null)
    {
        var config = new ConfigService();
        var environment = new EnvironmentService();
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        var history = new HistoryService(historyPath);
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(
            ytDlp,
            history,
            config,
            douyinSpecialDownloadService: douyinSpecialDownloadService);
        var batch = new BatchDownloadViewModel(manager, config, ytDlp);

        return new BatchContext(config, environment, history, ytDlp, manager, batch);
    }

    private sealed record BatchContext(
        ConfigService Config,
        EnvironmentService Environment,
        HistoryService History,
        YtDlpService YtDlp,
        DownloadManager Manager,
        BatchDownloadViewModel Batch) : IDisposable
    {
        public void Dispose() => History.Dispose();
    }

    private sealed record ViewModelContext(
        BatchContext BatchContext,
        DownloadViewModel Download,
        HistoryViewModel History,
        DouyinViewModel Douyin,
        SettingsViewModel Settings,
        MainViewModel Main) : IDisposable
    {
        public void Dispose() => BatchContext.Dispose();
    }
}
