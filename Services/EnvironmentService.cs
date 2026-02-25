using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace EasyGet.Services;

/// <summary>
/// 环境检测状态
/// </summary>
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

/// <summary>
/// 环境检测与自动安装服务
/// </summary>
public class EnvironmentService
{
    private readonly HttpClient _httpClient = new();

    /// <summary>当前环境状态</summary>
    public EnvironmentStatus Status { get; private set; } = new();

    /// <summary>
    /// 检测 yt-dlp 和 ffmpeg 是否可用
    /// </summary>
    public async Task<EnvironmentStatus> CheckEnvironmentAsync()
    {
        Status = new EnvironmentStatus();

        // 检测 yt-dlp
        var (ytFound, ytVer, ytPath) = await CheckToolAsync("yt-dlp", "--version");
        Status.YtDlpFound = ytFound;
        Status.YtDlpVersion = ytVer;
        Status.YtDlpPath = ytPath;

        // 检测 ffmpeg
        var (ffFound, ffVer, ffPath) = await CheckToolAsync("ffmpeg", "-version");
        Status.FfmpegFound = ffFound;
        Status.FfmpegVersion = ffVer;
        Status.FfmpegPath = ffPath;

        return Status;
    }

    /// <summary>
    /// 检测单个工具是否可用
    /// </summary>
    private async Task<(bool found, string version, string path)> CheckToolAsync(string tool, string versionArg)
    {
        // 1) 先检查本地 tools 目录
        var toolsDir = ConfigService.GetToolsDirectory();
        var localPath = Path.Combine(toolsDir, $"{tool}.exe");
        if (File.Exists(localPath))
        {
            var ver = await GetVersionAsync(localPath, versionArg);
            if (!string.IsNullOrEmpty(ver))
                return (true, ver, localPath);
        }

        // 2) 检查系统 PATH
        try
        {
            var ver = await GetVersionAsync(tool, versionArg);
            if (!string.IsNullOrEmpty(ver))
            {
                // 获取实际路径
                var whichResult = await RunCommandAsync("where", tool);
                var path = whichResult.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? tool;
                return (true, ver, path);
            }
        }
        catch { }

        return (false, "", "");
    }

    private async Task<string> GetVersionAsync(string tool, string versionArg)
    {
        try
        {
            var output = await RunCommandAsync(tool, versionArg);
            // 提取第一行作为版本号
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            // ffmpeg 的版本在 "ffmpeg version X.X.X" 格式
            if (firstLine.StartsWith("ffmpeg version"))
            {
                var parts = firstLine.Split(' ');
                return parts.Length >= 3 ? parts[2] : firstLine;
            }
            return firstLine;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 自动下载 yt-dlp
    /// </summary>
    public async Task<bool> AutoInstallYtDlpAsync(IProgress<string>? log = null)
    {
        try
        {
            log?.Report("正在下载 yt-dlp...");
            var toolsDir = ConfigService.GetToolsDirectory();
            var targetPath = Path.Combine(toolsDir, "yt-dlp.exe");

            const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
            log?.Report($"下载地址: {url}");

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(targetPath);
            await stream.CopyToAsync(fileStream);

            log?.Report("yt-dlp 下载完成！");

            // 验证
            var ver = await GetVersionAsync(targetPath, "--version");
            if (!string.IsNullOrEmpty(ver))
            {
                Status.YtDlpFound = true;
                Status.YtDlpVersion = ver;
                Status.YtDlpPath = targetPath;
                log?.Report($"yt-dlp 版本: {ver}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            log?.Report($"yt-dlp 下载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 自动下载 ffmpeg
    /// </summary>
    public async Task<bool> AutoInstallFfmpegAsync(IProgress<string>? log = null)
    {
        try
        {
            log?.Report("正在下载 ffmpeg...");
            var toolsDir = ConfigService.GetToolsDirectory();

            // 使用 gyan.dev 的 essentials 版本 (较小)
            const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            log?.Report($"下载地址: {url}");

            var zipPath = Path.Combine(toolsDir, "ffmpeg.zip");
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(zipPath);
            await stream.CopyToAsync(fileStream);

            log?.Report("正在解压 ffmpeg...");

            // 解压
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolsDir, true);

            // 找到 ffmpeg.exe 并移动到 tools 根目录
            var ffmpegExe = Directory.GetFiles(toolsDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ffmpegExe != null)
            {
                var targetPath = Path.Combine(toolsDir, "ffmpeg.exe");
                if (ffmpegExe != targetPath)
                    File.Move(ffmpegExe, targetPath, true);

                // 同时移动 ffprobe
                var ffprobeExe = Directory.GetFiles(toolsDir, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (ffprobeExe != null)
                    File.Move(ffprobeExe, Path.Combine(toolsDir, "ffprobe.exe"), true);
            }

            // 清理 zip 和解压目录
            File.Delete(zipPath);
            foreach (var dir in Directory.GetDirectories(toolsDir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            log?.Report("ffmpeg 解压完成！");

            var ver = await GetVersionAsync(Path.Combine(toolsDir, "ffmpeg.exe"), "-version");
            if (!string.IsNullOrEmpty(ver))
            {
                Status.FfmpegFound = true;
                Status.FfmpegVersion = ver;
                Status.FfmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
                log?.Report($"ffmpeg 版本: {ver}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            log?.Report($"ffmpeg 下载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 更新 yt-dlp 到最新版本
    /// </summary>
    public async Task<bool> UpdateYtDlpAsync(IProgress<string>? log = null)
    {
        try
        {
            if (!Status.YtDlpFound)
            {
                log?.Report("yt-dlp 未安装，无法更新");
                return false;
            }

            log?.Report("正在更新 yt-dlp...");
            var output = await RunCommandAsync(Status.YtDlpPath, "-U");
            log?.Report(output.Trim());

            // 重新检测版本
            var ver = await GetVersionAsync(Status.YtDlpPath, "--version");
            if (!string.IsNullOrEmpty(ver))
            {
                Status.YtDlpVersion = ver;
                log?.Report($"yt-dlp 当前版本: {ver}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            log?.Report($"yt-dlp 更新失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 运行外部命令并获取输出
    /// </summary>
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
}
