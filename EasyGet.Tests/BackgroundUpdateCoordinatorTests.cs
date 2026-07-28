using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class BackgroundUpdateCoordinatorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"easyget-update-coordinator-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckIfDueAsync_ChecksAndPersistsTimestamp()
    {
        var now = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var config = new ConfigService(_tempDirectory);
        var updater = new FakeUpdateService();
        var coordinator = new BackgroundUpdateCoordinator(updater, config, () => now);

        var result = await coordinator.CheckIfDueAsync();

        Assert.NotNull(result);
        Assert.Equal(1, updater.CheckCount);
        Assert.Equal(now, config.Config.LastAutomaticUpdateCheckUtc);
    }

    [Fact]
    public async Task CheckIfDueAsync_SkipsRecentOrDisabledChecks()
    {
        var now = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var config = new ConfigService(_tempDirectory);
        config.Config.LastAutomaticUpdateCheckUtc = now.AddHours(-2);
        var updater = new FakeUpdateService();
        var coordinator = new BackgroundUpdateCoordinator(updater, config, () => now);

        Assert.Null(await coordinator.CheckIfDueAsync());
        config.Config.AutomaticUpdateChecksEnabled = false;
        config.Config.LastAutomaticUpdateCheckUtc = null;
        Assert.Null(await coordinator.CheckIfDueAsync());
        Assert.Equal(0, updater.CheckCount);
    }

    [Fact]
    public void CleanupStalePackages_RemovesOnlyExpiredInstallerArtifacts()
    {
        var now = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_tempDirectory);
        var staleInstaller = WriteFile("EasyGet-Setup-v1.0.0.exe", now.AddDays(-31));
        var stalePartial = WriteFile("EasyGet-Setup-v1.0.1.exe.download", now.AddDays(-2));
        var currentInstaller = WriteFile("EasyGet-Setup-v1.0.2.exe", now.AddDays(-2));
        var unrelated = WriteFile("notes.txt", now.AddDays(-60));

        var count = BackgroundUpdateCoordinator.CleanupStalePackages(_tempDirectory, now);

        Assert.Equal(2, count);
        Assert.False(File.Exists(staleInstaller));
        Assert.False(File.Exists(stalePartial));
        Assert.True(File.Exists(currentInstaller));
        Assert.True(File.Exists(unrelated));
    }

    private string WriteFile(string name, DateTimeOffset modified)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, "test");
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, true);
        }
        catch
        {
        }
    }

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public int CheckCount { get; private set; }
        public string CurrentVersion => "1.0.0";
        public string CurrentExecutablePath => "EasyGet.exe";
        public string RuntimeDescription => "test";

        public Task<AppUpdateInfo> CheckLatestAsync(CancellationToken ct = default)
        {
            CheckCount++;
            return Task.FromResult(new AppUpdateInfo
            {
                CurrentVersion = "1.0.0",
                LatestVersion = "1.1.0",
                IsUpdateAvailable = true
            });
        }

        public Task<string> DownloadInstallerAsync(
            AppUpdateInfo updateInfo,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public bool LaunchInstaller(string installerPath) => false;
    }
}
