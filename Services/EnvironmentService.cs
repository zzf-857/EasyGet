using System.Diagnostics;
using System.IO;

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

    public async Task<bool> UpdateYtDlpAsync(IProgress<string>? log = null)
    {
        try
        {
            if (!Status.YtDlpFound)
            {
                log?.Report("yt-dlp not found. Install it first.");
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
}
