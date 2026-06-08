using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyGet.Models;

namespace EasyGet.Services;

public class VideoInfo
{
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public double Duration { get; set; }
    public string Thumbnail { get; set; } = "";
    public long FileSize { get; set; }
    public string Url { get; set; } = "";
}

public class DownloadProgress
{
    public double Percent { get; set; }
    public double Speed { get; set; }
    public double Eta { get; set; }
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public string RawLine { get; set; } = "";
}

internal sealed record ProcessOutput(string StandardOutput, string StandardError, int ExitCode);

public partial class YtDlpService
{
    private static readonly TimeSpan DefaultDownloadNoOutputTimeout = TimeSpan.FromMinutes(10);

    private enum CookieStrategy
    {
        Default,
        BrowserChrome,
        BrowserEdge
    }

    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;

    public YtDlpService(ConfigService configService, EnvironmentService envService)
    {
        _configService = configService;
        _envService = envService;
    }

    private string GetYtDlpPath()
    {
        return !string.IsNullOrWhiteSpace(_envService.Status.YtDlpPath)
            ? _envService.Status.YtDlpPath
            : "yt-dlp";
    }

    private string? GetFfmpegDirectory()
    {
        if (string.IsNullOrWhiteSpace(_envService.Status.FfmpegPath))
            return null;

        return Path.GetDirectoryName(_envService.Status.FfmpegPath);
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var args = new List<string>
            {
                "--no-playlist",
                "--dump-json",
                "--no-download",
                "--no-warnings",
                url
            };

            AddProxyArgs(args);
            AddCookieArgs(args, url, CookieStrategy.Default);

            var output = await RunAsync(args, ct);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var firstJson = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.TrimStart().StartsWith("{"));

            if (string.IsNullOrWhiteSpace(firstJson))
                return null;

            using var doc = JsonDocument.Parse(firstJson);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            title = title.Replace("\r", "").Replace("\n", " ").Trim();

            var platform = root.TryGetProperty("extractor_key", out var pk)
                ? pk.GetString() ?? ""
                : root.TryGetProperty("extractor", out var p) ? p.GetString() ?? "" : "";

            var thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(thumbnail)
                && root.TryGetProperty("thumbnails", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var thumbUrl))
                        thumbnail = thumbUrl.GetString() ?? thumbnail;
                }
            }

            long fileSize = 0;
            if (root.TryGetProperty("filesize_approx", out var fsApprox) && fsApprox.ValueKind == JsonValueKind.Number)
                fileSize = fsApprox.GetInt64();
            else if (root.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number)
                fileSize = fs.GetInt64();

            return new VideoInfo
            {
                Title = title,
                Platform = platform,
                Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : 0,
                Thumbnail = thumbnail,
                FileSize = fileSize,
                Url = url
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetVideoInfo failed: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string>> GetPlaylistUrlsAsync(string url, CancellationToken ct = default)
    {
        var urls = new List<string>();

        try
        {
            var args = new List<string>
            {
                "--flat-playlist",
                "--dump-json",
                "--no-warnings",
                url
            };

            AddProxyArgs(args);
            AddCookieArgs(args, url, CookieStrategy.Default);

            var output = await RunAsync(args, ct);
            if (string.IsNullOrWhiteSpace(output))
                return urls;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var videoUrl = root.TryGetProperty("url", out var u)
                        ? u.GetString() ?? ""
                        : root.TryGetProperty("webpage_url", out var wu) ? wu.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(videoUrl))
                        urls.Add(videoUrl);
                }
                catch
                {
                    // ignore non-json lines
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetPlaylistUrls failed: {ex.Message}");
        }

        return urls;
    }

    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        task.Status = DownloadStatus.Downloading;

        var strategies = new List<CookieStrategy> { CookieStrategy.Default };
        if (IsDouyinUrl(task.Url) || IsYoutubeUrl(task.Url))
        {
            if (HasBrowserCookies("chrome")) strategies.Add(CookieStrategy.BrowserChrome);
            if (HasBrowserCookies("edge")) strategies.Add(CookieStrategy.BrowserEdge);
        }

        List<string> lastStderr = [];
        var allStderr = new List<string>();
        int lastExitCode = -1;

        for (var i = 0; i < strategies.Count; i++)
        {
            var strategy = strategies[i];
            var aria2cPath = _configService.Config.UseAria2c ? _envService.GetAria2cPath() : null;
            if (_configService.Config.UseAria2c && string.IsNullOrWhiteSpace(aria2cPath))
                logCallback?.Invoke("[yt-dlp] aria2c 已启用但未找到 aria2c.exe，已回退到 yt-dlp 内置下载器。");

            var args = BuildDownloadArgs(task, strategy, aria2cPath);
            var strategyTag = strategy switch
            {
                CookieStrategy.BrowserChrome => " (cookies-from-browser: chrome)",
                CookieStrategy.BrowserEdge => " (cookies-from-browser: edge)",
                _ => string.Empty
            };

            logCallback?.Invoke($"[yt-dlp] start{strategyTag}: {task.Url}");
            logCallback?.Invoke($"[yt-dlp] args: {string.Join(" ", args)}");

            var stderrLines = new List<string>();
            string? capturedOutputPath = null;
            var downloadStartTime = DateTime.Now;
            ProcessOutput processOutput;

            try
            {
                processOutput = await RunDownloadProcessAsync(
                    GetYtDlpPath(),
                    args,
                    DefaultDownloadNoOutputTimeout,
                    line =>
                    {
                        logCallback?.Invoke(line);

                        var path = ParseOutputPath(line);
                        if (!string.IsNullOrWhiteSpace(path))
                            capturedOutputPath = path;

                        var parsed = ParseProgressLine(line);
                        if (parsed is not null)
                            progress?.Report(parsed);
                    },
                    line =>
                    {
                        stderrLines.Add(line);
                        logCallback?.Invoke($"[stderr] {line}");
                    },
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                return;
            }
            catch (TimeoutException ex)
            {
                var message = $"ERROR: {ex.Message}";
                stderrLines.Add(message);
                lastStderr = stderrLines;
                allStderr.AddRange(stderrLines);
                lastExitCode = -1;
                logCallback?.Invoke($"[yt-dlp] {ex.Message}");
                break;
            }

            var outputFile = ResolveOutputFile(capturedOutputPath, task, downloadStartTime);
            if (processOutput.ExitCode == 0)
            {
                task.Status = DownloadStatus.Completed;
                task.Progress = 100;

                if (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile))
                {
                    task.OutputFilePath = outputFile;
                    task.FileSize = new FileInfo(outputFile).Length;
                }

                logCallback?.Invoke($"[yt-dlp] completed: {task.Title}");
                return;
            }

            lastStderr = stderrLines;
            allStderr.AddRange(stderrLines);
            lastExitCode = processOutput.ExitCode;

            var canRetryWithNextCookieStrategy =
                i < strategies.Count - 1
                && ShouldRetryWithNextCookieStrategy(task.Url, stderrLines);

            if (canRetryWithNextCookieStrategy)
            {
                logCallback?.Invoke("[yt-dlp] retrying with next cookie strategy...");
                continue;
            }

            break;
        }

        if (IsDouyinUrl(task.Url) && task.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            logCallback?.Invoke("[yt-dlp] Douyin extractor failed; trying browser fallback...");
            var fallback = new DouyinBrowserDownloadService();
            if (await fallback.TryDownloadAsync(task, progress, logCallback, ct))
                return;
        }

        task.Status = DownloadStatus.Failed;
        task.ErrorMessage = BuildDownloadFailureMessage(task.Url, allStderr.Count > 0 ? allStderr : lastStderr, lastExitCode);

        logCallback?.Invoke($"[yt-dlp] failed (exit code: {lastExitCode})");
    }

    private List<string> BuildDownloadArgs(DownloadTask task, CookieStrategy cookieStrategy = CookieStrategy.Default, string? aria2cPath = null)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "-f",
            BuildFormatString(task.Format, task.Quality)
        };

        if (task.Format is "mp3" or "m4a")
        {
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(task.Format);
            args.Add("--audio-quality");
            args.Add("0");
        }
        else
        {
            args.Add("--merge-output-format");
            args.Add(task.Format);
        }

        args.Add("-o");
        args.Add(DownloadFileNameBuilder.BuildOutputTemplate(task.OutputDirectory, task.Title));

        args.Add("--windows-filenames");
        args.Add("--no-mtime");
        args.Add("--encoding");
        args.Add("utf-8");
        args.Add("--newline");
        args.Add("--progress-template");
        args.Add("download:%(progress._percent_str)s %(progress._speed_str)s ETA %(progress._eta_str)s");

        var fragments = Math.Clamp(
            _configService.Config.ConcurrentFragments,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);
        if (fragments > 1)
        {
            args.Add("--concurrent-fragments");
            args.Add(fragments.ToString());
        }

        var ffmpegDir = GetFfmpegDirectory();
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }

        switch (task.Subtitle)
        {
            case "auto":
                args.Add("--write-auto-subs");
                args.Add("--sub-lang");
                args.Add("zh-Hans,zh,en");
                break;
            case "all":
                args.Add("--write-subs");
                args.Add("--all-subs");
                break;
        }

        AddAria2cArgs(args, _configService.Config.UseAria2c, aria2cPath);

        AddProxyArgs(args);
        AddCookieArgs(args, task.Url, cookieStrategy);

        args.Add(task.Url);
        return args;
    }

    internal static void AddAria2cArgs(List<string> args, bool useAria2c, string? aria2cPath)
    {
        if (!useAria2c || string.IsNullOrWhiteSpace(aria2cPath))
            return;

        args.Add("--external-downloader");
        args.Add(aria2cPath);
        args.Add("--external-downloader-args");
        args.Add("aria2c:--min-split-size=1M --max-connection-per-server=16 --split=16");
    }

    private static string BuildFormatString(string format, string quality)
    {
        if (format is "mp3" or "m4a")
            return "bestaudio/best";

        var qualityFilter = quality switch
        {
            "2160" => "[height<=2160]",
            "1080" => "[height<=1080]",
            "720" => "[height<=720]",
            "480" => "[height<=480]",
            _ => ""
        };

        return $"bv*{qualityFilter}+ba/b{qualityFilter}/b";
    }

    private static DownloadProgress? ParseProgressLine(string line)
    {
        var match = UniversalProgressRegex().Match(line);
        if (match.Success)
        {
            var dp = new DownloadProgress { RawLine = line };
            if (double.TryParse(match.Groups[1].Value, out var percent))
                dp.Percent = percent;

            dp.Speed = ParseSpeed(match.Groups[2].Value);
            dp.Eta = ParseEta(match.Groups[3].Value);
            return dp;
        }

        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadProgress { Percent = 99, RawLine = line };
        }

        return null;
    }

    private static double ParseSpeed(string speedStr)
    {
        var m = SpeedRegex().Match(speedStr);
        if (!m.Success)
            return 0;

        var value = double.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value;

        return unit switch
        {
            "KiB" => value * 1024,
            "MiB" => value * 1024 * 1024,
            "GiB" => value * 1024 * 1024 * 1024,
            _ => value
        };
    }

    private static double ParseEta(string etaStr)
    {
        if (etaStr == "Unknown" || string.IsNullOrWhiteSpace(etaStr))
            return 0;

        var parts = etaStr.Split(':');
        return parts.Length switch
        {
            3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]),
            2 => int.Parse(parts[0]) * 60 + int.Parse(parts[1]),
            1 => int.Parse(parts[0]),
            _ => 0
        };
    }

    private void AddProxyArgs(List<string> args)
    {
        var config = _configService.Config;
        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            args.Add("--proxy");
            args.Add(config.ProxyAddress);
        }
    }

    private static readonly string CookieFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyGet",
        "cookies.txt");

    private void AddCookieArgs(List<string> args, string url, CookieStrategy strategy)
    {
        if (strategy is CookieStrategy.BrowserChrome or CookieStrategy.BrowserEdge)
        {
            var browser = strategy == CookieStrategy.BrowserChrome ? "chrome" : "edge";
            if (HasBrowserCookies(browser))
            {
                args.Add("--cookies-from-browser");
                args.Add(browser);
            }
            return;
        }

        var config = _configService.Config;
        if (string.IsNullOrWhiteSpace(config.CookieContent))
            return;

        try
        {
            SaveCookieFile(config.CookieContent);
            if (File.Exists(CookieFilePath))
            {
                args.Add("--cookies");
                args.Add(CookieFilePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] AddCookieArgs failed: {ex.Message}");
        }
    }

    private static bool IsDouyinUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYoutubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetryWithNextCookieStrategy(string url, List<string> stderrLines)
    {
        if (IsDouyinUrl(url))
        {
            return stderrLines.Any(line =>
                line.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase))
                || stderrLines.Any(IsBrowserCookieAccessError);
        }

        if (IsYoutubeUrl(url))
        {
            return stderrLines.Any(IsYoutubeBotOrForbiddenError)
                   || stderrLines.Any(IsBrowserCookieAccessError);
        }

        return false;
    }

    private static bool IsYoutubeBotOrForbiddenError(string line)
    {
        return line.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
               || line.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserCookieAccessError(string line)
    {
        return line.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Could not copy cookie database", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildDownloadFailureMessage(string url, IEnumerable<string> stderrLines, int exitCode)
    {
        var lines = stderrLines.ToList();

        if (IsDouyinUrl(url) && (lines.Any(line => line.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase))
            || lines.Any(IsBrowserCookieAccessError)))
        {
            return "抖音需要最新 Cookie，但自动读取浏览器 Cookie 失败或 Cookie 已失效。请关闭 Chrome/Edge 后重试，或在设置中粘贴最新抖音 Cookie。";
        }

        if (IsYoutubeUrl(url) && lines.Any(IsYoutubeBotOrForbiddenError))
        {
            return "YouTube 下载被风控拦截（403/需要登录验证）。请在设置中粘贴最新 YouTube Cookie，或关闭浏览器后重试。";
        }

        return lines.LastOrDefault(line =>
                   line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
               ?? $"yt-dlp exit code: {exitCode}";
    }

    private static bool HasBrowserCookies(string browser)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = browser.ToLowerInvariant() switch
        {
            "chrome" => new[] { Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default") },
            "edge" => new[] { Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default") },
            _ => Array.Empty<string>()
        };

        foreach (var root in roots)
        {
            if (File.Exists(Path.Combine(root, "Network", "Cookies"))
                || File.Exists(Path.Combine(root, "Cookies")))
            {
                return true;
            }
        }

        return false;
    }

    private static void SaveCookieFile(string cookieContent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CookieFilePath)!);
        File.WriteAllLines(CookieFilePath, BuildCookieFileLines(cookieContent));
    }

    internal static List<string> BuildCookieFileLines(string cookieContent)
    {
        var lines = new List<string>
        {
            "# Netscape HTTP Cookie File",
            "# Generated by EasyGet",
            ""
        };

        var trimmed = cookieContent.Trim();

        if (LooksLikeNetscapeCookieFile(trimmed))
        {
            foreach (var line in trimmed.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.Split('\t').Length >= 7)
                    lines.Add(line);
            }

            return lines;
        }

        if (trimmed.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var domain = item.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                    var secure = item.TryGetProperty("secure", out var s) && s.GetBoolean();
                    var hostOnly = item.TryGetProperty("hostOnly", out var ho) && ho.GetBoolean();

                    long expiry = 0;
                    if (item.TryGetProperty("expirationDate", out var exp)
                        && exp.ValueKind == JsonValueKind.Number)
                    {
                        expiry = (long)exp.GetDouble();
                    }

                    if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
                        continue;

                    var includeSubdomains = hostOnly ? "FALSE" : "TRUE";
                    var secureText = (secure || name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase)) ? "TRUE" : "FALSE";

                    lines.Add($"{domain}\t{includeSubdomains}\t{path}\t{secureText}\t{expiry}\t{name}\t{value}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YtDlpService] JSON cookie parse failed: {ex.Message}");
                ParsePlainTextCookies(trimmed, lines);
            }
        }
        else
        {
            ParsePlainTextCookies(trimmed, lines);
        }

        return lines;
    }

    private static void ParsePlainTextCookies(string cookieContent, List<string> lines)
    {
        string[] domains = [".youtube.com", ".x.com", ".twitter.com", ".instagram.com", ".bilibili.com", ".douyin.com", ".tiktok.com"];

        var headerValue = ExtractCookieHeaderValue(cookieContent);
        var pairs = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
                continue;

            var name = pair[..index].Trim();
            var value = pair[(index + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            foreach (var domain in domains)
                lines.Add($"{domain}\tTRUE\t/\tTRUE\t0\t{name}\t{value}");
        }
    }

    private static bool LooksLikeNetscapeCookieFile(string cookieContent)
    {
        if (cookieContent.Contains("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
            return true;

        return cookieContent
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => !line.StartsWith("#") && line.Split('\t').Length >= 7);
    }

    private static string ExtractCookieHeaderValue(string cookieContent)
    {
        var lines = cookieContent.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cookieLine = lines.FirstOrDefault(line => line.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase));
        var value = cookieLine ?? cookieContent.Trim();

        if (value.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            value = value["cookie:".Length..].Trim();

        return value;
    }

    private static string? ParseOutputPath(string line)
    {
        var mergerMatch = MergerOutputRegex().Match(line);
        if (mergerMatch.Success)
            return mergerMatch.Groups[1].Value;

        if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Destination:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = line.IndexOf("Destination:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var path = line[(idx + "Destination:".Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(path) && !TempFilePattern().IsMatch(path))
                    return path;
            }
        }

        if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
        {
            var start = line.IndexOf(']') + 1;
            var end = line.IndexOf(" has already been downloaded", StringComparison.OrdinalIgnoreCase);
            if (start > 0 && end > start)
                return line[start..end].Trim();
        }

        return null;
    }

    private static string? ResolveOutputFile(string? capturedPath, DownloadTask task, DateTime downloadStartTime)
    {
        if (!string.IsNullOrWhiteSpace(capturedPath) && File.Exists(capturedPath))
            return capturedPath;

        if (string.IsNullOrWhiteSpace(task.OutputDirectory) || !Directory.Exists(task.OutputDirectory))
            return null;

        var extensions = task.Format switch
        {
            "mp3" => new[] { ".mp3" },
            "m4a" => new[] { ".m4a" },
            "webm" => new[] { ".webm" },
            "mkv" => new[] { ".mkv", ".mp4", ".webm" },
            _ => new[] { ".mp4", ".mkv", ".webm" }
        };

        var file = Directory.GetFiles(task.OutputDirectory)
            .Select(path => new FileInfo(path))
            .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !f.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                && extensions.Any(ext => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                && f.LastWriteTime >= downloadStartTime.AddMinutes(-1))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();

        return file?.FullName;
    }

    private async Task<string> RunAsync(List<string> args, CancellationToken ct = default)
    {
        var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
        return result.StandardOutput;
    }

    internal static async Task<ProcessOutput> RunDownloadProcessAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan? noOutputTimeout = null,
        Action<string>? stdoutLineReceived = null,
        Action<string>? stderrLineReceived = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动命令: {fileName}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lastOutputTicks = DateTime.UtcNow.Ticks;
        void TouchOutput() => Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);

        var stdoutTask = ReadProcessLinesAsync(process.StandardOutput, stdout, stdoutLineReceived, TouchOutput);
        var stderrTask = ReadProcessLinesAsync(process.StandardError, stderr, stderrLineReceived, TouchOutput);

        var idleTimeoutSource = new TaskCompletionSource<TimeoutException>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = MonitorProcessOutputIdleAsync(
            process,
            () => new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc),
            noOutputTimeout ?? DefaultDownloadNoOutputTimeout,
            idleTimeoutSource,
            monitorCts.Token);

        var readersDrained = false;
        try
        {
            var exitTask = process.WaitForExitAsync(ct);
            var completedTask = await Task.WhenAny(exitTask, monitorTask, idleTimeoutSource.Task);
            if (completedTask == idleTimeoutSource.Task)
                throw await idleTimeoutSource.Task;

            if (completedTask == monitorTask)
                await monitorTask;

            await exitTask;
            if (idleTimeoutSource.Task.IsCompletedSuccessfully)
                throw idleTimeoutSource.Task.Result;

            if (monitorTask.IsFaulted)
                await monitorTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            readersDrained = true;
            throw;
        }
        catch (TimeoutException)
        {
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            readersDrained = true;
            throw;
        }
        finally
        {
            monitorCts.Cancel();
        }

        if (!readersDrained)
            await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessOutput(stdout.ToString(), stderr.ToString(), process.ExitCode);
    }

    internal static async Task<ProcessOutput> RunProcessAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动命令: {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw new TimeoutException($"yt-dlp 命令执行超时: {fileName}");
        }

        return new ProcessOutput(
            await stdoutTask,
            await stderrTask,
            process.ExitCode);
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

    private static async Task DrainProcessOutputAsync(Task stdoutTask, Task stderrTask)
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

    private static async Task ReadProcessLinesAsync(
        StreamReader reader,
        StringBuilder output,
        Action<string>? lineReceived,
        Action touchOutput)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                touchOutput();
                output.AppendLine(line);
                lineReceived?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Debug.WriteLine($"[YtDlpService] process output read error: {ex.Message}");
        }
    }

    private static async Task MonitorProcessOutputIdleAsync(
        Process process,
        Func<DateTime> getLastOutputTimeUtc,
        TimeSpan noOutputTimeout,
        TaskCompletionSource<TimeoutException> idleTimeoutSource,
        CancellationToken ct)
    {
        if (noOutputTimeout <= TimeSpan.Zero)
            return;

        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(noOutputTimeout.TotalMilliseconds / 4, 100, 1000));

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (process.HasExited)
                return;

            if (DateTime.UtcNow - getLastOutputTimeUtc() < noOutputTimeout)
                continue;

            var ex = new TimeoutException($"yt-dlp 下载超过 {FormatDuration(noOutputTimeout)} 没有输出，已终止。");
            idleTimeoutSource.TrySetResult(ex);
            TryKill(process);
            throw ex;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} 分钟"
            : $"{duration.TotalSeconds:0.#} 秒";
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

    [GeneratedRegex(@"([\d.]+)%.*?([\d.]+[KMGT]?i?B/s).*?ETA\s+([\w:]+)")]
    private static partial Regex UniversalProgressRegex();

    [GeneratedRegex(@"([\d.]+)([\w/]+)")]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"\.f\d+\.[a-zA-Z0-9]+$")]
    private static partial Regex TempFilePattern();

    [GeneratedRegex(@"\[Merger\].*?""(.+?)""")]
    private static partial Regex MergerOutputRegex();
}
