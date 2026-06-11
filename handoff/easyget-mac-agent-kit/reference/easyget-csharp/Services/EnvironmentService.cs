using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace EasyGet.Services;

public class EnvironmentStatus
{
    public bool YtDlpFound { get; set; }
    public string YtDlpVersion { get; set; } = "";
    public string YtDlpPath { get; set; } = "";

    public bool FfmpegFound { get; set; }
    public string FfmpegVersion { get; set; } = "";
    public string FfmpegPath { get; set; } = "";

    public bool IsReady => YtDlpFound && FfmpegFound;
}

public class EnvironmentService
{
    private static readonly HttpClient HttpClient = new();

    public EnvironmentStatus Status { get; private set; } = new();

    public async Task<EnvironmentStatus> CheckEnvironmentAsync()
    {
        Status = new EnvironmentStatus();

        var (ytFound, ytVer, ytPath) = await CheckToolAsync("yt-dlp", "--version");
        Status.YtDlpFound = ytFound;
        Status.YtDlpVersion = ytVer;
        Status.YtDlpPath = ytPath;

        var (ffFound, ffVer, ffPath) = await CheckToolAsync("ffmpeg", "-version");
        Status.FfmpegFound = ffFound;
        Status.FfmpegVersion = ffVer;
        Status.FfmpegPath = ffPath;

        return Status;
    }

    public async Task<EnvironmentStatus> InstallMissingToolsAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var currentStatus = Status.YtDlpFound || Status.FfmpegFound
            ? Status
            : await CheckEnvironmentAsync();

        var missingTools = GetMissingToolNames(currentStatus);
        if (missingTools.Count == 0)
        {
            log?.Report("环境已就绪，无需安装。");
            return Status;
        }

        Directory.CreateDirectory(ConfigService.GetToolsDirectory());

        foreach (var tool in missingTools)
        {
            log?.Report($"正在安装 {tool}...");
            if (tool == "yt-dlp")
                await InstallYtDlpAsync(log, ct);
            else if (tool == "ffmpeg")
                await InstallFfmpegAsync(log, ct);
        }

        var updated = await CheckEnvironmentAsync();
        log?.Report(updated.IsReady ? "环境安装完成。" : "环境安装未完成，请检查网络或手动安装。");
        return updated;
    }

    public async Task<bool> UpdateYtDlpAsync(IProgress<string>? log = null)
    {
        try
        {
            if (!Status.YtDlpFound)
            {
                log?.Report("yt-dlp 未安装。请先安装运行环境。");
                return false;
            }

            log?.Report("Updating yt-dlp...");
            var output = await RunCommandAsync(Status.YtDlpPath, "-U");
            if (!string.IsNullOrWhiteSpace(output))
                log?.Report(output.Trim());

            await CheckEnvironmentAsync();
            log?.Report(Status.YtDlpFound
                ? $"yt-dlp current version: {Status.YtDlpVersion}"
                : "yt-dlp update failed.");

            return Status.YtDlpFound;
        }
        catch (Exception ex)
        {
            log?.Report($"yt-dlp update failed: {ex.Message}");
            return false;
        }
    }

    internal static IReadOnlyList<string> GetMissingToolNames(EnvironmentStatus status)
    {
        var missing = new List<string>();
        if (!status.YtDlpFound)
            missing.Add("yt-dlp");
        if (!status.FfmpegFound)
            missing.Add("ffmpeg");
        return missing;
    }

    internal static Uri GetToolDownloadUri(string tool)
    {
        return tool.ToLowerInvariant() switch
        {
            "yt-dlp" => new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"),
            "ffmpeg" => new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool")
        };
    }

    internal static string? FindExecutableInDirectoryTree(string rootDirectory, string executableName)
    {
        if (!Directory.Exists(rootDirectory))
            return null;

        return Directory.EnumerateFiles(rootDirectory, executableName, SearchOption.AllDirectories)
            .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    private static async Task InstallYtDlpAsync(IProgress<string>? log, CancellationToken ct)
    {
        var targetPath = Path.Combine(ConfigService.GetToolsDirectory(), "yt-dlp.exe");
        var tempPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-{Guid.NewGuid():N}.exe");

        try
        {
            await DownloadFileAsync(GetToolDownloadUri("yt-dlp"), tempPath, "yt-dlp", log, ct);
            File.Copy(tempPath, targetPath, overwrite: true);
            log?.Report("yt-dlp 安装完成。");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task InstallFfmpegAsync(IProgress<string>? log, CancellationToken ct)
    {
        var toolsDir = ConfigService.GetToolsDirectory();
        var zipPath = Path.Combine(Path.GetTempPath(), $"easyget-ffmpeg-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"easyget-ffmpeg-{Guid.NewGuid():N}");

        try
        {
            await DownloadFileAsync(GetToolDownloadUri("ffmpeg"), zipPath, "ffmpeg", log, ct);
            log?.Report("正在解压 ffmpeg...");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var ffmpegPath = FindExecutableInDirectoryTree(extractDir, "ffmpeg.exe");
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                throw new FileNotFoundException("未能在 ffmpeg 压缩包中找到 ffmpeg.exe。");

            File.Copy(ffmpegPath, Path.Combine(toolsDir, "ffmpeg.exe"), overwrite: true);

            var ffprobePath = FindExecutableInDirectoryTree(extractDir, "ffprobe.exe");
            if (!string.IsNullOrWhiteSpace(ffprobePath))
                File.Copy(ffprobePath, Path.Combine(toolsDir, "ffprobe.exe"), overwrite: true);

            log?.Report("ffmpeg 安装完成。");
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDir);
        }
    }

    private static async Task DownloadFileAsync(Uri uri, string targetPath, string toolName, IProgress<string>? log, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(targetPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        var lastPercent = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Floor(totalRead * 100d / totalBytes.Value);
                if (percent >= lastPercent + 5 || percent == 100)
                {
                    lastPercent = percent;
                    log?.Report($"{toolName} 下载中... {percent}%");
                }
            }
        }
    }

    private async Task<(bool found, string version, string path)> CheckToolAsync(string tool, string versionArg)
    {
        var toolsDir = ConfigService.GetToolsDirectory();
        var localPath = Path.Combine(toolsDir, $"{tool}.exe");
        if (File.Exists(localPath))
        {
            var ver = await GetVersionAsync(localPath, versionArg);
            if (!string.IsNullOrWhiteSpace(ver))
                return (true, ver, localPath);
        }

        try
        {
            var ver = await GetVersionAsync(tool, versionArg);
            if (string.IsNullOrWhiteSpace(ver))
                return (false, "", "");

            var path = tool;
            var envPath = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                foreach (var p in envPath.Split(Path.PathSeparator))
                {
                    var dir = p.Trim('"', ' ');
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    var fullPath = Path.Combine(dir, $"{tool}.exe");
                    if (File.Exists(fullPath))
                    {
                        path = fullPath;
                        break;
                    }
                }
            }

            return (true, ver, path);
        }
        catch
        {
            return (false, "", "");
        }
    }

    private async Task<string> GetVersionAsync(string tool, string versionArg)
    {
        try
        {
            var output = await RunCommandAsync(tool, versionArg);
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

            if (firstLine.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? parts[2] : firstLine;
            }

            return firstLine;
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> RunCommandAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略临时文件清理失败。
        }
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
            // 忽略临时目录清理失败。
        }
    }
}
