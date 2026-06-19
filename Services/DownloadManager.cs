using System.Collections.ObjectModel;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 下载队列管理器 — 管理并发下载任务
/// </summary>
public class DownloadManager
{
    private readonly YtDlpService _ytDlpService;
    private readonly M3u8DownloadService _m3u8DownloadService;
    private readonly TelegramDownloadService _telegramDownloadService;
    private readonly HistoryService _historyService;
    private readonly ConfigService _configService;
    private readonly SemaphoreSlim _semaphore;
    private int _currentConcurrencyLimit;
    private readonly object _concurrencyLock = new();

    /// <summary>所有任务</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>日志回调</summary>
    public event Action<string>? LogReceived;

    /// <summary>任务完成回调（包含完成、失败、取消）</summary>
    public event Action<DownloadTask>? TaskFinished;

    public DownloadManager(
        YtDlpService ytDlpService,
        HistoryService historyService,
        ConfigService configService,
        M3u8DownloadService? m3u8DownloadService = null,
        TelegramDownloadService? telegramDownloadService = null)
    {
        _ytDlpService = ytDlpService;
        _m3u8DownloadService = m3u8DownloadService ?? new M3u8DownloadService(configService, new EnvironmentService());
        _telegramDownloadService = telegramDownloadService ?? new TelegramDownloadService(configService);
        _historyService = historyService;
        _configService = configService;
        _currentConcurrencyLimit = NormalizeConcurrencyLimit(configService.Config.MaxConcurrentDownloads);
        _semaphore = new SemaphoreSlim(_currentConcurrencyLimit, 100);
    }

    /// <summary>
    /// 更新并发限制
    /// </summary>
    public void UpdateConcurrencyLimit(int maxConcurrent)
    {
        maxConcurrent = NormalizeConcurrencyLimit(maxConcurrent);

        lock (_concurrencyLock)
        {
            int diff = maxConcurrent - _currentConcurrencyLimit;
            _currentConcurrencyLimit = maxConcurrent;
            
            if (diff > 0)
            {
                _semaphore.Release(diff);
            }
            else if (diff < 0)
            {
                Task.Run(async () =>
                {
                    for (int i = 0; i < -diff; i++)
                    {
                        await _semaphore.WaitAsync();
                    }
                });
            }
        }
    }

    internal static int NormalizeConcurrencyLimit(int maxConcurrent)
    {
        return Math.Clamp(
            maxConcurrent,
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);
    }

    /// <summary>
    /// 添加并开始下载任务
    /// </summary>
    public async Task EnqueueAsync(DownloadTask task)
    {
        task.Cts = new CancellationTokenSource();

        if (string.IsNullOrEmpty(task.OutputDirectory))
            task.OutputDirectory = _configService.Config.DefaultDownloadPath;

        Tasks.Add(task);

        // 先获取视频信息
        task.Status = DownloadStatus.Resolving;
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 正在解析: {task.Url}");

        var info = await _ytDlpService.GetVideoInfoAsync(task.Url, task.Cts.Token);
        if (info != null)
        {
            task.Title = info.Title;
            task.Platform = info.Platform;
            task.Duration = info.Duration;
            task.ThumbnailUrl = info.Thumbnail;
            task.FileSize = info.FileSize;
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 标题: {info.Title}");
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 平台: {info.Platform} | 时长: {task.DurationText}");

            // 按平台自动归类到子文件夹
            if (_configService.Config.AutoCategorizeByPlatform && !string.IsNullOrEmpty(info.Platform))
            {
                var folderName = MapPlatformToFolderName(info.Platform);
                task.OutputDirectory = System.IO.Path.Combine(task.OutputDirectory, folderName);
                System.IO.Directory.CreateDirectory(task.OutputDirectory);
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 自动归类到: {folderName}/");
            }
        }

        // 等待并发位
        _ = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync(task.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (task.Status != DownloadStatus.Paused)
                {
                    task.Status = DownloadStatus.Cancelled;
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已取消: {task.Title}");
                }
                TaskFinished?.Invoke(task);
                return;
            }

            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    ApplyProgress(task, p);
                });

                if (M3u8DownloadService.IsM3u8Url(task.Url))
                {
                    await _m3u8DownloadService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }
                else if (TelegramDownloadService.IsTelegramUrl(task.Url))
                {
                    await _telegramDownloadService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }
                else
                {
                    await _ytDlpService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }

                // 下载成功后保存历史
                if (task.Status == DownloadStatus.Completed)
                {
                    await _historyService.AddAsync(new DownloadHistory
                    {
                        Url = task.Url,
                        Title = task.Title,
                        Platform = task.Platform,
                        Format = task.Format,
                        Quality = task.Quality,
                        FileSize = task.FileSize,
                        FilePath = task.OutputFilePath,
                        ThumbnailUrl = task.ThumbnailUrl,
                        DownloadTime = DateTime.Now
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (task.Status != DownloadStatus.Paused)
                {
                    task.Status = DownloadStatus.Cancelled;
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已取消: {task.Title}");
                }
                else
                {
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已暂停: {task.Title}");
                }
            }
            catch (Exception ex)
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = ex.Message;
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 失败: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
                TaskFinished?.Invoke(task);
            }
        });
    }

    /// <summary>取消任务</summary>
    public void Cancel(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        task?.Cts?.Cancel();
    }

    /// <summary>暂停任务</summary>
    public void Pause(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null || task.Status != DownloadStatus.Downloading) return;

        task.Status = DownloadStatus.Paused;
        task.Cts?.Cancel();
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 暂停: {task.Title}");
    }

    /// <summary>恢复暂停的任务（yt-dlp 自动续传部分下载文件）</summary>
    public async Task ResumeAsync(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null || task.Status != DownloadStatus.Paused) return;

        task.Speed = 0;
        task.Eta = 0;
        task.ErrorMessage = "";
        task.Cts = new CancellationTokenSource();

        Tasks.Remove(task);

        // 重新入队，但跳过信息解析（已有元数据）
        task.Status = DownloadStatus.Waiting;
        Tasks.Add(task);

        _ = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync(task.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (task.Status != DownloadStatus.Paused)
                {
                    task.Status = DownloadStatus.Cancelled;
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已取消: {task.Title}");
                }

                TaskFinished?.Invoke(task);
                return;
            }

            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    ApplyProgress(task, p);
                });

                if (M3u8DownloadService.IsM3u8Url(task.Url))
                {
                    await _m3u8DownloadService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }
                else if (TelegramDownloadService.IsTelegramUrl(task.Url))
                {
                    await _telegramDownloadService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }
                else
                {
                    await _ytDlpService.DownloadAsync(
                        task,
                        progress,
                        line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                        task.Cts.Token);
                }

                if (task.Status == DownloadStatus.Completed)
                {
                    await _historyService.AddAsync(new DownloadHistory
                    {
                        Url = task.Url,
                        Title = task.Title,
                        Platform = task.Platform,
                        Format = task.Format,
                        Quality = task.Quality,
                        FileSize = task.FileSize,
                        FilePath = task.OutputFilePath,
                        ThumbnailUrl = task.ThumbnailUrl,
                        DownloadTime = DateTime.Now
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (task.Status != DownloadStatus.Paused)
                {
                    task.Status = DownloadStatus.Cancelled;
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已取消: {task.Title}");
                }
                else
                {
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已暂停: {task.Title}");
                }
            }
            catch (Exception ex)
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = ex.Message;
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 失败: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
                TaskFinished?.Invoke(task);
            }
        });
    }

    /// <summary>重试失败/已取消的任务</summary>
    public async Task RetryAsync(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null || (task.Status != DownloadStatus.Failed && task.Status != DownloadStatus.Cancelled))
            return;

        // 重置任务状态
        task.Status = DownloadStatus.Waiting;
        task.Progress = 0;
        task.Speed = 0;
        task.Eta = 0;
        task.DownloadedSize = 0;
        task.ErrorMessage = "";
        task.Cts = new CancellationTokenSource();

        // 从队列中移除再重新入队
        Tasks.Remove(task);
        await EnqueueAsync(task);
    }

    /// <summary>取消所有任务</summary>
    public void CancelAll()
    {
        foreach (var task in Tasks)
            task.Cts?.Cancel();
    }

    /// <summary>
    /// 将 yt-dlp 返回的平台标识符映射为中文友好且文件系统安全的文件夹名
    /// </summary>
    private static string MapPlatformToFolderName(string platform)
    {
        // 不区分大小写匹配常见平台
        return platform.ToLowerInvariant() switch
        {
            "youtube" => "YouTube",
            "bilibili" or "bilibilibangu" => "哔哩哔哩",
            "douyin" => "抖音",
            "tiktok" => "TikTok",
            "instagram" => "Instagram",
            "twitter" or "x" => "Twitter(X)",
            "weibo" => "微博",
            "xiaohongshu" => "小红书",
            "kuaishou" => "快手",
            "iqiyi" => "爱奇艺",
            "youku" => "优酷",
            "tencent" or "tencentvideo" or "qq" => "腾讯视频",
            "facebook" => "Facebook",
            "twitch" or "twitchvod" or "twitchstream" => "Twitch",
            "niconico" or "niconicouser" => "NicoNico",
            "vimeo" => "Vimeo",
            _ => SanitizeFolderName(platform) // 未知平台使用原始名并清理无效字符
        };
    }

    /// <summary>
    /// 清理文件夹名中的非法字符
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "其他" : sanitized;
    }

    private static void ApplyProgress(DownloadTask task, DownloadProgress progress)
    {
        void Apply()
        {
            task.Progress = Math.Clamp(NormalizeFiniteProgressValue(progress.Percent), 0, 100);
            task.Speed = Math.Max(0, NormalizeFiniteProgressValue(progress.Speed));
            task.Eta = Math.Max(0, NormalizeFiniteProgressValue(progress.Eta));
            task.DownloadedSize = Math.Max(0, progress.Downloaded);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private static double NormalizeFiniteProgressValue(double value)
        => double.IsFinite(value) ? value : 0;
}
