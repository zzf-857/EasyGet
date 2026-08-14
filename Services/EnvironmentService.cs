using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace EasyGet.Services;

public sealed record ToolUpdateCheckResult(
    string ToolName,
    bool IsInstalled,
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    string? ErrorMessage);

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
    internal const string YtDlpLatestReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    internal const string FfmpegReleaseVersionUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
    internal const string ToolUpdaterUserAgent = "EasyGet-ToolUpdater";

    private static readonly HttpClient HttpClient = CreateDefaultToolHttpClient();
    private const int ToolDownloadMaxAttempts = 3;
    private const int ToolDownloadBufferSize = 81920;
    private readonly ConfigService? _configService;
    private readonly Func<string, string, Task<(bool found, string version, string path)>> _checkToolAsync;
    private readonly Func<HttpClient>? _httpClientFactory;

    public EnvironmentStatus Status { get; private set; } = new();

    public EnvironmentService()
        : this(null, null, null)
    {
    }

    public EnvironmentService(ConfigService configService)
        : this(configService, null, null)
    {
    }

    internal EnvironmentService(Func<string, string, Task<(bool found, string version, string path)>>? checkToolAsync)
        : this(null, checkToolAsync, null)
    {
    }

    internal EnvironmentService(
        ConfigService? configService,
        Func<string, string, Task<(bool found, string version, string path)>>? checkToolAsync,
        Func<HttpClient>? httpClientFactory)
    {
        _configService = configService;
        _checkToolAsync = checkToolAsync ?? CheckToolAsync;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<EnvironmentStatus> CheckEnvironmentAsync()
    {
        Status = new EnvironmentStatus();

        var ytDlpTask = _checkToolAsync("yt-dlp", "--version");
        var ffmpegTask = _checkToolAsync("ffmpeg", "-version");
        await Task.WhenAll(ytDlpTask, ffmpegTask);

        var (ytFound, ytVer, ytPath) = await ytDlpTask;
        var (ffFound, ffVer, ffPath) = await ffmpegTask;

        var status = new EnvironmentStatus
        {
            YtDlpFound = ytFound,
            YtDlpVersion = ytVer,
            YtDlpPath = ytPath,
            FfmpegFound = ffFound,
            FfmpegVersion = ffVer,
            FfmpegPath = ffPath
        };

        Status = status;
        return status;
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

    public Task<bool> UpdateYtDlpAsync(IProgress<string>? log = null, CancellationToken ct = default)
        => UpdateToolAsync("yt-dlp", log, ct);

    public Task<bool> UpdateFfmpegAsync(IProgress<string>? log = null, CancellationToken ct = default)
        => UpdateToolAsync("ffmpeg", log, ct);

    public async Task<IReadOnlyList<ToolUpdateCheckResult>> CheckToolUpdatesAsync(
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        if (!Status.YtDlpFound && !Status.FfmpegFound)
            await CheckEnvironmentAsync();

        log?.Report("正在获取 yt-dlp 与 ffmpeg 的最新版本...");
        using var http = RentToolHttpClient();
        var ytDlpTask = FetchLatestVersionAsync("yt-dlp", http.Client, ct);
        var ffmpegTask = FetchLatestVersionAsync("ffmpeg", http.Client, ct);
        await Task.WhenAll(ytDlpTask, ffmpegTask);

        var results = new[]
        {
            CreateToolUpdateCheckResult("yt-dlp", Status.YtDlpFound, Status.YtDlpVersion, await ytDlpTask),
            CreateToolUpdateCheckResult("ffmpeg", Status.FfmpegFound, Status.FfmpegVersion, await ffmpegTask)
        };

        var available = results.Count(result => result.IsUpdateAvailable);
        log?.Report(available > 0
            ? $"发现 {available} 个组件可更新。"
            : results.Any(result => !string.IsNullOrWhiteSpace(result.ErrorMessage))
                ? "已检测本地组件，但未能获取全部最新版本。"
                : "组件已是最新。");
        return results;
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

        string? firstCandidate = null;
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        foreach (var path in Directory.EnumerateFiles(rootDirectory, executableName, SearchOption.AllDirectories))
        {
            firstCandidate ??= path;
            if (path.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return firstCandidate;
    }

    internal static string? FindExecutableOnPath(string executableName, string? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;

        var pathValue = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var fileNames = Path.HasExtension(executableName)
            ? [executableName]
            : new[] { executableName, $"{executableName}.exe" };

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public string? GetAria2cPath()
    {
        var bundledPath = Path.Combine(ConfigService.GetToolsDirectory(), "aria2c.exe");
        return File.Exists(bundledPath)
            ? bundledPath
            : FindExecutableOnPath("aria2c");
    }

    private async Task InstallYtDlpAsync(IProgress<string>? log, CancellationToken ct)
    {
        var targetPath = Path.Combine(ConfigService.GetToolsDirectory(), "yt-dlp.exe");
        var tempPath = Path.Combine(Path.GetTempPath(), $"easyget-ytdlp-{Guid.NewGuid():N}.exe");

        try
        {
            using var http = RentToolHttpClient();
            await DownloadFileAsync(GetToolDownloadUri("yt-dlp"), tempPath, "yt-dlp", log, ct, http.Client);
            await VerifyDownloadedExecutableAsync(tempPath, "yt-dlp", "--version", ct);
            ReplaceExecutable(tempPath, targetPath);
            log?.Report("yt-dlp 安装完成。");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private async Task InstallFfmpegAsync(IProgress<string>? log, CancellationToken ct)
    {
        var toolsDir = ConfigService.GetToolsDirectory();
        var zipPath = Path.Combine(Path.GetTempPath(), $"easyget-ffmpeg-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"easyget-ffmpeg-{Guid.NewGuid():N}");

        try
        {
            using var http = RentToolHttpClient();
            await DownloadFileAsync(GetToolDownloadUri("ffmpeg"), zipPath, "ffmpeg", log, ct, http.Client);
            log?.Report("正在解压 ffmpeg...");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var ffmpegPath = FindExecutableInDirectoryTree(extractDir, "ffmpeg.exe");
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                throw new FileNotFoundException("未能在 ffmpeg 压缩包中找到 ffmpeg.exe。");

            await VerifyDownloadedExecutableAsync(ffmpegPath, "ffmpeg", "-version", ct);
            ReplaceExecutable(ffmpegPath, Path.Combine(toolsDir, "ffmpeg.exe"));

            var ffprobePath = FindExecutableInDirectoryTree(extractDir, "ffprobe.exe");
            if (!string.IsNullOrWhiteSpace(ffprobePath))
                ReplaceExecutable(ffprobePath, Path.Combine(toolsDir, "ffprobe.exe"));

            log?.Report("ffmpeg 安装完成。");
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDir);
        }
    }

    internal static async Task DownloadFileAsync(
        Uri uri,
        string targetPath,
        string toolName,
        IProgress<string>? log,
        CancellationToken ct,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null)
    {
        retryDelayAsync ??= Task.Delay;

        for (var attempt = 1; attempt <= ToolDownloadMaxAttempts; attempt++)
        {
            try
            {
                await DownloadFileOnceAsync(uri, targetPath, toolName, log, ct, httpClient ?? HttpClient);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                TryDeleteFile(targetPath);
                if (!ShouldRetryToolDownload(ex, attempt, ct))
                    throw;

                var delay = GetToolDownloadRetryDelay(attempt);
                log?.Report($"{toolName} 下载失败，准备重试 ({attempt}/{ToolDownloadMaxAttempts}): {ex.Message}");
                await retryDelayAsync(delay, ct);
            }
        }
    }

    private static async Task DownloadFileOnceAsync(
        Uri uri,
        string targetPath,
        string toolName,
        IProgress<string>? log,
        CancellationToken ct,
        HttpClient httpClient)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = ArrayPool<byte>.Shared.Rent(ToolDownloadBufferSize);

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, ToolDownloadBufferSize, useAsync: true);

            long totalRead = 0;
            var lastPercent = -1;

            while (true)
            {
                int read;
                try
                {
                    read = await HttpIdleRead.ReadAsync(
                        source,
                        buffer.AsMemory(0, ToolDownloadBufferSize),
                        ct);
                }
                catch (TimeoutException ex)
                {
                    throw new IOException(ex.Message, ex);
                }
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

            if (totalBytes is > 0 && totalRead != totalBytes.Value)
                throw new IOException($"{toolName} 下载不完整：已下载 {totalRead} / {totalBytes.Value} 字节。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ShouldRetryToolDownload(Exception ex, int attempt, CancellationToken ct)
    {
        if (attempt >= ToolDownloadMaxAttempts || ct.IsCancellationRequested)
            return false;

        return ex switch
        {
            HttpRequestException httpEx => IsTransientStatusCode(httpEx.StatusCode),
            IOException => true,
            TaskCanceledException => true,
            _ => false
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode? statusCode)
    {
        if (statusCode is null)
            return true;

        var code = (int)statusCode.Value;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
               || code >= 500;
    }

    private static TimeSpan GetToolDownloadRetryDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(attempt, 3));

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

    internal static async Task<string> RunCommandAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动命令: {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw new TimeoutException($"命令执行超时: {fileName} {arguments}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return CombineCommandOutput(stdout, stderr);
    }

    private static async Task DrainProcessOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(1000));
        }
        catch
        {
            // 进程超时后的输出清理是 best effort。
        }
    }

    private static string CombineCommandOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;

        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;

        return $"{stdout.TrimEnd()}{Environment.NewLine}{stderr}";
    }

    private async Task<bool> UpdateToolAsync(string tool, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.GetToolsDirectory());
            log?.Report($"正在获取并应用 {tool}...");
            if (tool == "yt-dlp")
                await InstallYtDlpAsync(log, ct);
            else if (tool == "ffmpeg")
                await InstallFfmpegAsync(log, ct);
            else
                throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool");

            await CheckEnvironmentAsync();
            var found = tool == "yt-dlp" ? Status.YtDlpFound : Status.FfmpegFound;
            var version = tool == "yt-dlp" ? Status.YtDlpVersion : Status.FfmpegVersion;
            log?.Report(found ? $"{tool} 已更新到 {version}。" : $"{tool} 更新失败。");
            return found;
        }
        catch (Exception ex)
        {
            log?.Report($"{tool} 更新失败: {ex.Message}");
            return false;
        }
    }

    private async Task<(string? version, string? error)> FetchLatestVersionAsync(
        string tool,
        HttpClient httpClient,
        CancellationToken ct)
    {
        try
        {
            if (tool == "yt-dlp")
            {
                var json = await ReadSmallTextAsync(httpClient, new Uri(YtDlpLatestReleaseApiUrl), ct);
                var version = ParseYtDlpLatestVersion(json);
                return string.IsNullOrWhiteSpace(version)
                    ? (null, "未能解析 yt-dlp 最新版本。")
                    : (version, null);
            }

            if (tool == "ffmpeg")
            {
                var text = await ReadSmallTextAsync(httpClient, new Uri(FfmpegReleaseVersionUrl), ct);
                var version = ParseFfmpegLatestVersion(text);
                return string.IsNullOrWhiteSpace(version)
                    ? (null, "未能解析 ffmpeg 最新版本。")
                    : (version, null);
            }

            return (null, "未知组件");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    internal static ToolUpdateCheckResult CreateToolUpdateCheckResult(
        string toolName,
        bool isInstalled,
        string currentVersion,
        (string? version, string? error) latest)
    {
        var normalizedCurrent = NormalizeToolVersion(currentVersion);
        var normalizedLatest = NormalizeToolVersion(latest.version);
        return new ToolUpdateCheckResult(
            toolName,
            isInstalled,
            normalizedCurrent,
            string.IsNullOrWhiteSpace(normalizedLatest) ? null : normalizedLatest,
            IsToolUpdateAvailable(isInstalled ? normalizedCurrent : "", normalizedLatest),
            latest.error);
    }

    internal static string? ParseYtDlpLatestVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("tag_name", out var tag)
            ? NormalizeToolVersion(tag.GetString())
            : null;
    }

    internal static string? ParseFfmpegLatestVersion(string text)
        => NormalizeToolVersion(text);

    internal static string NormalizeToolVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var token = value
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        token = token.Trim().TrimStart('v', 'V', 'n', 'N');
        var separator = token.IndexOfAny(['-', '+', '_']);
        if (separator > 0)
            token = token[..separator];
        return token;
    }

    internal static bool IsToolUpdateAvailable(string? currentVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;
        if (string.IsNullOrWhiteSpace(currentVersion))
            return true;
        return CompareToolVersions(latestVersion, currentVersion) > 0;
    }

    internal static int CompareToolVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        if (leftParts.Count == 0 && rightParts.Count == 0)
            return 0;
        if (leftParts.Count == 0)
            return -1;
        if (rightParts.Count == 0)
            return 1;

        var length = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < length; i++)
        {
            var leftPart = i < leftParts.Count ? leftParts[i] : 0;
            var rightPart = i < rightParts.Count ? rightParts[i] : 0;
            var compared = leftPart.CompareTo(rightPart);
            if (compared != 0)
                return compared;
        }

        return 0;
    }

    private static List<int> ParseVersionParts(string? value)
    {
        var normalized = NormalizeToolVersion(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var parts = new List<int>();
        foreach (var part in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var number))
                return [];
            parts.Add(number);
        }

        return parts;
    }

    internal static void ReplaceExecutable(string sourcePath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到已下载的组件文件。", sourcePath);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ConfigService.GetToolsDirectory());
        var backupPath = targetPath + ".old";
        TryDeleteFile(backupPath);

        if (File.Exists(targetPath))
        {
            try
            {
                File.Move(targetPath, backupPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("无法替换正在使用的文件，请先暂停或完成下载任务后重试。", ex);
            }
        }

        try
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            TryDeleteFile(backupPath);
        }
        catch
        {
            if (File.Exists(backupPath) && !File.Exists(targetPath))
                File.Move(backupPath, targetPath);
            throw;
        }
    }

    private async Task VerifyDownloadedExecutableAsync(
        string path,
        string toolName,
        string versionArg,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var version = await GetVersionAsync(path, versionArg);
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"下载的 {toolName} 无法运行，已取消替换。");
    }

    private static async Task<string> ReadSmallTextAsync(HttpClient httpClient, Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(ct)).Trim();
    }

    internal HttpClient CreateToolHttpClient()
        => RentToolHttpClient().Client;

    private ToolHttpClientLease RentToolHttpClient()
    {
        if (_httpClientFactory is not null)
            return new ToolHttpClientLease(_httpClientFactory(), ownsClient: false);

        var config = _configService?.Config;
        if (config is { UseProxy: true } && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseProxy = true,
                Proxy = new WebProxy(config.ProxyAddress.Trim())
            };
            return new ToolHttpClientLease(
                ConfigureToolHttpClient(new HttpClient(handler, disposeHandler: true)),
                ownsClient: true);
        }

        return new ToolHttpClientLease(ConfigureToolHttpClient(new HttpClient()), ownsClient: true);
    }

    private sealed class ToolHttpClientLease(HttpClient client, bool ownsClient) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            if (ownsClient)
                Client.Dispose();
        }
    }

    private static HttpClient CreateDefaultToolHttpClient()
        => ConfigureToolHttpClient(new HttpClient());

    private static HttpClient ConfigureToolHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ToolUpdaterUserAgent);
        return client;
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略进程清理失败。
        }
    }
}
