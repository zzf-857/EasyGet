using EasyGet.ViewModels;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class SettingsViewModelTests
{
    [Theory]
    [InlineData("", true, "检测中")]
    [InlineData("正在安装 yt-dlp...", true, "准备安装")]
    [InlineData("yt-dlp 下载中... 45%", true, "下载中")]
    [InlineData("正在解压 ffmpeg...", true, "解压中")]
    [InlineData("ffmpeg 安装完成。", true, "完成")]
    [InlineData("环境安装完成。", false, "完成")]
    [InlineData("环境安装未完成，请检查网络或手动安装。", false, "失败")]
    [InlineData("安装失败: 网络超时", false, "失败")]
    [InlineData("", false, "")]
    public void DescribeInstallStatusStage_ClassifiesInstallProgress(string message, bool isInstalling, string expectedStage)
    {
        Assert.Equal(expectedStage, SettingsViewModel.DescribeInstallStatusStage(message, isInstalling));
    }

    [Fact]
    public async Task CheckAppUpdateCommand_SetsAvailableUpdateState()
    {
        var service = new FakeAppUpdateService
        {
            NextUpdate = new AppUpdateInfo
            {
                CurrentVersion = "1.0.0",
                LatestVersion = "1.1.0",
                IsUpdateAvailable = true,
                InstallerFileName = "EasyGet-Setup-v1.1.0.exe",
                InstallerDownloadUrl = new Uri("https://example.com/EasyGet-Setup-v1.1.0.exe")
            }
        };
        var viewModel = CreateViewModel(service);

        await viewModel.CheckAppUpdateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsAppUpdateAvailable);
        Assert.False(viewModel.IsAppUpdateDownloaded);
        Assert.Equal("1.1.0", viewModel.LatestAppVersion);
        Assert.Contains("发现新版本", viewModel.AppUpdateStatusMessage);
        Assert.Equal("安装版运行", viewModel.AppRuntimeText);
        Assert.True(viewModel.CanDownloadAppUpdate);
    }

    [Fact]
    public async Task DownloadAppUpdateCommand_TracksProgressAndDownloadedInstaller()
    {
        var service = new FakeAppUpdateService
        {
            NextUpdate = new AppUpdateInfo
            {
                CurrentVersion = "1.0.0",
                LatestVersion = "1.1.0",
                IsUpdateAvailable = true,
                InstallerFileName = "EasyGet-Setup-v1.1.0.exe",
                InstallerDownloadUrl = new Uri("https://example.com/EasyGet-Setup-v1.1.0.exe")
            },
            DownloadedPath = @"C:\Temp\EasyGet-Setup-v1.1.0.exe"
        };
        var viewModel = CreateViewModel(service);
        await viewModel.CheckAppUpdateCommand.ExecuteAsync(null);

        await viewModel.DownloadAppUpdateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsAppUpdateDownloaded);
        Assert.Equal(100, viewModel.AppUpdateProgress);
        Assert.Contains("已下载", viewModel.AppUpdateStatusMessage);
        Assert.True(viewModel.CanInstallAppUpdate);
    }

    [Fact]
    public void Initialize_LoadsDouyinSpecialSettingsFromConfig()
    {
        var config = CreateTempConfigService();
        config.Config.EnableDouyinSpecialEngine = true;
        config.Config.DouyinMode = "post";
        config.Config.DouyinLimit = 12;
        config.Config.DouyinDownloadCover = true;
        config.Config.DouyinDownloadMusic = true;
        config.Config.DouyinDownloadJson = true;
        SetAppConfigString(config.Config, "DouyinStartTime", "2024-01-01");
        SetAppConfigString(config.Config, "DouyinEndTime", "2024-01-31");
        SetAppConfigBool(config.Config, "DouyinDownloadComments", value: true);
        SetAppConfigBool(config.Config, "DouyinDownloadAvatar", value: true);
        SetAppConfigBool(config.Config, "DouyinEnableDatabase", value: true);
        SetAppConfigBool(config.Config, "DouyinIncrementalDownload", value: true);
        SetAppConfigBool(config.Config, "DouyinDownloadPinned", value: true);
        SetAppConfigString(config.Config, "DouyinAuthorDirectoryMode", "sec_uid");
        SetAppConfigBool(config.Config, "DouyinGroupByMode", value: false);
        SetAppConfigBool(config.Config, "DouyinCommentIncludeReplies", value: true);
        SetAppConfigInt(config.Config, "DouyinMaxComments", 500);
        SetAppConfigInt(config.Config, "DouyinCommentPageSize", 12);
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());

        viewModel.Initialize();

        Assert.True(viewModel.EnableDouyinSpecialEngine);
        Assert.Equal("post", viewModel.DouyinMode);
        Assert.Equal(12, viewModel.DouyinLimit);
        Assert.True(viewModel.DouyinDownloadCover);
        Assert.True(viewModel.DouyinDownloadMusic);
        Assert.True(viewModel.DouyinDownloadJson);
        AssertViewModelString(viewModel, "DouyinStartTime", "2024-01-01");
        AssertViewModelString(viewModel, "DouyinEndTime", "2024-01-31");
        AssertViewModelBool(viewModel, "DouyinDownloadComments", expected: true);
        AssertViewModelBool(viewModel, "DouyinDownloadAvatar", expected: true);
        AssertViewModelBool(viewModel, "DouyinEnableDatabase", expected: true);
        AssertViewModelBool(viewModel, "DouyinIncrementalDownload", expected: true);
        AssertViewModelBool(viewModel, "DouyinDownloadPinned", expected: true);
        AssertViewModelString(viewModel, "DouyinAuthorDirectoryMode", "sec_uid");
        AssertViewModelBool(viewModel, "DouyinGroupByMode", expected: false);
        AssertViewModelBool(viewModel, "DouyinCommentIncludeReplies", expected: true);
        AssertViewModelInt(viewModel, "DouyinMaxComments", 500);
        AssertViewModelInt(viewModel, "DouyinCommentPageSize", 12);
        Assert.Equal(
            ["post", "like", "mix", "music", "post,like,mix,music", "collect", "collectmix"],
            viewModel.DouyinModeOptions);
        Assert.Equal(
            ["nickname", "sec_uid", "nickname_uid", "user_sec_uid"],
            viewModel.DouyinAuthorDirectoryModeOptions);
    }

    [Fact]
    public void Initialize_LoadsDouyinTemplateSettingsFromConfig()
    {
        var config = CreateTempConfigService();
        SetAppConfigString(config.Config, "DouyinFilenameTemplate", "{author}_{title}_{id}");
        SetAppConfigString(config.Config, "DouyinFolderTemplate", "{date}_{id}");
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());

        viewModel.Initialize();

        AssertViewModelString(viewModel, "DouyinFilenameTemplate", "{author}_{title}_{id}");
        AssertViewModelString(viewModel, "DouyinFolderTemplate", "{date}_{id}");
    }

    [Fact]
    public void DouyinTemplatePreviewReflectsCurrentTemplatesAndVariables()
    {
        var viewModel = CreateViewModel(new FakeAppUpdateService());

        SetViewModelString(viewModel, "DouyinFilenameTemplate", "{author}_{title}_{id}");
        SetViewModelString(viewModel, "DouyinFolderTemplate", "{date}_{mode}_{id}");

        Assert.Equal("示例：示例作者_今天去爬山啦_7412345678901234567", viewModel.DouyinFilenameTemplatePreviewText);
        Assert.Equal("示例：2026-04-10_post_7412345678901234567", viewModel.DouyinFolderTemplatePreviewText);
        Assert.Contains("{id}", viewModel.DouyinTemplateVariablesText, StringComparison.Ordinal);
        Assert.Contains("{title}", viewModel.DouyinTemplateVariablesText, StringComparison.Ordinal);
        Assert.Contains("{mode}", viewModel.DouyinTemplateVariablesText, StringComparison.Ordinal);

        SetViewModelString(viewModel, "DouyinFilenameTemplate", "{title}");

        Assert.Equal("示例：2026-04-10_今天去爬山啦_7412345678901234567", viewModel.DouyinFilenameTemplatePreviewText);
    }

    [Fact]
    public async Task SaveSettingsCommand_PersistsDouyinSpecialSettings()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        viewModel.EnableDouyinSpecialEngine = true;
        viewModel.DouyinMode = "post";
        viewModel.DouyinLimit = 8;
        viewModel.DouyinDownloadCover = true;
        viewModel.DouyinDownloadMusic = true;
        viewModel.DouyinDownloadJson = true;
        SetViewModelString(viewModel, "DouyinStartTime", " 2024-01-01 ");
        SetViewModelString(viewModel, "DouyinEndTime", " 2024-01-31 ");
        SetViewModelBool(viewModel, "DouyinDownloadComments", value: true);
        SetViewModelBool(viewModel, "DouyinDownloadAvatar", value: true);
        SetViewModelBool(viewModel, "DouyinEnableDatabase", value: true);
        SetViewModelBool(viewModel, "DouyinIncrementalDownload", value: true);
        SetViewModelBool(viewModel, "DouyinDownloadPinned", value: true);
        SetViewModelString(viewModel, "DouyinAuthorDirectoryMode", " SEC_UID ");
        SetViewModelBool(viewModel, "DouyinGroupByMode", value: false);
        SetViewModelBool(viewModel, "DouyinCommentIncludeReplies", value: true);
        SetViewModelInt(viewModel, "DouyinMaxComments", 500);
        SetViewModelInt(viewModel, "DouyinCommentPageSize", 99);

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.True(config.Config.EnableDouyinSpecialEngine);
        Assert.Equal("post", config.Config.DouyinMode);
        Assert.Equal(8, config.Config.DouyinLimit);
        Assert.True(config.Config.DouyinDownloadCover);
        Assert.True(config.Config.DouyinDownloadMusic);
        Assert.True(config.Config.DouyinDownloadJson);
        AssertAppConfigString(config.Config, "DouyinStartTime", "2024-01-01");
        AssertAppConfigString(config.Config, "DouyinEndTime", "2024-01-31");
        AssertAppConfigBool(config.Config, "DouyinDownloadComments", expected: true);
        AssertAppConfigBool(config.Config, "DouyinDownloadAvatar", expected: true);
        AssertAppConfigBool(config.Config, "DouyinEnableDatabase", expected: true);
        AssertAppConfigBool(config.Config, "DouyinIncrementalDownload", expected: true);
        AssertAppConfigBool(config.Config, "DouyinDownloadPinned", expected: true);
        AssertAppConfigString(config.Config, "DouyinAuthorDirectoryMode", "sec_uid");
        AssertAppConfigBool(config.Config, "DouyinGroupByMode", expected: false);
        AssertViewModelString(viewModel, "DouyinAuthorDirectoryMode", "sec_uid");
        AssertViewModelBool(viewModel, "DouyinGroupByMode", expected: false);
        AssertAppConfigBool(config.Config, "DouyinCommentIncludeReplies", expected: true);
        AssertAppConfigInt(config.Config, "DouyinMaxComments", 500);
        AssertAppConfigInt(config.Config, "DouyinCommentPageSize", 20);
        AssertViewModelBool(viewModel, "DouyinCommentIncludeReplies", expected: true);
        AssertViewModelInt(viewModel, "DouyinMaxComments", 500);
        AssertViewModelInt(viewModel, "DouyinCommentPageSize", 20);
    }

    [Fact]
    public async Task SaveSettingsCommand_PersistsDouyinTemplatesAndSyncsNormalizedValues()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        SetViewModelString(viewModel, "DouyinFilenameTemplate", " {author}_{title}_{id} ");
        SetViewModelString(viewModel, "DouyinFolderTemplate", "{date}_{title}");

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        AssertAppConfigString(config.Config, "DouyinFilenameTemplate", "{author}_{title}_{id}");
        AssertAppConfigString(config.Config, "DouyinFolderTemplate", "{date}_{title}_{id}");
        AssertViewModelString(viewModel, "DouyinFilenameTemplate", "{author}_{title}_{id}");
        AssertViewModelString(viewModel, "DouyinFolderTemplate", "{date}_{title}_{id}");
    }

    [Fact]
    public async Task DouyinDownloadPinnedChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelBool(viewModel, "DouyinDownloadPinned", value: true);

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigBool(config.Config, "DouyinDownloadPinned", expected: true);
    }

    [Fact]
    public async Task DouyinGroupByModeChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelBool(viewModel, "DouyinGroupByMode", value: false);

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigBool(config.Config, "DouyinGroupByMode", expected: false);
    }

    [Fact]
    public async Task DouyinAuthorDirectoryModeChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelString(viewModel, "DouyinAuthorDirectoryMode", "nickname_uid");

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigString(config.Config, "DouyinAuthorDirectoryMode", "nickname_uid");
    }

    [Fact]
    public async Task DouyinCommentOptionsChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelInt(viewModel, "DouyinMaxComments", 250);

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigInt(config.Config, "DouyinMaxComments", 250);
    }

    [Fact]
    public async Task DouyinCommentIncludeRepliesChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelBool(viewModel, "DouyinCommentIncludeReplies", value: true);

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigBool(config.Config, "DouyinCommentIncludeReplies", expected: true);
    }

    [Fact]
    public async Task DouyinCommentPageSizeChange_AutoSavesSetting()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SettingsSaved += () => saved.TrySetResult();

        SetViewModelInt(viewModel, "DouyinCommentPageSize", 12);

        var completed = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(saved.Task, completed);
        AssertAppConfigInt(config.Config, "DouyinCommentPageSize", 12);
    }

    [Theory]
    [InlineData("post")]
    [InlineData("like")]
    [InlineData("mix")]
    [InlineData("music")]
    [InlineData("collect")]
    [InlineData("collectmix")]
    public async Task SaveSettingsCommand_PersistsSupportedDouyinUserModes(string mode)
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        viewModel.EnableDouyinSpecialEngine = true;
        viewModel.DouyinMode = mode;
        viewModel.DouyinLimit = 4;

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(mode, config.Config.DouyinMode);
        Assert.Equal(4, config.Config.DouyinLimit);
    }

    [Fact]
    public async Task SaveSettingsCommand_PersistsNormalizedDouyinMultiMode()
    {
        var config = CreateTempConfigService();
        var viewModel = CreateViewModel(config, new FakeAppUpdateService());
        viewModel.EnableDouyinSpecialEngine = true;
        viewModel.DouyinMode = " like, mix , music ";
        viewModel.DouyinLimit = 4;

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("like,mix,music", config.Config.DouyinMode);
        Assert.Equal("like,mix,music", viewModel.DouyinMode);
        Assert.Equal(4, config.Config.DouyinLimit);
    }

    private static SettingsViewModel CreateViewModel(IAppUpdateService appUpdateService)
        => CreateViewModel(new ConfigService(), appUpdateService);

    private static SettingsViewModel CreateViewModel(ConfigService config, IAppUpdateService appUpdateService)
    {
        var environment = new EnvironmentService();
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "history.db");
        var history = new HistoryService(historyPath);
        var ytDlp = new YtDlpService(config, environment);
        var manager = new DownloadManager(ytDlp, history, config);

        return new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            appUpdateService);
    }

    private static ConfigService CreateTempConfigService()
        => new(Path.Combine(
            Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"),
            "config"));

    private static void AssertAppConfigBool(AppConfig config, string propertyName, bool expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<bool>(property!.GetValue(config)));
    }

    private static void AssertAppConfigInt(AppConfig config, string propertyName, int expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<int>(property!.GetValue(config)));
    }

    private static void AssertAppConfigString(AppConfig config, string propertyName, string expected)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<string>(property!.GetValue(config)));
    }

    private static void SetAppConfigBool(AppConfig config, string propertyName, bool value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void SetAppConfigInt(AppConfig config, string propertyName, int value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void SetAppConfigString(AppConfig config, string propertyName, string value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void AssertViewModelBool(SettingsViewModel viewModel, string propertyName, bool expected)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<bool>(property!.GetValue(viewModel)));
    }

    private static void AssertViewModelInt(SettingsViewModel viewModel, string propertyName, int expected)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<int>(property!.GetValue(viewModel)));
    }

    private static void AssertViewModelString(SettingsViewModel viewModel, string propertyName, string expected)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expected, Assert.IsType<string>(property!.GetValue(viewModel)));
    }

    private static void SetViewModelBool(SettingsViewModel viewModel, string propertyName, bool value)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(viewModel, value);
    }

    private static void SetViewModelInt(SettingsViewModel viewModel, string propertyName, int value)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(viewModel, value);
    }

    private static void SetViewModelString(SettingsViewModel viewModel, string propertyName, string value)
    {
        var property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(viewModel, value);
    }

    private sealed class FakeAppUpdateService : IAppUpdateService
    {
        public string CurrentVersion { get; init; } = "1.0.0";
        public string CurrentExecutablePath { get; init; } = @"C:\Program Files\EasyGet\EasyGet.exe";
        public string RuntimeDescription { get; init; } = "安装版运行";

        public AppUpdateInfo NextUpdate { get; init; } = new();

        public string DownloadedPath { get; init; } = "";

        public Task<AppUpdateInfo> CheckLatestAsync(CancellationToken ct = default)
            => Task.FromResult(NextUpdate);

        public Task<string> DownloadInstallerAsync(
            AppUpdateInfo updateInfo,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(100);
            return Task.FromResult(DownloadedPath);
        }

        public bool LaunchInstaller(string installerPath) => true;
    }
}
