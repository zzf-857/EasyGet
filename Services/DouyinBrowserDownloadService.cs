using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

internal sealed record DouyinBrowserCaptureResult(string VideoUrl, string Title, string ThumbnailUrl);

internal class DouyinBrowserDownloadService
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    private static readonly HttpClient HttpClient = new();

    public async Task<bool> TryDownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        if (!task.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            logCallback?.Invoke("[douyin-browser] fallback skipped: only mp4 is supported.");
            return false;
        }

        var browserPath = FindChromiumBrowserPath();
        if (string.IsNullOrWhiteSpace(browserPath))
        {
            logCallback?.Invoke("[douyin-browser] fallback skipped: Chrome/Edge was not found.");
            return false;
        }

        logCallback?.Invoke("[douyin-browser] opening Douyin page with headless browser...");
        progress?.Report(new DownloadProgress { Percent = 3, RawLine = "[douyin-browser] opening page" });
        var capture = await CaptureVideoAsync(browserPath, task.Url, logCallback, ct);
        if (capture is null || string.IsNullOrWhiteSpace(capture.VideoUrl))
        {
            logCallback?.Invoke("[douyin-browser] no playable mp4 response was captured.");
            return false;
        }

        logCallback?.Invoke("[douyin-browser] captured playable mp4 URL.");
        ApplyCapturedMetadata(task, capture);
        if (!string.IsNullOrWhiteSpace(task.Title))
            logCallback?.Invoke($"[douyin-browser] title: {task.Title}");

        Directory.CreateDirectory(task.OutputDirectory);
        var outputPath = BuildOutputPath(task.OutputDirectory, task.Title);
        var tempPath = $"{outputPath}.part";

        task.Status = DownloadStatus.Downloading;
        await DownloadFileAsync(capture.VideoUrl, tempPath, outputPath, task.Url, progress, ct);

        task.OutputFilePath = outputPath;
        task.FileSize = new FileInfo(outputPath).Length;
        task.Progress = 100;
        progress?.Report(new DownloadProgress
        {
            Percent = 100,
            Downloaded = task.FileSize,
            Total = task.FileSize,
            RawLine = "[douyin-browser] completed"
        });
        task.Status = DownloadStatus.Completed;
        logCallback?.Invoke($"[douyin-browser] completed: {outputPath}");
        return true;
    }

    internal static bool TryExtractVideoUrlFromCdpMessage(string message, out string videoUrl)
    {
        videoUrl = "";

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || !method.ValueEquals("Network.responseReceived"))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("response", out var response))
            {
                return false;
            }

            var url = response.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";

            var mimeType = response.TryGetProperty("mimeType", out var mimeElement)
                ? mimeElement.GetString() ?? ""
                : "";

            if (IsDouyinVideoResponse(url, mimeType))
            {
                videoUrl = url;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static bool TryExtractThumbnailUrlFromCdpMessage(string message, out string thumbnailUrl)
    {
        thumbnailUrl = "";

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || !method.ValueEquals("Network.responseReceived"))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("response", out var response))
            {
                return false;
            }

            var url = response.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";

            var mimeType = response.TryGetProperty("mimeType", out var mimeElement)
                ? mimeElement.GetString() ?? ""
                : "";

            if (IsDouyinThumbnailResponse(url, mimeType))
            {
                thumbnailUrl = url;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static string BuildOutputPath(string outputDirectory, string? title)
    {
        var fileName = $"{DownloadFileNameBuilder.SanitizeResolvedTitle(title)}.mp4";
        var outputPath = Path.Combine(outputDirectory, fileName);
        if (!File.Exists(outputPath))
            return outputPath;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(outputDirectory, $"{stem} ({i}).mp4");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(outputDirectory, $"{stem} ({Guid.NewGuid():N}).mp4");
    }

    internal static string NormalizeDouyinTitle(string? title)
    {
        var normalized = (title ?? "")
            .Replace("\r", "")
            .Replace("\n", " ")
            .Trim();

        if (normalized.EndsWith("- 抖音", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4].Trim();

        if (normalized.StartsWith("《") && normalized.Contains('》'))
            normalized = normalized.Remove(0, 1).Replace("》", "", StringComparison.Ordinal);

        return normalized.Trim();
    }

    internal static void ApplyCapturedMetadata(DownloadTask task, DouyinBrowserCaptureResult capture)
    {
        void Apply()
        {
            var title = NormalizeDouyinTitle(capture.Title);
            if (!string.IsNullOrWhiteSpace(title))
                task.Title = title;

            if (string.IsNullOrWhiteSpace(task.Platform))
                task.Platform = "Douyin";

            if (!string.IsNullOrWhiteSpace(capture.ThumbnailUrl))
                task.ThumbnailUrl = capture.ThumbnailUrl;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private static bool IsDouyinVideoResponse(string url, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return mimeType.Contains("video/mp4", StringComparison.OrdinalIgnoreCase)
            && (url.Contains("douyinvod.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("douyin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDouyinThumbnailResponse(string url, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return (mimeType.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("image/webp", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("image/png", StringComparison.OrdinalIgnoreCase))
            && (url.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("douyinstatic.com", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<DouyinBrowserCaptureResult?> CaptureVideoAsync(
        string browserPath,
        string url,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        var port = GetFreeTcpPort();
        var userDataDir = Path.Combine(Path.GetTempPath(), $"easyget-douyin-browser-{Guid.NewGuid():N}");

        using var process = StartBrowser(browserPath, port, userDataDir);
        try
        {
            await WaitForDevToolsAsync(port, ct);
            var webSocketUrl = await CreatePageAsync(port, ct);

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(webSocketUrl), ct);

            var id = 1;
            await SendCdpCommandAsync(socket, id++, "Network.enable", null, ct);
            await SendCdpCommandAsync(socket, id++, "Page.enable", null, ct);
            await SendCdpCommandAsync(socket, id++, "Network.setUserAgentOverride", new Dictionary<string, object?>
            {
                ["userAgent"] = BrowserUserAgent
            }, ct);
            await SendCdpCommandAsync(socket, id++, "Page.navigate", new Dictionary<string, object?>
            {
                ["url"] = url
            }, ct);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            DateTime? firstCandidateAt = null;
            string? bestVideoUrl = null;
            string? thumbnailUrl = null;
            long bestVideoSize = 0;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                var receiveTask = ReceiveCdpMessageAsync(socket, ct);
                var timeoutTask = Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct);
                var completed = await Task.WhenAny(receiveTask, timeoutTask);
                if (completed != receiveTask)
                    continue;

                var message = await receiveTask;
                if (thumbnailUrl is null && TryExtractThumbnailUrlFromCdpMessage(message, out var capturedThumbnail))
                    thumbnailUrl = capturedThumbnail;

                if (TryExtractVideoCandidateFromCdpMessage(message, out var videoUrl, out var sizeHint))
                {
                    firstCandidateAt ??= DateTime.UtcNow;
                    if (bestVideoUrl is null || sizeHint > bestVideoSize)
                    {
                        bestVideoUrl = videoUrl;
                        bestVideoSize = sizeHint;
                    }

                    if (DateTime.UtcNow - firstCandidateAt > TimeSpan.FromSeconds(5))
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestVideoUrl))
            {
                var title = await EvaluateStringAsync(socket, id++, "document.title", ct);
                var thumbnail = await EvaluateStringAsync(socket, id++, """
                    document.querySelector('meta[property="og:image"]')?.content
                      || document.querySelector('meta[name="twitter:image"]')?.content
                      || document.querySelector('video')?.poster
                      || ''
                    """, ct);

                return new DouyinBrowserCaptureResult(bestVideoUrl, title, string.IsNullOrWhiteSpace(thumbnail) ? thumbnailUrl ?? "" : thumbnail);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[douyin-browser] fallback failed: {ex.Message}");
        }
        finally
        {
            TryKill(process);
            TryDeleteDirectory(userDataDir);
        }

        return null;
    }

    private static bool TryExtractVideoCandidateFromCdpMessage(string message, out string videoUrl, out long sizeHint)
    {
        videoUrl = "";
        sizeHint = 0;

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || !method.ValueEquals("Network.responseReceived"))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("response", out var response))
            {
                return false;
            }

            var url = response.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";

            var mimeType = response.TryGetProperty("mimeType", out var mimeElement)
                ? mimeElement.GetString() ?? ""
                : "";

            if (!IsDouyinVideoResponse(url, mimeType))
                return false;

            videoUrl = url;
            sizeHint = TryGetResponseSizeHint(response);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long TryGetResponseSizeHint(JsonElement response)
    {
        if (!response.TryGetProperty("headers", out var headers))
            return 0;

        var contentRange = GetHeader(headers, "content-range");
        if (!string.IsNullOrWhiteSpace(contentRange))
        {
            var slashIndex = contentRange.LastIndexOf('/');
            if (slashIndex >= 0 && long.TryParse(contentRange[(slashIndex + 1)..], out var rangeTotal))
                return rangeTotal;
        }

        var contentLength = GetHeader(headers, "content-length");
        return long.TryParse(contentLength, out var length) ? length : 0;
    }

    private static string GetHeader(JsonElement headers, string headerName)
    {
        foreach (var header in headers.EnumerateObject())
        {
            if (header.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                return header.Value.GetString() ?? "";
        }

        return "";
    }

    private static Process StartBrowser(string browserPath, int port, string userDataDir)
    {
        Directory.CreateDirectory(userDataDir);
        var args = string.Join(" ", [
            "--headless=new",
            "--disable-gpu",
            "--mute-audio",
            "--no-first-run",
            "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required",
            $"--remote-debugging-port={port}",
            $"--user-data-dir=\"{userDataDir}\"",
            "about:blank"
        ]);

        return Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start browser.");
    }

    private static async Task WaitForDevToolsAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await HttpClient.GetAsync($"http://127.0.0.1:{port}/json/version", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Browser is still starting.
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException("Timed out waiting for browser DevTools.");
    }

    private static async Task<string> CreatePageAsync(int port, CancellationToken ct)
    {
        using var response = await HttpClient.PutAsync($"http://127.0.0.1:{port}/json/new?about%3Ablank", null, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("Browser did not return a page websocket URL.");
    }

    private static async Task SendCdpCommandAsync(
        ClientWebSocket socket,
        int id,
        string method,
        Dictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var payload = parameters is null
            ? JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method
            })
            : JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            });

        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveCdpMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return "";

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> EvaluateStringAsync(ClientWebSocket socket, int id, string expression, CancellationToken ct)
    {
        await SendCdpCommandAsync(socket, id, "Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true
        }, ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveCdpMessageAsync(socket, ct);
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var responseId)
                    || responseId.GetInt32() != id
                    || !root.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("result", out var valueResult))
                {
                    continue;
                }

                if (valueResult.TryGetProperty("value", out var value))
                    return value.GetString() ?? "";
            }
            catch (JsonException)
            {
                // Ignore unrelated CDP events.
            }
        }

        return "";
    }

    internal static async Task DownloadFileAsync(
        string url,
        string tempPath,
        string outputPath,
        string referer,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        long total = 0;
        var started = DateTime.UtcNow;
        var fileMode = FileMode.Create;

        try
        {
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.Referrer = new Uri(referer);
                if (downloaded > 0)
                    request.Headers.Range = new RangeHeaderValue(downloaded, null);

                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                if (downloaded > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    downloaded = 0;
                    fileMode = FileMode.Create;
                }

                total = response.Content.Headers.ContentRange?.Length
                    ?? Math.Max(total, response.Content.Headers.ContentLength ?? 0);

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.Read))
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;

                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;

                        var elapsed = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.1);
                        var speed = downloaded / elapsed;
                        var percent = total > 0 ? downloaded * 100d / total : 0;
                        var eta = total > 0 && speed > 0 ? (total - downloaded) / speed : 0;
                        progress?.Report(new DownloadProgress
                        {
                            Percent = percent,
                            Downloaded = downloaded,
                            Total = total,
                            Speed = speed,
                            Eta = eta,
                            RawLine = "[douyin-browser] downloading"
                        });
                    }
                }

                if (total <= 0 || downloaded >= total)
                    break;

                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException($"抖音兜底下载不完整：已下载 {downloaded} / {total} 字节。");

                fileMode = FileMode.Append;
            }

            if (total > 0 && downloaded != total)
                throw new IOException($"抖音兜底下载字节数异常：已下载 {downloaded} / {total} 字节。");
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
    }

    private static string? FindChromiumBrowserPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        [
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
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
            // Best effort cleanup.
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
            // Best effort cleanup.
        }
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
            // Best effort cleanup.
        }
    }
}
