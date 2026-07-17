using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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

        // 去掉 Query 参数后判断后缀，或者直接模糊匹配
        var cleanUrl = url.Split('?')[0];
        return cleanUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || cleanUrl.EndsWith(".m3n8", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".m3n8", StringComparison.OrdinalIgnoreCase);
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
        var tempDir = Path.Combine(task.OutputDirectory, $"temp_segments_{timestamp}");
        var outputTsPath = Path.Combine(task.OutputDirectory, $"temp_output_{timestamp}.ts");
        
        // 最终输出视频文件
        var finalName = string.IsNullOrWhiteSpace(task.Title) ? $"m3u8_{timestamp}" : task.Title;
        if (!finalName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            finalName += ".mp4";
        }
        var finalMp4Path = Path.Combine(task.OutputDirectory, finalName);

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
                m3u8Content = await httpClient.GetStringAsync(task.Url, ct);
            }
            catch (Exception ex)
            {
                throw new Exception($"无法获取 m3u8 文件: {ex.Message}", ex);
            }

            var segments = ParseSegments(m3u8Content, task.Url);
            var totalSegments = segments.Count;
            logCallback?.Invoke($"[m3u8] 共解析出 {totalSegments} 个视频分片。");

            if (totalSegments == 0)
            {
                throw new Exception("未解析到任何分片，请检查 m3u8 链接是否正确。");
            }

            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // 2. 多线程下载分片
            logCallback?.Invoke("[m3u8] 开始多线程下载分片...");
            long totalDownloadedBytes = 0;
            long lastReportedBytes = 0;
            long completedSegments = 0;
            var failedIndices = new List<int>();

            var stopwatch = Stopwatch.StartNew();

            // 启动测速后台 Task
            var speedReportTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && task.Status == DownloadStatus.Downloading)
                {
                    await Task.Delay(1000, ct);
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

                    progress?.Report(new DownloadProgress
                    {
                        Percent = Math.Min(99.9, (double)currentCompleted / totalSegments * 100),
                        Speed = speed,
                        Eta = eta,
                        Downloaded = currentBytes,
                        Total = 0
                    });
                }
            }, ct);

            var maxParallelSegments = ResolveSegmentConcurrency(
                _configService.Config.ConcurrentFragments,
                _configService.Config.MaxConcurrentDownloads);
            logCallback?.Invoke($"[m3u8] 分片下载并发数: {maxParallelSegments}");
            failedIndices = (await DownloadSegmentsWithWorkersAsync(
                segments,
                maxParallelSegments,
                (index, segUrl) => DownloadSegmentWithRetryAsync(
                    httpClient,
                    segUrl,
                    index,
                    tempDir,
                    bytes => Interlocked.Add(ref totalDownloadedBytes, bytes),
                    logCallback,
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
            stopwatch.Stop();

            // 3. 重试下载失败的分片
            if (failedIndices.Count > 0)
            {
                logCallback?.Invoke($"[m3u8] 警告: 有 {failedIndices.Count} 个分片下载失败。开始重试...");
                var stillFailed = await RetryFailedSegmentsAsync(
                    failedIndices,
                    segments,
                    maxParallelSegments,
                    (index, segUrl) => DownloadSegmentWithRetryAsync(
                        httpClient,
                        segUrl,
                        index,
                        tempDir,
                        bytes => Interlocked.Add(ref totalDownloadedBytes, bytes),
                        logCallback,
                        ct),
                    _ => Interlocked.Increment(ref completedSegments),
                    logCallback,
                    ct);

                if (stillFailed.Count > 0)
                {
                    logCallback?.Invoke($"[m3u8] 严重警告: 依然有 {stillFailed.Count} 个分片下载失败，合成的视频可能存在损坏或卡顿。");
                }
            }

            logCallback?.Invoke("[m3u8] 分片下载完成，开始拼接...");

            // 4. 拼接分片为单个 ts 文件
            using (var outfile = new FileStream(outputTsPath, FileMode.Create, FileAccess.Write, FileShare.None, SegmentIoBufferSize, useAsync: true))
            {
                for (int i = 0; i < totalSegments; i++)
                {
                    var partPath = Path.Combine(tempDir, $"{i:D4}.ts");
                    if (File.Exists(partPath))
                    {
                        using var infile = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, SegmentIoBufferSize, useAsync: true);
                        await infile.CopyToAsync(outfile, SegmentIoBufferSize, ct);
                    }
                    else
                    {
                        logCallback?.Invoke($"[m3u8] 缺失分片: {partPath}");
                    }
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
                    
                    var arguments = $"-y -i \"{outputTsPath}\" -c copy \"{finalMp4Path}\"";
                    logCallback?.Invoke($"[m3u8] 运行 ffmpeg: {ffmpegPath} {arguments}");
                    
                    var processResult = await EnvironmentService.RunCommandAsync(ffmpegPath, arguments, TimeSpan.FromMinutes(10), ct);
                    convertSuccess = File.Exists(finalMp4Path);
                    if (convertSuccess)
                    {
                        logCallback?.Invoke($"[m3u8] 转换成功！已生成 MP4: {finalMp4Path}");
                        File.Delete(outputTsPath);
                    }
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[m3u8] 使用 ffmpeg 封装 MP4 失败: {ex.Message}");
                }
            }

            if (!convertSuccess)
            {
                logCallback?.Invoke("[m3u8] ffmpeg 封装失败或不可用，直接重命名 TS 文件为 MP4...");
                try
                {
                    if (File.Exists(finalMp4Path))
                    {
                        File.Delete(finalMp4Path);
                    }
                    File.Move(outputTsPath, finalMp4Path);
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
            task.Status = DownloadStatus.Cancelled;
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
        var segments = new List<string>();
        var baseUri = new Uri(m3u8Url);

        foreach (var line in EnumeratePlaylistLines(m3u8Content))
        {
            var trimmedLine = line.Span.Trim();
            if (trimmedLine.IsEmpty)
                continue;

            // 如果包含加密，抛出不支持的异常
            if (trimmedLine.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("该 m3u8 视频流被加密，当前暂不支持下载。");
            }

            if (!trimmedLine.StartsWith("#", StringComparison.Ordinal))
            {
                // 用 Uri 类自动解析相对路径拼接
                var segmentUri = new Uri(baseUri, trimmedLine.ToString());
                segments.Add(segmentUri.AbsoluteUri);
            }
        }

        return segments;
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
    /// 带有重试机制的单分片下载
    /// </summary>
    private static async Task<bool> DownloadSegmentWithRetryAsync(
        HttpClient httpClient,
        string url,
        int index,
        string tempDir,
        Action<int>? onBytesRead,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        var filePath = Path.Combine(tempDir, $"{index:D4}.ts");
        const int maxRetries = 5;
        var buffer = ArrayPool<byte>.Shared.Rent(SegmentIoBufferSize);

        try
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return false;

                try
                {
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, SegmentIoBufferSize, useAsync: true);

                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, SegmentIoBufferSize), ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        onBytesRead?.Invoke(read);
                    }

                    return true; // 下载成功
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries || ct.IsCancellationRequested)
                    {
                        logCallback?.Invoke($"[m3u8] 分片 {index} 下载最终失败 (尝试了 {maxRetries} 次): {ex.Message}");
                        return false;
                    }

                    // 线性避让退避
                    await Task.Delay(1000 * attempt, ct);
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
