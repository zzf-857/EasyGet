using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;

namespace EasyGet.Services;

public class XiaohongshuImageDownloadService
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly ConfigService _configService;

    public XiaohongshuImageDownloadService(ConfigService configService)
    {
        _configService = configService;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (_configService.Config.UseProxy && !string.IsNullOrWhiteSpace(_configService.Config.ProxyAddress))
        {
            try
            {
                handler.Proxy = new WebProxy(_configService.Config.ProxyAddress);
                handler.UseProxy = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[XhsImageDownload] Failed to configure proxy: {ex.Message}");
            }
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    public async Task<VideoInfo?> GetImageNoteInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateHttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;

            var noteId = ExtractNoteId(finalUrl);
            if (string.IsNullOrEmpty(noteId))
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var noteData = ExtractNoteDataFromJson(html, noteId);
            if (noteData == null)
                return null;

            var title = "";
            if (noteData.Value.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                title = titleProp.GetString();

            if (string.IsNullOrWhiteSpace(title) && noteData.Value.TryGetProperty("desc", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                var desc = descProp.GetString() ?? "";
                title = desc.Split('\n', '\r').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
                if (title.Length > 60)
                    title = title[..60] + "...";
            }

            if (string.IsNullOrWhiteSpace(title))
                title = $"小红书图文笔记_{noteId}";

            title = title.Trim();

            // Find thumbnail URL
            var thumbnailUrl = "";
            if (noteData.Value.TryGetProperty("imageList", out var imageListProp) && imageListProp.ValueKind == JsonValueKind.Array && imageListProp.GetArrayLength() > 0)
            {
                var firstImage = imageListProp[0];
                if (firstImage.TryGetProperty("urlDefault", out var defaultUrlVal) && !string.IsNullOrWhiteSpace(defaultUrlVal.GetString()))
                {
                    thumbnailUrl = defaultUrlVal.GetString() ?? "";
                }
                else if (firstImage.TryGetProperty("infoList", out var infoListVal) && infoListVal.ValueKind == JsonValueKind.Array)
                {
                    foreach (var infoItem in infoListVal.EnumerateArray())
                    {
                        if (infoItem.TryGetProperty("imageScene", out var sceneVal) && sceneVal.GetString() == "WB_DFT")
                        {
                            if (infoItem.TryGetProperty("url", out var urlVal))
                            {
                                thumbnailUrl = urlVal.GetString() ?? "";
                                break;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(thumbnailUrl) && infoListVal.GetArrayLength() > 0)
                    {
                        if (infoListVal[0].TryGetProperty("url", out var urlVal))
                            thumbnailUrl = urlVal.GetString() ?? "";
                    }
                }
            }

            return new VideoInfo
            {
                Title = title,
                Platform = "XiaoHongShu",
                Duration = 0,
                Thumbnail = thumbnailUrl,
                FileSize = 0,
                Url = finalUrl
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[XhsImageDownload] GetImageNoteInfoAsync failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> TryDownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        try
        {
            logCallback?.Invoke("[xhs-image] 开始小红书图片提取与下载...");
            progress?.Report(new DownloadProgress { Percent = 5, RawLine = "[xhs-image] 正在解析链接..." });

            using var client = CreateHttpClient();
            using var response = await client.GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? task.Url;

            var noteId = ExtractNoteId(finalUrl);
            if (string.IsNullOrEmpty(noteId))
            {
                logCallback?.Invoke("[xhs-image] 错误：无法从链接中提取有效的笔记 ID。");
                return false;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var noteData = ExtractNoteDataFromJson(html, noteId);
            if (noteData == null)
            {
                logCallback?.Invoke("[xhs-image] 错误：未能在页面 HTML 中提取到合法的 `window.__INITIAL_STATE__`。");
                return false;
            }

            // Extract title if not set
            var title = task.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                if (noteData.Value.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                    title = titleProp.GetString();

                if (string.IsNullOrWhiteSpace(title) && noteData.Value.TryGetProperty("desc", out var descProp) && descProp.ValueKind == JsonValueKind.String)
                {
                    var desc = descProp.GetString() ?? "";
                    title = desc.Split('\n', '\r').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
                    if (title.Length > 60)
                        title = title[..60] + "...";
                }

                if (string.IsNullOrWhiteSpace(title))
                    title = $"小红书图文笔记_{noteId}";

                title = title.Trim();
                ApplyTitle(task, title);
            }

            // Extract all image URLs
            var imageUrls = new List<string>();
            if (noteData.Value.TryGetProperty("imageList", out var imageListProp) && imageListProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var imageObj in imageListProp.EnumerateArray())
                {
                    string imageUrl = "";
                    if (imageObj.TryGetProperty("urlDefault", out var defaultUrlVal) && !string.IsNullOrWhiteSpace(defaultUrlVal.GetString()))
                    {
                        imageUrl = defaultUrlVal.GetString() ?? "";
                    }
                    else if (imageObj.TryGetProperty("infoList", out var infoListVal) && infoListVal.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var infoItem in infoListVal.EnumerateArray())
                        {
                            if (infoItem.TryGetProperty("imageScene", out var sceneVal) && sceneVal.GetString() == "WB_DFT")
                            {
                                if (infoItem.TryGetProperty("url", out var urlVal))
                                {
                                    imageUrl = urlVal.GetString() ?? "";
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(imageUrl) && infoListVal.GetArrayLength() > 0)
                        {
                            if (infoListVal[0].TryGetProperty("url", out var urlVal))
                                imageUrl = urlVal.GetString() ?? "";
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        imageUrl = imageUrl.Replace("\\u002F", "/").Replace("&amp;", "&");
                        if (imageUrl.StartsWith("//"))
                            imageUrl = "https:" + imageUrl;
                        imageUrls.Add(imageUrl);
                    }
                }
            }

            if (imageUrls.Count == 0)
            {
                logCallback?.Invoke("[xhs-image] 错误：该笔记未包含任何图片。");
                return false;
            }

            logCallback?.Invoke($"[xhs-image] 找到 {imageUrls.Count} 张图片。正在准备下载目录...");

            var sanitizedTitle = DownloadFileNameBuilder.SanitizeResolvedTitle(title);
            var subfolderPath = Path.Combine(task.OutputDirectory, sanitizedTitle);
            Directory.CreateDirectory(subfolderPath);

            logCallback?.Invoke($"[xhs-image] 正在保存至文件夹：{subfolderPath}");

            long totalBytes = 0;
            var savedFiles = new List<string>();

            for (var idx = 0; idx < imageUrls.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();

                var imageUrl = imageUrls[idx];
                var ext = ".jpg";
                if (imageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase))
                    ext = ".png";
                else if (imageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase))
                    ext = ".webp";
                else if (imageUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase))
                    ext = ".gif";

                var fileName = $"{(idx + 1)}{ext}";
                var imagePath = Path.Combine(subfolderPath, fileName);

                logCallback?.Invoke($"[xhs-image] 正在下载第 {idx + 1}/{imageUrls.Count} 张图片...");

                var startProgress = 5.0 + (90.0 * idx / imageUrls.Count);
                progress?.Report(new DownloadProgress
                {
                    Percent = startProgress,
                    RawLine = $"[xhs-image] 正在下载图片 {idx + 1}/{imageUrls.Count}"
                });

                await DownloadFileAsync(client, imageUrl, imagePath, finalUrl, ct);

                savedFiles.Add(imagePath);
                if (File.Exists(imagePath))
                {
                    totalBytes += new FileInfo(imagePath).Length;
                }
            }

            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            if (savedFiles.Count > 0)
            {
                task.OutputFilePath = savedFiles[0];
                var extension = Path.GetExtension(savedFiles[0]).TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrEmpty(extension))
                {
                    task.Format = extension;
                }
            }
            task.FileSize = totalBytes;

            progress?.Report(new DownloadProgress
            {
                Percent = 100,
                Downloaded = totalBytes,
                Total = totalBytes,
                RawLine = "[xhs-image] 所有图片下载完成。"
            });

            logCallback?.Invoke($"[xhs-image] 图片下载成功！总大小：{ByteSizeFormatter.FormatClampZero(totalBytes)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            logCallback?.Invoke("[xhs-image] 下载已取消。");
            return false;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[xhs-image] 下载过程中发生异常：{ex.Message}");
            return false;
        }
    }

    internal static string ExtractNoteId(string url)
    {
        var match = Regex.Match(url, @"/(?:discovery/item|explore)/([a-zA-Z0-9]+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    internal static JsonElement? ExtractNoteDataFromJson(string html, string noteId)
    {
        var match = Regex.Match(html, @"window\.__INITIAL_STATE__\s*=\s*(.+?)</script>", RegexOptions.Singleline);
        if (!match.Success)
        {
            match = Regex.Match(html, @"window\.__INITIAL_SSR_STATE__\s*=\s*(.+?)</script>", RegexOptions.Singleline);
        }

        if (!match.Success)
            return null;

        var jsonStr = match.Groups[1].Value.Trim();
        if (jsonStr.EndsWith(";"))
            jsonStr = jsonStr[..^1].Trim();

        // Replace undefined with null
        jsonStr = Regex.Replace(jsonStr, @":\s*undefined", ":null");

        try
        {
            var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.TryGetProperty("note", out var noteProp) &&
                noteProp.TryGetProperty("noteDetailMap", out var detailMapProp) &&
                detailMapProp.TryGetProperty(noteId, out var noteDetailItem))
            {
                if (noteDetailItem.TryGetProperty("note", out var innerNote))
                {
                    return innerNote.Clone();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[XhsImageDownload] Failed to parse initial state JSON: {ex.Message}");
        }

        return null;
    }

    private static void ApplyTitle(DownloadTask task, string title)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            task.Title = title;
        else
            dispatcher.Invoke(() => task.Title = title);
    }

    private static async Task DownloadFileAsync(HttpClient client, string url, string outputPath, string referer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(referer);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

}
