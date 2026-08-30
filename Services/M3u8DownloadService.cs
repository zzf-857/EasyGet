using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyGet.Models;

namespace EasyGet.Services;

public class M3u8DownloadService
{
    private const int DefaultSegmentConcurrency = 16;
    private const int SegmentIoBufferSize = 81920;

    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;

    private const int ManifestMaxRetries = 3;
    private const int MaxPlaylistNestingDepth = 4;

    public M3u8DownloadService(ConfigService configService, EnvironmentService envService)
    {
        _configService = configService;
        _envService = envService;
    }

    /// <summary>
    /// 判断是否为 m3u8 / m3n8 链接
    /// </summary>
    public static bool IsM3u8Url(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var path = GetUrlPathWithoutQueryOrFragment(url.Trim());
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3n8", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUrlPathWithoutQueryOrFragment(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath;

        var withoutFragment = url.Split('#', 2)[0];
        return withoutFragment.Split('?', 2)[0];
    }

    /// <summary>
    /// 核心下载方法
    /// </summary>
    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        task.Status = DownloadStatus.Downloading;
        logCallback?.Invoke($"[m3u8] 开始下载: {task.Url}");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var workingPaths = CreateWorkingPaths(task.OutputDirectory);
        var tempDir = workingPaths.SegmentDirectory;
        var outputTsPath = workingPaths.TransportStreamPath;
        var muxedOutputPath = workingPaths.MuxedOutputPath;
        
        // 最终输出视频文件
        var finalName = DownloadFileNameBuilder.SanitizeResolvedTitle(
            string.IsNullOrWhiteSpace(task.Title)
                ? $"m3u8_{timestamp}_{workingPaths.OperationId[..8]}"
                : task.Title);
        if (!finalName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            finalName += ".mp4";
        }
        using var finalOutputReservation = DownloadOutputPathReservation.Reserve(
            task.OutputDirectory,
            finalName);
        var finalMp4Path = finalOutputReservation.Path;

        // 初始化 HttpClientHandler，支持代理
        var handler = new HttpClientHandler();
        if (_configService.Config.UseProxy && !string.IsNullOrWhiteSpace(_configService.Config.ProxyAddress))
        {
            try
            {
                handler.Proxy = new WebProxy(_configService.Config.ProxyAddress);
                handler.UseProxy = true;
                logCallback?.Invoke($"[m3u8] 启用代理: {_configService.Config.ProxyAddress}");
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[m3u8] 代理配置失败: {ex.Message}");
            }
        }

        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            // 1. 获取并解析 m3u8 文件
            logCallback?.Invoke("[m3u8] 正在获取 m3u8 文件清单...");
            string m3u8Content;
            try
            {
                m3u8Content = await GetPlaylistContentWithRetryAsync(
                    httpClient,
                    task.Url,
                    logCallback,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception($"无法获取 m3u8 文件: {ex.Message}", ex);
            }

            var playlist = await LoadMediaPlaylistAsync(
                httpClient,
                m3u8Content,
                task.Url,
                logCallback,
                ct);
            var segmentRequests = playlist.Segments;
            var segments = segmentRequests.Select(segment => segment.Url).ToList();
            var totalSegments = segments.Count;
            logCallback?.Invoke($"[m3u8] 共解析出 {totalSegments} 个视频分片。");

            long totalDownloadedBytes = 0;
            long lastReportedBytes = 0;
            long completedSegments = 0;

            if (totalSegments == 0)
            {
                throw new Exception("未解析到任何分片，请检查 m3u8 链接是否正确。");
            }

            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            var keyCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string? initializationPath = null;
            if (playlist.InitializationSegment is not null)
            {
                initializationPath = Path.Combine(tempDir, "init.segment");
                logCallback?.Invoke("[m3u8] 正在下载 fMP4 初始化片段...");
                var initializationDownloaded = await DownloadRequestWithRetryAsync(
                    httpClient,
                    playlist.InitializationSegment,
                    initializationPath,
                    bytes => Interlocked.Add(ref totalDownloadedBytes, bytes),
                    logCallback,
                    keyCache,
                    ct);
                if (!initializationDownloaded)
                    throw new IOException("M3U8 初始化片段下载失败，已停止合并。");
            }

            // 2. 多线程下载分片
            logCallback?.Invoke("[m3u8] 开始多线程下载分片...");
            var stopwatch = Stopwatch.StartNew();
            using var speedReportCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var speedReportTask = RunPeriodicProgressReporterAsync(
                () =>
                {
                    var currentBytes = Interlocked.Read(ref totalDownloadedBytes);
                    var speed = currentBytes - lastReportedBytes;
                    lastReportedBytes = currentBytes;

                    var currentCompleted = Interlocked.Read(ref completedSegments);
                    double eta = 0;
                    if (currentCompleted > 0 && currentCompleted < totalSegments)
                    {
                        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                        var averageSecondsPerSegment = elapsedSeconds / currentCompleted;
                        eta = averageSecondsPerSegment * (totalSegments - currentCompleted);
                    }

                    return new DownloadProgress
                    {
                        Percent = Math.Min(99.9, (double)currentCompleted / totalSegments * 100),
                        Speed = speed,
                        Eta = eta,
                        Downloaded = currentBytes,
                        Total = 0
                    };
                },
                progress,
                TimeSpan.FromSeconds(1),
                speedReportCancellation.Token);

            IReadOnlyList<int> stillFailed = [];
            try
            {
                var maxParallelSegments = ResolveSegmentConcurrency(
                    _configService.Config.ConcurrentFragments,
                    _configService.Config.MaxConcurrentDownloads);
                logCallback?.Invoke($"[m3u8] 分片下载并发数: {maxParallelSegments}");
                var failedIndices = (await DownloadSegmentsWithWorkersAsync(
                    segments,
                    maxParallelSegments,
                    (index, segUrl) => DownloadSegmentWithRetryAsync(
                        httpClient,
                        segmentRequests[index],
                        segUrl,
                        index,
                        tempDir,
                        bytes => Interlocked.Add(ref totalDownloadedBytes, bytes),
                        logCallback,
                        keyCache,
                        ct),
                    _ =>
                    {
                        Interlocked.Increment(ref completedSegments);
                        var currentCompleted = Interlocked.Read(ref completedSegments);
                        progress?.Report(new DownloadProgress
                        {
                            Percent = Math.Min(99.9, (double)currentCompleted / totalSegments * 100),
                            Downloaded = Interlocked.Read(ref totalDownloadedBytes),
                            Total = 0
                        });
                    },
                    ct)).ToList();

                // 3. 重试下载失败的分片
                stillFailed = failedIndices;
                if (failedIndices.Count > 0)
                {
                    logCallback?.Invoke($"[m3u8] 警告: 有 {failedIndices.Count} 个分片下载失败。开始重试...");
                    stillFailed = await RetryFailedSegmentsAsync(
                        failedIndices,
                        segments,
                        maxParallelSegments,
                        (index, segUrl) => DownloadSegmentWithRetryAsync(
                            httpClient,
                            segmentRequests[index],
                            segUrl,
                            index,
                            tempDir,
                            bytes => Interlocked.Add(ref totalDownloadedBytes, bytes),
                            logCallback,
                            keyCache,
                            ct),
                        _ => Interlocked.Increment(ref completedSegments),
                        logCallback,
                        ct);
                }
            }
            finally
            {
                stopwatch.Stop();
                speedReportCancellation.Cancel();
                try
                {
                    await speedReportTask;
                }
                catch (OperationCanceledException) when (speedReportCancellation.IsCancellationRequested)
                {
                }
            }

            EnsureSegmentsReadyForMerge(tempDir, totalSegments, stillFailed);
            logCallback?.Invoke("[m3u8] 分片下载完成，开始拼接...");

            // 4. 拼接分片为单个 ts 文件
            using (var outfile = new FileStream(outputTsPath, FileMode.Create, FileAccess.Write, FileShare.None, SegmentIoBufferSize, useAsync: true))
            {
                if (initializationPath is not null)
                {
                    await using var initializationFile = new FileStream(
                        initializationPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        SegmentIoBufferSize,
                        useAsync: true);
                    await initializationFile.CopyToAsync(outfile, SegmentIoBufferSize, ct);
                }

                for (int i = 0; i < totalSegments; i++)
                {
                    var partPath = Path.Combine(tempDir, $"{i:D4}.ts");
                    using var infile = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, SegmentIoBufferSize, useAsync: true);
                    await infile.CopyToAsync(outfile, SegmentIoBufferSize, ct);
                }
            }

            // 5. 使用 ffmpeg 转换为 MP4
            logCallback?.Invoke("[m3u8] 拼接完成。正在转换为 MP4...");
            task.Status = DownloadStatus.Merging;
            progress?.Report(new DownloadProgress { Percent = 99.9 });

            var ffmpegPath = _envService.Status.FfmpegPath;
            bool convertSuccess = false;

            if (_envService.Status.FfmpegFound && !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(outputTsPath))
            {
                try
                {
                    // 确保输出路径目录存在
                    Directory.CreateDirectory(Path.GetDirectoryName(finalMp4Path)!);
                    
                    var arguments = new[] { "-y", "-i", outputTsPath, "-c", "copy", muxedOutputPath };
                    logCallback?.Invoke($"[m3u8] 运行 ffmpeg: {ffmpegPath} {string.Join(' ', arguments)}");

                    var processResult = await YtDlpService.RunProcessAsync(
                        ffmpegPath,
                        arguments,
                        TimeSpan.FromMinutes(10),
                        ct);
                    convertSuccess = PromoteFfmpegOutputIfSuccessful(
                        processResult,
                        muxedOutputPath,
                        finalMp4Path);
                    if (convertSuccess)
                    {
                        logCallback?.Invoke($"[m3u8] 转换成功！已生成 MP4: {finalMp4Path}");
                        File.Delete(outputTsPath);
                    }
                    else
                    {
                        TryDeleteTemporaryFile(muxedOutputPath);
                        logCallback?.Invoke(
                            $"[m3u8] ffmpeg 封装未成功（退出码: {processResult.ExitCode}），将保留 TS 流作为回退输出。");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    TryDeleteTemporaryFile(muxedOutputPath);
                    logCallback?.Invoke($"[m3u8] 使用 ffmpeg 封装 MP4 失败: {ex.Message}");
                }
            }

            if (!convertSuccess)
            {
                logCallback?.Invoke("[m3u8] ffmpeg 封装失败或不可用，直接重命名 TS 文件为 MP4...");
                try
                {
                    File.Move(outputTsPath, finalMp4Path, overwrite: false);
                    logCallback?.Invoke($"[m3u8] 重命名成功！已生成: {finalMp4Path} (注意：此文件实质为 TS 流格式)");
                }
                catch (Exception renameErr)
                {
                    throw new Exception($"保存视频文件失败: {renameErr.Message}", renameErr);
                }
            }

            // 6. 下载完成状态更新
            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            task.OutputFilePath = finalMp4Path;
            task.FileSize = new FileInfo(finalMp4Path).Length;
            logCallback?.Invoke($"[m3u8] 任务全部结束。已保存至: {finalMp4Path}");
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelledUnlessPaused();
            logCallback?.Invoke("[m3u8] 任务已取消。");
            throw;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            logCallback?.Invoke($"[m3u8] 任务失败: {ex.Message}");
            throw;
        }
        finally
        {
            TryDeleteTemporaryFile(outputTsPath);
            TryDeleteTemporaryFile(muxedOutputPath);

            // 清理临时分片目录
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[m3u8] 清理临时目录失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 解析 m3u8 内容获取分片地址
    /// </summary>
    internal static List<string> ParseSegments(string m3u8Content, string m3u8Url)
    {
        ArgumentNullException.ThrowIfNull(m3u8Content);
        var playlist = StripPlaylistPreamble(m3u8Content);
        if (!playlist.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("不是有效的 M3U8 播放列表");

        var mediaPlaylist = ParseMediaPlaylist(m3u8Content, m3u8Url);
        if (mediaPlaylist.Segments.Any(segment => !string.IsNullOrWhiteSpace(segment.KeyUrl)))
            throw new NotSupportedException("该 m3u8 视频流被加密，当前同步解析入口不执行解密。");

        return mediaPlaylist.Segments
            .Select(segment => segment.Url)
            .ToList();
    }

    internal static M3u8MediaPlaylist ParseMediaPlaylist(string m3u8Content, string m3u8Url)
    {
        ArgumentNullException.ThrowIfNull(m3u8Content);
        var playlist = StripPlaylistPreamble(m3u8Content);
        if (!playlist.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("不是有效的 M3U8 播放列表");

        var baseUri = CreatePlaylistUri(m3u8Url);
        var segments = new List<M3u8SegmentRequest>();
        M3u8SegmentRequest? initializationSegment = null;
        M3u8KeyState? currentKey = null;
        long? previousByteRangeEnd = null;
        string? previousByteRangeUrl = null;
        var nextSequence = 0L;
        var hasVariant = false;
        var hasMediaSegment = false;
        string? pendingByteRangeText = null;
        var lines = EnumeratePlaylistLines(playlist).ToList();

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var trimmedLine = lines[lineIndex].Span.Trim();
            if (trimmedLine.IsEmpty)
                continue;

            if (trimmedLine.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                hasVariant = true;
                continue;
            }

            if (trimmedLine.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(
                        trimmedLine["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsedSequence)
                    && parsedSequence >= 0)
                {
                    nextSequence = parsedSequence;
                }
                continue;
            }

            if (trimmedLine.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                currentKey = ParseKeyState(trimmedLine["#EXT-X-KEY:".Length..].ToString(), baseUri, nextSequence);
                continue;
            }

            if (trimmedLine.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributeList(trimmedLine["#EXT-X-MAP:".Length..].ToString());
                var mapUrl = ResolveRequiredUri(attributes, "URI", baseUri, "初始化片段");
                M3u8ByteRange? mapRange = null;
                if (attributes.TryGetValue("BYTERANGE", out var mapRangeText))
                {
                    if (!TryParseByteRange(mapRangeText, 0, out var parsedMapRange))
                        throw new NotSupportedException($"无法解析 M3U8 初始化片段字节范围: {mapRangeText}");
                    mapRange = parsedMapRange;
                }
                var parsedMap = new M3u8SegmentRequest(
                    mapUrl,
                    currentKey?.KeyUrl,
                    currentKey?.InitializationVector,
                    mapRange?.Length,
                    mapRange?.Offset,
                    nextSequence);
                if (initializationSegment is not null
                    && (!string.Equals(initializationSegment.Url, parsedMap.Url, StringComparison.Ordinal)
                        || initializationSegment.RangeLength != parsedMap.RangeLength
                        || initializationSegment.RangeOffset != parsedMap.RangeOffset))
                {
                    throw new NotSupportedException("该 M3U8 包含多个初始化片段，当前无法安全拼接。");
                }

                initializationSegment = parsedMap;
                continue;
            }

            if (trimmedLine.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                pendingByteRangeText = trimmedLine["#EXT-X-BYTERANGE:".Length..].ToString().Trim();
                continue;
            }

            if (trimmedLine.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (hasVariant && !hasMediaSegment)
            {
                throw new NotSupportedException(
                    "该 m3u8 是主播放列表，包含多个码率的子播放列表。请提供具体媒体播放列表链接后重试。");
            }

            var segmentUrl = new Uri(baseUri, trimmedLine.ToString()).AbsoluteUri;
            M3u8ByteRange? byteRange = null;
            if (pendingByteRangeText is not null)
            {
                var fallbackOffset = string.Equals(previousByteRangeUrl, segmentUrl, StringComparison.Ordinal)
                    ? previousByteRangeEnd
                    : null;
                if (!TryParseByteRange(pendingByteRangeText, fallbackOffset, out var parsedByteRange))
                {
                    throw new NotSupportedException($"无法解析 M3U8 字节范围: {pendingByteRangeText}");
                }

                byteRange = parsedByteRange;
                previousByteRangeEnd = parsedByteRange.Offset + parsedByteRange.Length;
                previousByteRangeUrl = segmentUrl;
                pendingByteRangeText = null;
            }

            segments.Add(new M3u8SegmentRequest(
                segmentUrl,
                currentKey?.KeyUrl,
                currentKey?.InitializationVector ?? CreateInitializationVector(nextSequence),
                byteRange?.Length,
                byteRange?.Offset,
                nextSequence));
            hasMediaSegment = true;
            nextSequence++;
        }

        if (hasVariant)
        {
            throw new NotSupportedException(
                "该 m3u8 是主播放列表，包含多个码率的子播放列表。请提供具体媒体播放列表链接后重试。");
        }

        return new M3u8MediaPlaylist(segments, initializationSegment);
    }

    private async Task<M3u8MediaPlaylist> LoadMediaPlaylistAsync(
        HttpClient httpClient,
        string initialContent,
        string initialUrl,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        var content = initialContent;
        var url = initialUrl;

        for (var depth = 0; depth < MaxPlaylistNestingDepth; depth++)
        {
            if (!TryParseMasterPlaylist(content, url, out var variants))
                return ParseMediaPlaylist(content, url);

            var selected = variants
                .OrderByDescending(variant => variant.Bandwidth)
                .ThenByDescending(variant => variant.Height)
                .FirstOrDefault();
            if (selected is null)
                throw new NotSupportedException("M3U8 主播放列表没有可用的媒体变体。");

            logCallback?.Invoke(
                $"[m3u8] 检测到主播放列表，选择最高码率变体: {selected.Bandwidth} bps"
                + (selected.Height > 0 ? $" ({selected.Height}p)" : ""));
            url = selected.Url;
            content = await GetPlaylistContentWithRetryAsync(httpClient, url, logCallback, ct);
        }

        throw new NotSupportedException("M3U8 子播放列表嵌套层级过深，已停止解析。");
    }

    private static bool TryParseMasterPlaylist(
        string content,
        string playlistUrl,
        out List<M3u8Variant> variants)
    {
        variants = [];
        var normalized = StripPlaylistPreamble(content);
        if (!normalized.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("不是有效的 M3U8 播放列表");

        var baseUri = CreatePlaylistUri(playlistUrl);
        M3u8VariantBuilder? pending = null;
        foreach (var line in EnumeratePlaylistLines(normalized))
        {
            var trimmed = line.Span.Trim();
            if (trimmed.IsEmpty)
                continue;

            if (trimmed.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributeList(trimmed["#EXT-X-STREAM-INF:".Length..].ToString());
                pending = new M3u8VariantBuilder(
                    ParseLongAttribute(attributes, "BANDWIDTH"),
                    ParseResolutionHeight(attributes.GetValueOrDefault("RESOLUTION")));
                continue;
            }

            if (pending is not null && !trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                variants.Add(new M3u8Variant(
                    new Uri(baseUri, trimmed.ToString()).AbsoluteUri,
                    pending.Bandwidth,
                    pending.Height));
                pending = null;
            }
        }

        return variants.Count > 0;
    }

    private async Task<string> GetPlaylistContentWithRetryAsync(
        HttpClient httpClient,
        string url,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= ManifestMaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastException = ex;
                if (attempt == ManifestMaxRetries)
                    break;

                logCallback?.Invoke($"[m3u8] 清单请求失败，将在第 {attempt} 次重试: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            }
        }

        throw new IOException($"无法获取 M3U8 清单: {lastException?.Message}", lastException);
    }

    private static Uri CreatePlaylistUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NotSupportedException("M3U8 地址必须是有效的 HTTP/HTTPS 链接。");
        }

        return uri;
    }

    private static M3u8KeyState? ParseKeyState(
        string value,
        Uri baseUri,
        long sequence)
    {
        var attributes = ParseAttributeList(value);
        var method = attributes.GetValueOrDefault("METHOD", "NONE");
        if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!string.Equals(method, "AES-128", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"该 M3U8 使用 {method} 加密方式，当前仅支持 AES-128。");
        }

        if (attributes.TryGetValue("KEYFORMAT", out var keyFormat)
            && !string.Equals(keyFormat, "identity", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("该 M3U8 使用非 identity 密钥格式，当前无法解密。");
        }

        var keyUrl = ResolveRequiredUri(attributes, "URI", baseUri, "AES-128 密钥");
        byte[]? iv = null;
        if (attributes.TryGetValue("IV", out var ivText))
            iv = ParseInitializationVector(ivText);

        return new M3u8KeyState(keyUrl, iv, sequence);
    }

    private static byte[] ParseInitializationVector(string text)
    {
        var normalized = text.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        if (normalized.Length > 32 || normalized.Length == 0)
            throw new NotSupportedException("M3U8 AES-128 IV 长度无效。");

        normalized = normalized.PadLeft(32, '0');
        var iv = new byte[16];
        for (var index = 0; index < iv.Length; index++)
        {
            if (!byte.TryParse(
                    normalized.AsSpan(index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out iv[index]))
            {
                throw new NotSupportedException("M3U8 AES-128 IV 不是有效的十六进制值。");
            }
        }

        return iv;
    }

    private static byte[] CreateInitializationVector(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), checked((ulong)sequence));
        return iv;
    }

    private static Dictionary<string, string> ParseAttributeList(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        var inQuotes = false;
        for (var index = 0; index <= value.Length; index++)
        {
            var isEnd = index == value.Length;
            if (!isEnd && value[index] == '"')
                inQuotes = !inQuotes;
            if (!isEnd && (value[index] != ',' || inQuotes))
                continue;

            var item = value[start..index].Trim();
            var equals = item.IndexOf('=');
            if (equals > 0)
            {
                var key = item[..equals].Trim();
                var itemValue = item[(equals + 1)..].Trim();
                if (itemValue.Length >= 2 && itemValue[0] == '"' && itemValue[^1] == '"')
                    itemValue = itemValue[1..^1];
                result[key] = itemValue;
            }

            start = index + 1;
        }

        return result;
    }

    private static string ResolveRequiredUri(
        IReadOnlyDictionary<string, string> attributes,
        string attributeName,
        Uri baseUri,
        string description)
    {
        if (!attributes.TryGetValue(attributeName, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new NotSupportedException($"M3U8 {description}缺少 URI。");
        }

        return new Uri(baseUri, value.Trim()).AbsoluteUri;
    }

    private static long ParseLongAttribute(
        IReadOnlyDictionary<string, string> attributes,
        string name)
        => attributes.TryGetValue(name, out var value)
           && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? Math.Max(0, result)
            : 0;

    private static int ParseResolutionHeight(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return 0;
        var separator = resolution.IndexOf('x');
        return separator >= 0
               && int.TryParse(
                   resolution[(separator + 1)..],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var height)
            ? Math.Max(0, height)
            : 0;
    }

    private static bool TryParseByteRange(
        string? value,
        long? fallbackOffset,
        out M3u8ByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split('@', 2);
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || length <= 0)
        {
            return false;
        }

        long offset;
        if (parts.Length == 2)
        {
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                || offset < 0)
            {
                return false;
            }
        }
        else if (fallbackOffset is { } previousOffset)
        {
            offset = previousOffset;
        }
        else
        {
            return false;
        }

        range = new M3u8ByteRange(length, offset);
        return true;
    }

    internal static string StripPlaylistPreamble(string content)
    {
        var span = content.AsSpan().TrimStart();
        if (!span.IsEmpty && span[0] == '\uFEFF')
            span = span[1..].TrimStart();
        return span.ToString();
    }

    internal static async Task RunPeriodicProgressReporterAsync(
        Func<DownloadProgress> createReport,
        IProgress<DownloadProgress>? progress,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createReport);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(createReport());
        }
    }

    private static IEnumerable<ReadOnlyMemory<char>> EnumeratePlaylistLines(string content)
    {
        var start = 0;
        while (start < content.Length)
        {
            var end = start;
            while (end < content.Length && content[end] is not ('\r' or '\n'))
            {
                end++;
            }

            if (end > start)
            {
                yield return content.AsMemory(start, end - start);
            }

            start = end;
            while (start < content.Length && content[start] is '\r' or '\n')
            {
                start++;
            }
        }
    }

    internal static int ResolveSegmentConcurrency(int configuredFragments)
        => Math.Clamp(
            Math.Max(configuredFragments, DefaultSegmentConcurrency),
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);

    internal static int ResolveSegmentConcurrency(
        int configuredFragments,
        int maxConcurrentDownloads)
        => DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            ResolveSegmentConcurrency(configuredFragments),
            maxConcurrentDownloads);

    internal static (
        string OperationId,
        string SegmentDirectory,
        string TransportStreamPath,
        string MuxedOutputPath) CreateWorkingPaths(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        return (
            operationId,
            Path.Combine(outputDirectory, $"temp_segments_{operationId}"),
            Path.Combine(outputDirectory, $"temp_output_{operationId}.ts"),
            Path.Combine(outputDirectory, $"temp_output_{operationId}.mp4"));
    }

    internal static void EnsureSegmentsReadyForMerge(
        string segmentDirectory,
        int totalSegments,
        IReadOnlyList<int> failedIndices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSegments);
        ArgumentNullException.ThrowIfNull(failedIndices);

        var unavailableIndices = failedIndices
            .Where(index => index >= 0 && index < totalSegments)
            .ToHashSet();
        for (var index = 0; index < totalSegments; index++)
        {
            var partPath = Path.Combine(segmentDirectory, $"{index:D4}.ts");
            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                unavailableIndices.Add(index);
        }

        if (unavailableIndices.Count == 0)
            return;

        var indexText = string.Join(", ", unavailableIndices.Order());
        throw new IOException(
            $"M3U8 分片下载不完整，缺失或无效的分片索引: {indexText}。已停止合并，避免生成损坏文件。");
    }

    internal static bool IsSuccessfulFfmpegMerge(
        ProcessOutput processResult,
        string muxedOutputPath)
    {
        ArgumentNullException.ThrowIfNull(processResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(muxedOutputPath);

        return processResult.ExitCode == 0
               && File.Exists(muxedOutputPath)
               && new FileInfo(muxedOutputPath).Length > 0;
    }

    internal static bool PromoteFfmpegOutputIfSuccessful(
        ProcessOutput processResult,
        string muxedOutputPath,
        string finalOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalOutputPath);
        if (!IsSuccessfulFfmpegMerge(processResult, muxedOutputPath))
            return false;

        File.Move(muxedOutputPath, finalOutputPath, overwrite: false);
        return true;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
        }
    }

    internal static async Task<IReadOnlyList<int>> RetryFailedSegmentsAsync(
        IReadOnlyList<int> failedIndices,
        IReadOnlyList<string> segments,
        int maxParallelSegments,
        Func<int, string, Task<bool>> downloadSegmentAsync,
        Action<int>? onSegmentCompleted,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        if (failedIndices.Count == 0)
            return [];

        return await DownloadSegmentsWithWorkersAsync(
            segments,
            maxParallelSegments,
            async (index, segUrl) =>
            {
                logCallback?.Invoke($"[m3u8] 正在重试分片 {index}: {segUrl}");
                return await downloadSegmentAsync(index, segUrl);
            },
            onSegmentCompleted,
            ct,
            failedIndices);
    }

    internal static async Task<IReadOnlyList<int>> DownloadSegmentsWithWorkersAsync(
        IReadOnlyList<string> segments,
        int maxParallelSegments,
        Func<int, string, Task<bool>> downloadSegmentAsync,
        Action<int>? onSegmentCompleted,
        CancellationToken ct,
        IReadOnlyList<int>? segmentIndices = null)
    {
        var itemCount = segmentIndices?.Count ?? segments.Count;
        if (itemCount == 0)
            return [];

        var stillFailed = new List<int>();
        var nextItem = -1;
        var workerCount = Math.Min(Math.Max(1, maxParallelSegments), itemCount);
        var workers = new Task[workerCount];

        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = RunWorkerAsync();
        }

        await Task.WhenAll(workers);
        stillFailed.Sort();
        return stillFailed;

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var item = Interlocked.Increment(ref nextItem);
                if (item >= itemCount)
                    return;

                ct.ThrowIfCancellationRequested();
                var index = segmentIndices is null ? item : segmentIndices[item];
                var segUrl = segments[index];
                var success = await downloadSegmentAsync(index, segUrl);
                if (success)
                {
                    onSegmentCompleted?.Invoke(index);
                }
                else
                {
                    lock (stillFailed)
                    {
                        stillFailed.Add(index);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 带有重试、字节范围和 AES-128 解密能力的单分片下载。
    /// </summary>
    private static Task<bool> DownloadSegmentWithRetryAsync(
        HttpClient httpClient,
        M3u8SegmentRequest request,
        string url,
        int index,
        string tempDir,
        Action<int>? onBytesRead,
        Action<string>? logCallback,
        ConcurrentDictionary<string, byte[]> keyCache,
        CancellationToken ct)
    {
        var filePath = Path.Combine(tempDir, $"{index:D4}.ts");
        return DownloadRequestWithRetryAsync(
            httpClient,
            request with { Url = url },
            filePath,
            onBytesRead,
            logCallback,
            keyCache,
            ct);
    }

    private static async Task<bool> DownloadRequestWithRetryAsync(
        HttpClient httpClient,
        M3u8SegmentRequest request,
        string filePath,
        Action<int>? onBytesRead,
        Action<string>? logCallback,
        ConcurrentDictionary<string, byte[]> keyCache,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
                if (request.RangeLength is { } rangeLength && request.RangeOffset is { } rangeOffset)
                {
                    httpRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                        rangeOffset,
                        checked(rangeOffset + rangeLength - 1));
                }

                using var response = await httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                response.EnsureSuccessStatusCode();
                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);

                if (request.RangeLength is null && string.IsNullOrWhiteSpace(request.KeyUrl))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await using var output = new FileStream(
                        filePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        SegmentIoBufferSize,
                        useAsync: true);
                    var streamingBuffer = ArrayPool<byte>.Shared.Rent(SegmentIoBufferSize);
                    var streamedBytes = 0L;
                    try
                    {
                        int read;
                        while ((read = await responseStream.ReadAsync(
                                   streamingBuffer.AsMemory(0, SegmentIoBufferSize),
                                   ct)) > 0)
                        {
                            await output.WriteAsync(streamingBuffer.AsMemory(0, read), ct);
                            streamedBytes += read;
                            onBytesRead?.Invoke(read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(streamingBuffer);
                    }

                    if (streamedBytes <= 0)
                        throw new IOException("服务器返回了空的 M3U8 分片。");

                    return true;
                }

                using var memory = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(SegmentIoBufferSize);
                try
                {
                    int read;
                    while ((read = await responseStream.ReadAsync(
                               buffer.AsMemory(0, SegmentIoBufferSize),
                               ct)) > 0)
                    {
                        await memory.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                var bytes = memory.ToArray();
                onBytesRead?.Invoke(bytes.Length);

                if (request.RangeLength is { } expectedLength)
                {
                    if (bytes.Length < expectedLength)
                        throw new IOException($"服务器返回的字节范围长度不足（期望 {expectedLength}，实际 {bytes.Length}）。");

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var offset = checked((int)request.RangeOffset!.Value);
                        if (offset > bytes.Length - expectedLength)
                            throw new IOException("服务器忽略了 M3U8 字节范围请求。");
                        bytes = bytes.AsSpan(offset, checked((int)expectedLength)).ToArray();
                    }
                    else if (bytes.Length > expectedLength)
                    {
                        bytes = bytes[..checked((int)expectedLength)];
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.KeyUrl))
                {
                    var key = await GetKeyBytesAsync(
                        httpClient,
                        request.KeyUrl,
                        keyCache,
                        logCallback,
                        ct);
                    bytes = DecryptAes128(bytes, key, request.InitializationVector
                        ?? CreateInitializationVector(request.SequenceNumber));
                }

                if (bytes.Length == 0)
                    throw new IOException("服务器返回了空的 M3U8 分片。");

                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllBytesAsync(filePath, bytes, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteTemporaryFile(filePath);
                if (attempt == maxRetries || ct.IsCancellationRequested)
                {
                    logCallback?.Invoke($"[m3u8] 分片下载最终失败 (尝试了 {maxRetries} 次): {ex.Message}");
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }

        return false;
    }

    private static async Task<byte[]> GetKeyBytesAsync(
        HttpClient httpClient,
        string keyUrl,
        ConcurrentDictionary<string, byte[]> keyCache,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        if (keyCache.TryGetValue(keyUrl, out var cached))
            return cached;

        Exception? lastException = null;
        for (var attempt = 1; attempt <= ManifestMaxRetries; attempt++)
        {
            try
            {
                var bytes = await httpClient.GetByteArrayAsync(keyUrl, ct);
                if (bytes.Length != 16)
                    throw new CryptographicException($"AES-128 密钥长度无效（实际 {bytes.Length} 字节）。");
                keyCache[keyUrl] = bytes;
                return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt == ManifestMaxRetries)
                    break;
                logCallback?.Invoke($"[m3u8] 密钥请求失败，将在第 {attempt} 次重试: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            }
        }

        throw new IOException($"无法获取 M3U8 AES-128 密钥: {lastException?.Message}", lastException);
    }

    internal static byte[] DecryptAes128(byte[] encryptedBytes, byte[] key, byte[] initializationVector)
    {
        ArgumentNullException.ThrowIfNull(encryptedBytes);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(initializationVector);
        if (key.Length != 16 || initializationVector.Length != 16)
            throw new CryptographicException("AES-128 密钥或 IV 长度无效。");
        if (encryptedBytes.Length == 0 || encryptedBytes.Length % 16 != 0)
            throw new CryptographicException("AES-128 分片长度不是 16 字节的整数倍。");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = initializationVector;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
    }
}

internal sealed record M3u8MediaPlaylist(
    IReadOnlyList<M3u8SegmentRequest> Segments,
    M3u8SegmentRequest? InitializationSegment);

internal sealed record M3u8SegmentRequest(
    string Url,
    string? KeyUrl,
    byte[]? InitializationVector,
    long? RangeLength,
    long? RangeOffset,
    long SequenceNumber);

internal sealed record M3u8KeyState(
    string KeyUrl,
    byte[]? InitializationVector,
    long SequenceNumber);

internal readonly record struct M3u8ByteRange(long Length, long Offset);

internal sealed record M3u8Variant(string Url, long Bandwidth, int Height);

internal sealed record M3u8VariantBuilder(long Bandwidth, int Height);
