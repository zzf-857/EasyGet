using System.Collections.ObjectModel;
using System.Threading.Channels;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 下载队列管理器 — 管理并发下载任务
/// </summary>
public class DownloadManager : IDisposable
{
    private readonly IYtDlpDownloadService _ytDlpService;
    private readonly M3u8DownloadService _m3u8DownloadService;
    private readonly TelegramDownloadService _telegramDownloadService;
    private readonly HistoryService _historyService;
    private readonly ConfigService _configService;
    private readonly DynamicConcurrencyGate _downloadGate;
    private readonly SemaphoreSlim _historyWriteSemaphore = new(1, 1);
    private readonly Channel<DownloadAttempt> _metadataQueue;
    private readonly Task[] _metadataWorkers;
    private readonly object _attemptLock = new();
    private readonly Dictionary<DownloadTask, DownloadAttempt> _activeAttempts = [];
    private readonly object _idleLock = new();
    private int _activeTaskCount;
    private int _disposed;
    private TaskCompletionSource _idleSignal = CreateCompletedSignal();
    private const int MetadataWorkerCount = 4;
    private const int MetadataQueueCapacity = 128;

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
        : this(
            new YtDlpDownloadServiceAdapter(ytDlpService),
            historyService,
            configService,
            m3u8DownloadService,
            telegramDownloadService)
    {
    }

    internal DownloadManager(
        IYtDlpDownloadService ytDlpService,
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
        _downloadGate = new DynamicConcurrencyGate(
            NormalizeConcurrencyLimit(configService.Config.MaxConcurrentDownloads));
        _metadataQueue = Channel.CreateBounded<DownloadAttempt>(new BoundedChannelOptions(
            MetadataQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _metadataWorkers = Enumerable.Range(0, MetadataWorkerCount)
            .Select(_ => Task.Run(ProcessMetadataQueueAsync))
            .ToArray();
    }

    /// <summary>
    /// 更新并发限制
    /// </summary>
    public void UpdateConcurrencyLimit(int maxConcurrent)
    {
        _downloadGate.UpdateLimit(NormalizeConcurrencyLimit(maxConcurrent));
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
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var attempt = await BeginAttemptAsync(task);

        ObjectDisposedException.ThrowIf(
            !StartEnqueuedAttempt(task, attempt),
            this);
    }

    private bool StartEnqueuedAttempt(DownloadTask task, DownloadAttempt attempt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            AbandonAttempt(attempt);
            return false;
        }

        try
        {
            if (string.IsNullOrEmpty(task.OutputDirectory))
                task.OutputDirectory = _configService.Config.DefaultDownloadPath;

            Tasks.Add(task);

            // 先获取视频信息
            task.Status = DownloadStatus.Resolving;
            RegisterActiveTask();
            attempt.MarkRegistered();
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 正在解析: {task.Url}");

            _ = QueueMetadataAsync(attempt);
            return true;
        }
        catch
        {
            FinishAttempt(attempt, currentTask =>
            {
                currentTask.Status = DownloadStatus.Failed;
                currentTask.ErrorMessage = "启动下载任务失败，请重试";
            });
            throw;
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        Task idleTask;
        lock (_idleLock)
            idleTask = _idleSignal.Task;

        return cancellationToken.CanBeCanceled
            ? idleTask.WaitAsync(cancellationToken)
            : idleTask;
    }

    private async Task QueueMetadataAsync(DownloadAttempt attempt)
    {
        try
        {
            await _metadataQueue.Writer.WriteAsync(
                attempt,
                attempt.Token);
        }
        catch (OperationCanceledException)
        {
            FinishAttempt(attempt, currentTask => ApplyCancellationStatus(attempt, currentTask));
        }
        catch (Exception)
        {
            if (attempt.Token.IsCancellationRequested)
            {
                FinishAttempt(
                    attempt,
                    currentTask => ApplyCancellationStatus(attempt, currentTask));
            }
            else
            {
                FinishAttempt(attempt, currentTask =>
                {
                    currentTask.Status = DownloadStatus.Failed;
                    currentTask.ErrorMessage = "加入解析队列失败，请重试";
                });
            }
        }
    }

    private async Task ProcessMetadataQueueAsync()
    {
        await foreach (var attempt in _metadataQueue.Reader.ReadAllAsync())
        {
            try
            {
                await ResolveMetadataAsync(attempt);
            }
            catch (Exception)
            {
                if (attempt.Token.IsCancellationRequested)
                {
                    FinishAttempt(
                        attempt,
                        currentTask => ApplyCancellationStatus(attempt, currentTask));
                }
                else
                {
                    FinishAttempt(attempt, currentTask =>
                    {
                        currentTask.Status = DownloadStatus.Failed;
                        currentTask.ErrorMessage = "解析失败，请检查链接、网络或登录状态后重试";
                    });
                }
            }
        }
    }

    private async Task ResolveMetadataAsync(DownloadAttempt attempt)
    {
        var task = attempt.Task;
        try
        {
            var info = await _ytDlpService.GetVideoInfoAsync(
                task.Url,
                attempt.Token);
            if (info != null
                && !TryUpdateCurrentAttempt(
                    attempt,
                    currentTask => ApplyVideoInfoMetadata(currentTask, info)))
            {
                FinishAttempt(attempt);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            FinishAttempt(attempt, currentTask => ApplyCancellationStatus(attempt, currentTask));
            return;
        }
        catch (Exception)
        {
            if (attempt.Token.IsCancellationRequested)
            {
                FinishAttempt(
                    attempt,
                    currentTask => ApplyCancellationStatus(attempt, currentTask));
            }
            else
            {
                var isCurrent = FinishAttempt(attempt, currentTask =>
                {
                    currentTask.Status = DownloadStatus.Failed;
                    currentTask.ErrorMessage = "解析失败，请检查链接、网络或登录状态后重试";
                });
                if (isCurrent)
                    LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 解析失败，请检查链接、网络或登录状态后重试");
            }
            return;
        }
        if (!TryUpdateCurrentAttempt(attempt, currentTask => currentTask.Status = DownloadStatus.Waiting))
        {
            FinishAttempt(attempt);
            return;
        }

        _ = ProcessDownloadAsync(attempt);
    }

    private async Task ProcessDownloadAsync(DownloadAttempt attempt)
    {
        var task = attempt.Task;
        // 等待并发位
        try
        {
            await _downloadGate.WaitAsync(attempt.Token);
        }
        catch (OperationCanceledException)
        {
            var isCurrent = FinishAttempt(
                attempt,
                currentTask => ApplyCancellationStatus(attempt, currentTask));
            if (isCurrent && task.Status == DownloadStatus.Cancelled)
            {
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 已取消: {task.Title}");
            }
            return;
        }
        catch (Exception)
        {
            if (attempt.Token.IsCancellationRequested)
            {
                FinishAttempt(
                    attempt,
                    currentTask => ApplyCancellationStatus(attempt, currentTask));
            }
            else
            {
                FinishAttempt(attempt, currentTask =>
                {
                    currentTask.Status = DownloadStatus.Failed;
                    currentTask.ErrorMessage = "等待下载队列失败，请重试";
                });
            }
            return;
        }

        var cancelled = false;
        var failed = false;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                TryUpdateCurrentAttempt(
                    attempt,
                    currentTask => ApplyProgress(currentTask, p));
            });

            await DownloadWithMatchingServiceAsync(task, progress, attempt.Token);
            if (IsCurrentAttempt(attempt))
                await SaveHistoryIfCompletedAsync(task);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            if (attempt.Token.IsCancellationRequested)
                cancelled = true;
            else
                failed = true;
        }
        finally
        {
            _downloadGate.Release();
            var isCurrent = FinishAttempt(attempt, currentTask =>
            {
                if (cancelled || attempt.IsCancellationRequested)
                {
                    ApplyCancellationStatus(attempt, currentTask);
                }
                else if (failed)
                {
                    currentTask.Status = DownloadStatus.Failed;
                    currentTask.ErrorMessage = "下载失败，请检查网络、登录状态或输出目录后重试";
                }
            });

            if (isCurrent && cancelled)
            {
                var action = task.Status == DownloadStatus.Cancelled ? "已取消" : "已暂停";
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {action}: {task.Title}");
            }
            else if (isCurrent && failed)
            {
                LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 下载失败，请检查网络、登录状态或输出目录后重试");
            }
        }
    }

    private async Task<DownloadAttempt> BeginAttemptAsync(DownloadTask task)
    {
        while (true)
        {
            Task? previousCompletion;
            lock (_attemptLock)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);

                if (!_activeAttempts.TryGetValue(task, out var previousAttempt))
                    return CreateAttemptLocked(task);

                previousCompletion = previousAttempt.Completion;
            }

            await previousCompletion;
        }
    }

    private async Task<DownloadAttempt?> BeginConditionalAttemptAsync(
        DownloadTask task,
        Func<DownloadStatus, bool> canStart)
    {
        while (true)
        {
            Task? previousCompletion;
            DownloadAttempt? createdAttempt = null;
            lock (_attemptLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return null;

                if (!canStart(task.Status))
                    return null;

                if (!_activeAttempts.TryGetValue(task, out var previousAttempt))
                {
                    createdAttempt = CreateAttemptLocked(task);
                    previousCompletion = null;
                }
                else
                {
                    previousCompletion = previousAttempt.Completion;
                }
            }

            if (createdAttempt is not null)
            {
                try
                {
                    task.Status = DownloadStatus.Waiting;
                    if (createdAttempt.IsCancellationRequested)
                        ApplyCancellationStatus(createdAttempt, task);
                    return createdAttempt;
                }
                catch
                {
                    FinishAttempt(createdAttempt);
                    throw;
                }
            }

            await previousCompletion!;
        }
    }

    private DownloadAttempt CreateAttemptLocked(DownloadTask task)
    {
        var attempt = new DownloadAttempt(task);
        _activeAttempts.Add(task, attempt);
        task.Cts = attempt.Source;
        return attempt;
    }

    private bool IsCurrentAttempt(DownloadAttempt attempt)
    {
        lock (_attemptLock)
        {
            return _activeAttempts.TryGetValue(attempt.Task, out var activeAttempt)
                && ReferenceEquals(activeAttempt, attempt)
                && ReferenceEquals(attempt.Task.Cts, attempt.Source)
                && !attempt.IsFinishing
                && !attempt.IsCancellationRequested;
        }
    }

    private bool TryUpdateCurrentAttempt(
        DownloadAttempt attempt,
        Action<DownloadTask> update)
    {
        lock (attempt.UpdateSync)
        {
            lock (_attemptLock)
            {
                if (!_activeAttempts.TryGetValue(attempt.Task, out var activeAttempt)
                    || !ReferenceEquals(activeAttempt, attempt)
                    || !ReferenceEquals(attempt.Task.Cts, attempt.Source)
                    || attempt.IsFinishing
                    || attempt.IsCancellationRequested)
                {
                    return false;
                }
            }

            update(attempt.Task);
            if (attempt.IsCancellationRequested)
            {
                ApplyCancellationStatus(attempt, attempt.Task);
                return false;
            }

            lock (_attemptLock)
            {
                return _activeAttempts.TryGetValue(attempt.Task, out var activeAttempt)
                    && ReferenceEquals(activeAttempt, attempt)
                    && ReferenceEquals(attempt.Task.Cts, attempt.Source)
                    && !attempt.IsFinishing;
            }
        }
    }

    private bool FinishAttempt(
        DownloadAttempt attempt,
        Action<DownloadTask>? updateCurrentTask = null)
    {
        if (!attempt.TryFinish())
            return false;

        var isCurrent = false;
        try
        {
            lock (attempt.UpdateSync)
            {
                lock (_attemptLock)
                {
                    isCurrent = _activeAttempts.TryGetValue(attempt.Task, out var activeAttempt)
                        && ReferenceEquals(activeAttempt, attempt);
                    if (isCurrent && ReferenceEquals(attempt.Task.Cts, attempt.Source))
                        attempt.Task.Cts = null;
                }

                if (isCurrent && updateCurrentTask is not null)
                {
                    try
                    {
                        updateCurrentTask(attempt.Task);
                    }
                    catch (Exception)
                    {
                        // Property subscribers must not prevent attempt cleanup.
                    }
                }
            }
        }
        finally
        {
            if (attempt.IsCancellationRequested)
                CancelAttemptSource(attempt.Task, attempt, attempt.Source);

            var cancellationCompletion = attempt.SourceCancellationCompletion;
            if (cancellationCompletion is not null && !cancellationCompletion.IsCompleted)
            {
                _ = CompleteAttemptCleanupAfterCancellationAsync(
                    attempt,
                    isCurrent,
                    cancellationCompletion);
            }
            else
            {
                CompleteAttemptCleanup(attempt, isCurrent);
            }
        }

        return isCurrent;
    }

    private async Task CompleteAttemptCleanupAfterCancellationAsync(
        DownloadAttempt attempt,
        bool isCurrent,
        Task cancellationCompletion)
    {
        await cancellationCompletion.ConfigureAwait(false);
        CompleteAttemptCleanup(attempt, isCurrent);
    }

    private void CompleteAttemptCleanup(DownloadAttempt attempt, bool isCurrent)
    {
        try
        {
            attempt.Source.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // An external owner may have disposed the exposed source.
        }
        finally
        {
            var idleSignal = attempt.WasRegistered
                ? CompleteActiveTask()
                : null;
            attempt.MarkCleanupComplete();

            try
            {
                if (isCurrent)
                    NotifyTaskFinishedOnce(attempt);
            }
            finally
            {
                lock (_attemptLock)
                {
                    if (_activeAttempts.TryGetValue(attempt.Task, out var activeAttempt)
                        && ReferenceEquals(activeAttempt, attempt))
                    {
                        _activeAttempts.Remove(attempt.Task);
                    }
                }

                attempt.SignalCompletion();
                idleSignal?.TrySetResult();
            }
        }
    }

    private static bool IsFinishedStatus(DownloadStatus status)
        => status is DownloadStatus.Completed
            or DownloadStatus.Failed
            or DownloadStatus.Cancelled;

    private void RegisterActiveTask()
    {
        lock (_idleLock)
        {
            if (_activeTaskCount++ == 0)
            {
                _idleSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private TaskCompletionSource? CompleteActiveTask()
    {
        TaskCompletionSource? signal = null;
        lock (_idleLock)
        {
            if (_activeTaskCount <= 0)
                return null;
            if (--_activeTaskCount == 0)
                signal = _idleSignal;
        }

        return signal;
    }

    private void NotifyTaskFinished(DownloadTask task)
    {
        try
        {
            TaskFinished?.Invoke(task);
        }
        catch (Exception)
        {
            // UI subscribers must not terminate metadata/download workers.
        }
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    /// <summary>取消任务</summary>
    public void Cancel(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
            return;

        DownloadAttempt? attempt = null;
        CancellationTokenSource? source;
        bool shouldMarkCancelled;
        var notifyFinishingCancellation = false;
        lock (_attemptLock)
        {
            if (_activeAttempts.TryGetValue(task, out var activeAttempt))
            {
                if (activeAttempt.IsFinishing
                    && !activeAttempt.WasPauseRequested
                    && task.Status != DownloadStatus.Paused)
                {
                    return;
                }

                attempt = activeAttempt;
                notifyFinishingCancellation = attempt.IsFinishing;
                attempt.RequestCancel();
            }

            source = task.Cts;
            shouldMarkCancelled = notifyFinishingCancellation
                || task.Status is DownloadStatus.Waiting
                or DownloadStatus.Resolving
                or DownloadStatus.Paused;
        }

        try
        {
            if (shouldMarkCancelled)
                task.Status = DownloadStatus.Cancelled;
        }
        finally
        {
            CancelAttemptSource(task, attempt, source);
            if (notifyFinishingCancellation
                && attempt is not null
                && attempt.IsCleanupComplete)
            {
                NotifyTaskFinishedOnce(attempt);
            }
        }
    }

    private void NotifyTaskFinishedOnce(DownloadAttempt attempt)
    {
        if (!attempt.WasRegistered
            || !IsFinishedStatus(attempt.Task.Status)
            || !attempt.TryClaimFinishedNotification())
        {
            return;
        }

        NotifyTaskFinished(attempt.Task);
    }

    /// <summary>暂停任务</summary>
    public void Pause(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
            return;

        DownloadAttempt? attempt = null;
        CancellationTokenSource? source;
        lock (_attemptLock)
        {
            if (task.Status != DownloadStatus.Downloading)
                return;

            if (_activeAttempts.TryGetValue(task, out var activeAttempt))
            {
                if (activeAttempt.IsFinishing)
                    return;

                attempt = activeAttempt;
                attempt.RequestPause();
            }
            source = task.Cts;
        }

        try
        {
            task.Status = DownloadStatus.Paused;
        }
        finally
        {
            CancelAttemptSource(task, attempt, source);
        }
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 暂停: {task.Title}");
    }

    /// <summary>恢复暂停的任务（yt-dlp 自动续传部分下载文件）</summary>
    public async Task ResumeAsync(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
            return;

        var attempt = await BeginConditionalAttemptAsync(
            task,
            static status => status == DownloadStatus.Paused);
        if (attempt is null)
            return;

        if (Volatile.Read(ref _disposed) != 0)
        {
            AbandonAttempt(attempt);
            return;
        }

        var removedFromQueue = false;
        try
        {
            task.Speed = 0;
            task.Eta = 0;
            task.ErrorMessage = "";

            removedFromQueue = Tasks.Remove(task);
            if (Volatile.Read(ref _disposed) != 0)
            {
                AbandonAttempt(attempt);
                RestoreTaskIfMissing(task);
                return;
            }

            // 重新入队，但跳过信息解析（已有元数据）
            Tasks.Add(task);

            RegisterActiveTask();
            attempt.MarkRegistered();
            _ = ProcessDownloadAsync(attempt);
        }
        catch
        {
            FinishAttempt(attempt, currentTask =>
            {
                currentTask.Status = DownloadStatus.Failed;
                currentTask.ErrorMessage = "恢复下载任务失败，请重试";
            });
            if (removedFromQueue)
                RestoreTaskIfMissing(task);
            throw;
        }
    }

    /// <summary>重试失败/已取消的任务</summary>
    public async Task RetryAsync(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
            return;

        var attempt = await BeginConditionalAttemptAsync(
            task,
            static status => status is DownloadStatus.Failed or DownloadStatus.Cancelled);
        if (attempt is null)
            return;

        var removedFromQueue = false;
        try
        {
            // 重置任务状态
            task.Progress = 0;
            task.Speed = 0;
            task.Eta = 0;
            task.DownloadedSize = 0;
            task.ErrorMessage = "";
            ClearDouyinTaskAttemptState(task);
            // 从队列中移除再重新入队
            removedFromQueue = Tasks.Remove(task);
            if (!StartEnqueuedAttempt(task, attempt) && removedFromQueue)
                RestoreTaskIfMissing(task);
        }
        catch
        {
            FinishAttempt(attempt, currentTask =>
            {
                currentTask.Status = DownloadStatus.Failed;
                currentTask.ErrorMessage = "重试下载任务失败，请重试";
            });
            if (removedFromQueue)
                RestoreTaskIfMissing(task);
            throw;
        }
    }

    /// <summary>取消所有任务</summary>
    public void CancelAll()
    {
        foreach (var task in Tasks.ToArray())
        {
            try
            {
                Cancel(task.Id);
            }
            catch (Exception)
            {
                // A task subscriber must not prevent cancellation of later tasks.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelAll();

        DownloadAttempt[] attemptsToCancel;
        lock (_attemptLock)
        {
            attemptsToCancel = _activeAttempts.Values
                .Where(attempt => !attempt.IsFinishing
                    || attempt.WasPauseRequested
                    || attempt.Task.Status == DownloadStatus.Paused)
                .ToArray();
            foreach (var attempt in attemptsToCancel)
                attempt.RequestCancel();
        }

        foreach (var attempt in attemptsToCancel)
        {
            if (attempt.IsFinishing)
            {
                try
                {
                    attempt.Task.Status = DownloadStatus.Cancelled;
                }
                catch (Exception)
                {
                    // Property subscribers must not interrupt disposal.
                }
            }

            CancelAttemptSource(attempt.Task, attempt, attempt.Source);
            if (attempt.IsFinishing && attempt.IsCleanupComplete)
            {
                try
                {
                    NotifyTaskFinishedOnce(attempt);
                }
                catch (Exception)
                {
                    // Event subscribers must not interrupt disposal.
                }
            }
        }

        _metadataQueue.Writer.TryComplete();
    }

    private void CancelAttemptSource(
        DownloadTask task,
        DownloadAttempt? attempt,
        CancellationTokenSource? source)
    {
        if (source is null)
            return;

        if (attempt is not null)
        {
            if (!attempt.TryBeginSourceCancellation(out var completion))
                return;

            try
            {
                var cancellationTask = source.CancelAsync();
                _ = cancellationTask.ContinueWith(
                    static (completedTask, state) =>
                    {
                        _ = completedTask.Exception;
                        ((TaskCompletionSource)state!).TrySetResult();
                    },
                    completion,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (ObjectDisposedException)
            {
                ClearTaskSourceIfCurrent(task, source);
                completion.TrySetResult();
            }

            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            ClearTaskSourceIfCurrent(task, source);
        }
        catch (AggregateException)
        {
            // Cancellation callbacks have all run; task cleanup must continue.
        }
    }

    private void ClearTaskSourceIfCurrent(
        DownloadTask task,
        CancellationTokenSource source)
    {
        lock (_attemptLock)
        {
            if (ReferenceEquals(task.Cts, source))
            {
                task.Cts = null;
            }
        }
    }

    private void AbandonAttempt(DownloadAttempt attempt)
    {
        attempt.RequestCancel();
        CancelAttemptSource(attempt.Task, attempt, attempt.Source);
        FinishAttempt(
            attempt,
            currentTask => ApplyCancellationStatus(attempt, currentTask));
    }

    private void RestoreTaskIfMissing(DownloadTask task)
    {
        if (!Tasks.Contains(task))
            Tasks.Add(task);
    }

    private static void ApplyCancellationStatus(
        DownloadAttempt attempt,
        DownloadTask task)
    {
        task.Status = attempt.WasPauseRequested
            ? DownloadStatus.Paused
            : DownloadStatus.Cancelled;
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

    private async Task DownloadWithMatchingServiceAsync(
        DownloadTask task,
        IProgress<DownloadProgress> progress,
        CancellationToken token)
    {
        Action<string> log = line => LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}");

        if (M3u8DownloadService.IsM3u8Url(task.Url))
        {
            await _m3u8DownloadService.DownloadAsync(task, progress, log, token);
            return;
        }

        if (TelegramDownloadService.IsTelegramUrl(task.Url))
        {
            await _telegramDownloadService.DownloadAsync(task, progress, log, token);
            return;
        }

        await _ytDlpService.DownloadAsync(task, progress, log, token);
    }

    private async Task SaveHistoryIfCompletedAsync(DownloadTask task)
    {
        if (task.Status != DownloadStatus.Completed)
            return;

        await _historyWriteSemaphore.WaitAsync();
        try
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
                BatchId = task.BatchId,
                BatchName = task.BatchName,
                BatchDirectory = task.BatchDirectory,
                AttachmentFilePaths = GetAttachmentFilePathsForHistory(task),
                ThumbnailUrl = task.ThumbnailUrl,
                DownloadTime = DateTime.Now
            });
        }
        finally
        {
            _historyWriteSemaphore.Release();
        }
    }

    private static List<string> GetAttachmentFilePathsForHistory(DownloadTask task)
    {
        var attachmentFilePaths = new List<string>();
        foreach (var rawPath in task.OutputFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var path = rawPath.Trim();
            if (AreEquivalentPaths(path, task.OutputFilePath)
                || !IsSafeOutputFilePath(task.OutputDirectory, path)
                || attachmentFilePaths.Any(existingPath => AreEquivalentPaths(existingPath, path)))
            {
                continue;
            }

            attachmentFilePaths.Add(path);
        }

        return attachmentFilePaths;
    }

    private static bool IsSafeOutputFilePath(string? outputDirectory, string outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(outputFilePath))
            return false;

        try
        {
            var fullOutputDirectory = System.IO.Path.GetFullPath(outputDirectory);
            var fullOutputFilePath = System.IO.Path.GetFullPath(outputFilePath);
            var directoryWithSeparator = fullOutputDirectory.EndsWith(System.IO.Path.DirectorySeparatorChar)
                || fullOutputDirectory.EndsWith(System.IO.Path.AltDirectorySeparatorChar)
                    ? fullOutputDirectory
                    : fullOutputDirectory + System.IO.Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return fullOutputFilePath.StartsWith(directoryWithSeparator, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return false;
        }
    }

    private static bool AreEquivalentPaths(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private void ApplyVideoInfoMetadata(DownloadTask task, VideoInfo info)
    {
        task.Title = string.IsNullOrWhiteSpace(task.CollectionTitle)
            ? info.Title
            : CollectionNamingService.BuildItemTitle(
                info.Title,
                task.CollectionTitle,
                task.CollectionItemIndex,
                task.CollectionItemCount);
        task.Platform = info.Platform;
        task.Duration = info.Duration;
        task.ThumbnailUrl = info.Thumbnail;
        task.FileSize = info.FileSize;
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 标题: {task.Title}");
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 平台: {info.Platform} | 时长: {task.DurationText}");
        ApplyAutoCategorization(task, info.Platform);
    }

    private void ApplyAutoCategorization(DownloadTask task, string platform)
    {
        if (!_configService.Config.AutoCategorizeByPlatform
            || string.IsNullOrEmpty(platform)
            || !string.IsNullOrWhiteSpace(task.CollectionTitle))
        {
            return;
        }

        var folderName = MapPlatformToFolderName(platform);
        task.OutputDirectory = System.IO.Path.Combine(task.OutputDirectory, folderName);
        System.IO.Directory.CreateDirectory(task.OutputDirectory);
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] 自动归类到: {folderName}/");
    }

    private static void ClearDouyinTaskAttemptState(DownloadTask task)
    {
        task.DouyinSuccessCount = 0;
        task.DouyinFailedCount = 0;
        task.DouyinSkippedCount = 0;
        task.DouyinTaskEventLog = "";
    }

    private static string SanitizeTitleToken(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Item" : sanitized;
    }

    private sealed class DownloadAttempt
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _sourceCancellationCompletion;
        private int _cancellationKind;
        private int _cleanupComplete;
        private int _finished;
        private int _finishedNotificationClaimed;
        private int _registered;

        public DownloadAttempt(DownloadTask task)
        {
            Task = task;
            Source = new CancellationTokenSource();
            Token = Source.Token;
        }

        public DownloadTask Task { get; }
        public CancellationTokenSource Source { get; }
        public CancellationToken Token { get; }
        public System.Threading.Tasks.Task Completion => _completion.Task;
        public System.Threading.Tasks.Task? SourceCancellationCompletion
            => Volatile.Read(ref _sourceCancellationCompletion)?.Task;
        public object UpdateSync { get; } = new();
        public bool IsCancellationRequested => Volatile.Read(ref _cancellationKind) != 0;
        public bool IsCleanupComplete => Volatile.Read(ref _cleanupComplete) != 0;
        public bool WasPauseRequested => Volatile.Read(ref _cancellationKind) == 1;
        public bool IsFinishing => Volatile.Read(ref _finished) != 0;
        public bool WasRegistered => Volatile.Read(ref _registered) != 0;

        public void RequestPause()
            => Interlocked.CompareExchange(ref _cancellationKind, 1, 0);

        public void RequestCancel()
            => Interlocked.Exchange(ref _cancellationKind, 2);

        public bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;

        public bool TryClaimFinishedNotification()
            => Interlocked.Exchange(ref _finishedNotificationClaimed, 1) == 0;

        public bool TryBeginSourceCancellation(out TaskCompletionSource completion)
        {
            var existing = Volatile.Read(ref _sourceCancellationCompletion);
            if (existing is not null)
            {
                completion = existing;
                return false;
            }

            var created = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            existing = Interlocked.CompareExchange(
                ref _sourceCancellationCompletion,
                created,
                null);
            completion = existing ?? created;
            return existing is null;
        }

        public void MarkCleanupComplete() => Volatile.Write(ref _cleanupComplete, 1);

        public void MarkRegistered() => Volatile.Write(ref _registered, 1);

        public void SignalCompletion() => _completion.TrySetResult();
    }
}

internal interface IYtDlpDownloadService
{
    Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default);

    Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default);
}

internal sealed class YtDlpDownloadServiceAdapter(YtDlpService ytDlpService) : IYtDlpDownloadService
{
    public Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        => ytDlpService.GetVideoInfoAsync(url, cancellationToken);

    public Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
        => ytDlpService.DownloadAsync(task, progress, logCallback, cancellationToken);
}
