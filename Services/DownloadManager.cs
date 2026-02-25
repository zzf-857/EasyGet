using System.Collections.ObjectModel;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 下载队列管理器 — 管理并发下载任务
/// </summary>
public class DownloadManager
{
    private readonly YtDlpService _ytDlpService;
    private readonly HistoryService _historyService;
    private readonly ConfigService _configService;
    private readonly SemaphoreSlim _semaphore;

    /// <summary>所有任务</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>日志回调</summary>
    public event Action<string>? LogReceived;

    /// <summary>任务完成回调（包含完成、失败、取消）</summary>
    public event Action<DownloadTask>? TaskFinished;

    public DownloadManager(YtDlpService ytDlpService, HistoryService historyService, ConfigService configService)
    {
        _ytDlpService = ytDlpService;
        _historyService = historyService;
        _configService = configService;
        _semaphore = new SemaphoreSlim(configService.Config.MaxConcurrentDownloads);
    }

    /// <summary>
    /// 更新并发限制
    /// </summary>
    public void UpdateConcurrencyLimit(int maxConcurrent)
    {
        // SemaphoreSlim 不支持动态修改；此处仅在初始化时设置
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
        }

        // 等待并发位
        _ = Task.Run(async () =>
        {
            await _semaphore.WaitAsync(task.Cts.Token);
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    task.Progress = p.Percent;
                    task.Speed = p.Speed;
                    task.Eta = p.Eta;
                    task.DownloadedSize = p.Downloaded;
                });

                await _ytDlpService.DownloadAsync(
                    task,
                    progress,
                    line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                    task.Cts.Token);

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
            await _semaphore.WaitAsync(task.Cts.Token);
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    task.Progress = p.Percent;
                    task.Speed = p.Speed;
                    task.Eta = p.Eta;
                    task.DownloadedSize = p.Downloaded;
                });

                await _ytDlpService.DownloadAsync(
                    task,
                    progress,
                    line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}"),
                    task.Cts.Token);

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
}
