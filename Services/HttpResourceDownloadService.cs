using System.IO;
using System.Net;
using System.Net.Http;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 下载课程附件和普通 HTTP 资源，不经过 yt-dlp 的视频格式选择逻辑。
/// </summary>
public sealed class HttpResourceDownloadService
{
    private const int MaxRetries = 5;
    private const int BufferSize = 81920;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "ppt", "pptx", "doc", "docx", "xls", "xlsx", "csv", "txt", "md", "epub",
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "svg",
        "zip", "rar", "7z", "tar", "gz", "bz2",
        "mp3", "m4a", "wav", "flac", "aac", "ogg", "opus"
    };

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = "pdf",
        ["application/zip"] = "zip",
        ["application/x-rar-compressed"] = "rar",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/msword"] = "doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "m4a",
        ["audio/wav"] = "wav",
        ["audio/ogg"] = "ogg"
    };

    private readonly ConfigService _configService;

    public HttpResourceDownloadService(ConfigService configService)
    {
        _configService = configService;
    }

    public static bool IsResourceUrl(string? url)
    {
        if (!TryCreateHttpUri(url, out var uri))
            return false;

        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
        return SupportedExtensions.Contains(extension);
    }

    public static string ResolveExtension(
        string? url,
        string? extensionHint = null,
        string? mimeType = null)
    {
        var normalizedHint = NormalizeExtension(extensionHint);
        if (normalizedHint.Length > 0 && SupportedExtensions.Contains(normalizedHint))
            return normalizedHint;

        if (TryCreateHttpUri(url, out var uri))
        {
            var fromUrl = NormalizeExtension(Path.GetExtension(uri.AbsolutePath));
            if (fromUrl.Length > 0 && SupportedExtensions.Contains(fromUrl))
                return fromUrl;
        }

        var normalizedMimeType = mimeType?.Split(';', 2)[0].Trim() ?? "";
        return MimeExtensions.GetValueOrDefault(normalizedMimeType, "bin");
    }

    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Status = DownloadStatus.Downloading;

        var extension = ResolveExtension(task.Url, task.ResourceExtension, task.ResourceMimeType);
        task.ResourceExtension = extension;
        task.IsNonVideoResource = true;
        task.Format = extension;
        var title = DownloadFileNameBuilder.SanitizeResolvedTitle(
            string.IsNullOrWhiteSpace(task.Title) ? "课程资源" : task.Title);
        var titleExtension = NormalizeExtension(Path.GetExtension(title));
        if (!string.Equals(titleExtension, extension, StringComparison.OrdinalIgnoreCase))
        {
            title += $".{extension}";
        }

        using var reservation = DownloadOutputPathReservation.Reserve(task.OutputDirectory, title);
        var temporaryPath = Path.Combine(
            task.OutputDirectory,
            $".easyget-resource-{Guid.NewGuid():N}.part");

        using var handler = new HttpClientHandler();
        if (_configService.Config.UseProxy
            && !string.IsNullOrWhiteSpace(_configService.Config.ProxyAddress))
        {
            handler.Proxy = new WebProxy(_configService.Config.ProxyAddress);
            handler.UseProxy = true;
            logCallback?.Invoke($"[resource] 启用代理: {_configService.Config.ProxyAddress}");
        }

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        try
        {
            Exception? lastException = null;
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                    Directory.CreateDirectory(task.OutputDirectory);

                    using var response = await httpClient.GetAsync(
                        task.Url,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);
                    response.EnsureSuccessStatusCode();
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (LooksLikeHtml(contentType, extension))
                    {
                        throw new InvalidDataException(
                            "服务器返回了网页而不是课程资源，已停止保存 HTML 页面。");
                    }

                    var total = response.Content.Headers.ContentLength ?? 0;
                    long downloaded = 0;
                    await using (var input = await response.Content.ReadAsStreamAsync(ct))
                    await using (var output = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     BufferSize,
                                     FileOptions.Asynchronous))
                    {
                        var buffer = new byte[BufferSize];
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            downloaded += read;
                            progress?.Report(new DownloadProgress
                            {
                                Percent = total > 0
                                    ? Math.Min(99.9, (double)downloaded / total * 100)
                                    : 0,
                                Downloaded = downloaded,
                                Total = total
                            });
                        }
                    }

                    if (downloaded <= 0)
                        throw new InvalidDataException("服务器返回了空的课程资源。");

                    File.Move(temporaryPath, reservation.Path, overwrite: false);
                    task.OutputFilePath = reservation.Path;
                    task.OutputFilePaths = [reservation.Path];
                    task.FileSize = downloaded;
                    task.Progress = 100;
                    task.Status = DownloadStatus.Completed;
                    logCallback?.Invoke($"[resource] 下载完成: {reservation.Path}");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt == MaxRetries || ex is InvalidDataException)
                        break;
                    logCallback?.Invoke($"[resource] 下载失败，将在第 {attempt} 次重试: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                }
            }

            throw new IOException($"普通资源下载失败: {lastException?.Message}", lastException);
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelledUnlessPaused();
            throw;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            logCallback?.Invoke($"[resource] 任务失败: {ex.Message}");
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool LooksLikeHtml(string contentType, string extension)
        => contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(extension, "html", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(extension, "htm", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string? extension)
        => (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();

    private static bool TryCreateHttpUri(string? url, out Uri uri)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
