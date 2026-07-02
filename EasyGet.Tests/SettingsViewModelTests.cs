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

    private static SettingsViewModel CreateViewModel(IAppUpdateService appUpdateService)
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
        var manager = new DownloadManager(ytDlp, history, config);

        return new SettingsViewModel(
            config,
            environment,
            manager,
            new TelegramDownloadService(config),
            appUpdateService);
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
