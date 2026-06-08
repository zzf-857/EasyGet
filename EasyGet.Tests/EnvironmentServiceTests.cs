using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class EnvironmentServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"easyget-tests-{Guid.NewGuid():N}");

    [Fact]
    public void GetMissingToolNames_ReturnsOnlyToolsNotFound()
    {
        var status = new EnvironmentStatus
        {
            YtDlpFound = false,
            FfmpegFound = true
        };

        var missing = EnvironmentService.GetMissingToolNames(status);

        Assert.Equal(["yt-dlp"], missing);
    }

    [Fact]
    public void GetToolDownloadUri_UsesStableOfficialReleaseEndpoints()
    {
        Assert.Equal(
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
            EnvironmentService.GetToolDownloadUri("yt-dlp").ToString());

        Assert.Equal(
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            EnvironmentService.GetToolDownloadUri("ffmpeg").ToString());
    }

    [Fact]
    public void FindExecutableInDirectoryTree_FindsFfmpegInsideExtractedZipLayout()
    {
        var binDir = Path.Combine(_tempDir, "ffmpeg-release-essentials", "bin");
        Directory.CreateDirectory(binDir);
        var expectedPath = Path.Combine(binDir, "ffmpeg.exe");
        File.WriteAllText(expectedPath, "fake exe");

        var foundPath = EnvironmentService.FindExecutableInDirectoryTree(_tempDir, "ffmpeg.exe");

        Assert.Equal(expectedPath, foundPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
