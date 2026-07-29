using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// Persists the recoverable download queue without serializing runtime credentials or cancellation state.
/// </summary>
public sealed class TaskQueuePersistenceService : IDisposable
{
    private const int CurrentDocumentVersion = 1;
    private const int MaxTaskCount = 10_000;
    private const long MaxStateFileSize = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _stateFilePath;
    private readonly TimeSpan _debounceDelay;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _debounceSource;
    private TaskQueueStateDocument? _pendingDocument;
    private long _nextSequence;
    private long _latestWrittenSequence;
    private int _disposed;

    public TaskQueuePersistenceService()
        : this(GetDefaultStateFilePath(), DefaultDebounceDelay)
    {
    }

    internal TaskQueuePersistenceService(string stateFilePath, TimeSpan? debounceDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _stateFilePath = Path.GetFullPath(stateFilePath);
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        if (_debounceDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
    }

    internal string StateFilePath => _stateFilePath;

    public void ScheduleSave(IEnumerable<DownloadTask> tasks)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var document = CreateDocument(tasks);
        CancellationTokenSource source;

        lock (_sync)
        {
            document.Sequence = ++_nextSequence;
            _pendingDocument = document;
            _debounceSource?.Cancel();
            _debounceSource?.Dispose();
            source = new CancellationTokenSource();
            _debounceSource = source;
        }

        _ = RunDebouncedSaveAsync(document, source);
    }

    public async Task FlushAsync(
        IEnumerable<DownloadTask> tasks,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var document = CreateDocument(tasks);

        lock (_sync)
        {
            document.Sequence = ++_nextSequence;
            _pendingDocument = document;
            _debounceSource?.Cancel();
            _debounceSource?.Dispose();
            _debounceSource = null;
        }

        await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TaskQueueStateDocument? document;

        lock (_sync)
        {
            _debounceSource?.Cancel();
            _debounceSource?.Dispose();
            _debounceSource = null;
            document = _pendingDocument;
        }

        if (document is not null)
            await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadTask>> RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!File.Exists(_stateFilePath))
            return [];

        try
        {
            var fileInfo = new FileInfo(_stateFilePath);
            if (fileInfo.Length is <= 0 or > MaxStateFileSize)
                throw new InvalidDataException("Queue state file size is invalid.");

            await using var stream = new FileStream(
                _stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<TaskQueueStateDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (document is null || document.Version != CurrentDocumentVersion || document.Tasks is null)
                throw new InvalidDataException("Queue state document is invalid or unsupported.");
            if (document.Tasks.Count > MaxTaskCount)
                throw new InvalidDataException("Queue state contains too many tasks.");

            var restored = new List<DownloadTask>(document.Tasks.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var persistedTask in document.Tasks)
            {
                var task = RestoreTask(persistedTask);
                if (task is not null && ids.Add(task.Id))
                    restored.Add(task);
            }

            return restored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            QuarantineCorruptStateFile();
            Debug.WriteLine($"[TaskQueuePersistence] Corrupt state quarantined: {ex.Message}");
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TaskQueuePersistence] State restore skipped: {ex.Message}");
            return [];
        }
    }

    private async Task RunDebouncedSaveAsync(
        TaskQueueStateDocument document,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_debounceDelay, source.Token).ConfigureAwait(false);
            await WriteDocumentAsync(document, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TaskQueuePersistence] Debounced save failed: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_debounceSource, source))
                {
                    _debounceSource = null;
                    source.Dispose();
                }
            }
        }
    }

    private async Task WriteDocumentAsync(
        TaskQueueStateDocument document,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            if (document.Sequence < Volatile.Read(ref _latestWrittenSequence))
                return;

            var directory = Path.GetDirectoryName(_stateFilePath)
                ?? throw new InvalidOperationException("Queue state path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_stateFilePath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
            temporaryPath = null;
            Volatile.Write(ref _latestWrittenSequence, document.Sequence);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            _writeGate.Release();
        }
    }

    private TaskQueueStateDocument CreateDocument(IEnumerable<DownloadTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new TaskQueueStateDocument
        {
            Version = CurrentDocumentVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Tasks = tasks
                .Where(task => task.Status != DownloadStatus.Completed)
                .Take(MaxTaskCount)
                .Select(PersistedDownloadTask.FromModel)
                .ToList()
        };
    }

    private static DownloadTask? RestoreTask(PersistedDownloadTask persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted.Url)
            || persisted.Status == DownloadStatus.Completed
            || !Enum.IsDefined(persisted.Status))
        {
            return null;
        }

        var recoveredOperationalState = persisted.Status is DownloadStatus.Resolving
            or DownloadStatus.Downloading
            or DownloadStatus.Merging;
        var restoredStatus = recoveredOperationalState
            ? DownloadStatus.Paused
            : persisted.Status;
        var id = string.IsNullOrWhiteSpace(persisted.Id)
            ? Guid.NewGuid().ToString("N")[..8]
            : Limit(persisted.Id, 128);

        return new DownloadTask
        {
            Id = id,
            Url = Limit(persisted.Url, 32_768),
            Title = Limit(persisted.Title, 4_096),
            Platform = Limit(persisted.Platform, 256),
            Duration = NormalizeFiniteNonNegative(persisted.Duration),
            FileSize = Math.Max(0, persisted.FileSize),
            ThumbnailUrl = Limit(persisted.ThumbnailUrl, 32_768),
            Format = Limit(persisted.Format, 64, "mp4"),
            Quality = Limit(persisted.Quality, 64, "best"),
            SourceFormatSelector = Limit(persisted.SourceFormatSelector, 256),
            Subtitle = Limit(persisted.Subtitle, 64, "none"),
            OutputDirectory = Limit(persisted.OutputDirectory, 32_768),
            BatchId = Limit(persisted.BatchId, 256),
            BatchName = Limit(persisted.BatchName, 4_096),
            BatchDirectory = Limit(persisted.BatchDirectory, 32_768),
            CollectionTitle = Limit(persisted.CollectionTitle, 4_096),
            CollectionItemIndex = Math.Max(0, persisted.CollectionItemIndex),
            CollectionItemCount = Math.Max(0, persisted.CollectionItemCount),
            OutputFilePath = Limit(persisted.OutputFilePath, 32_768),
            OutputFilePaths = (persisted.OutputFilePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(128)
                .Select(path => Limit(path, 32_768))
                .ToList(),
            Progress = Math.Clamp(NormalizeFiniteNonNegative(persisted.Progress), 0, 100),
            DownloadedSize = Math.Max(0, persisted.DownloadedSize),
            Status = restoredStatus == DownloadStatus.Scheduled
                     && persisted.ScheduledStartTimeUtc is null
                ? DownloadStatus.Paused
                : restoredStatus,
            ScheduledStartTimeUtc = restoredStatus == DownloadStatus.Scheduled
                ? persisted.ScheduledStartTimeUtc?.ToUniversalTime()
                : null,
            WasRestoredFromPreviousSession = recoveredOperationalState
                                                 || persisted.WasRestoredFromPreviousSession
        };
    }

    private void QuarantineCorruptStateFile()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return;

            var directory = Path.GetDirectoryName(_stateFilePath)!;
            var name = Path.GetFileNameWithoutExtension(_stateFilePath);
            var extension = Path.GetExtension(_stateFilePath);
            var quarantinePath = Path.Combine(
                directory,
                $"{name}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}{extension}");
            File.Move(_stateFilePath, quarantinePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TaskQueuePersistence] Could not quarantine state: {ex.Message}");
        }
    }

    private static string GetDefaultStateFilePath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyGet");
        return Path.Combine(appData, "queue-state.json");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static double NormalizeFiniteNonNegative(double value)
        => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static string Limit(string? value, int maxLength, string fallback = "")
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length == 0)
            return fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sync)
        {
            _debounceSource?.Cancel();
            _debounceSource?.Dispose();
            _debounceSource = null;
        }
    }
}

internal sealed class TaskQueueStateDocument
{
    public int Version { get; set; }
    public DateTimeOffset SavedAtUtc { get; set; }
    public List<PersistedDownloadTask> Tasks { get; set; } = [];

    [JsonIgnore]
    internal long Sequence { get; set; }
}

internal sealed class PersistedDownloadTask
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public double Duration { get; set; }
    public long FileSize { get; set; }
    public string ThumbnailUrl { get; set; } = "";
    public string Format { get; set; } = "mp4";
    public string Quality { get; set; } = "best";
    public string SourceFormatSelector { get; set; } = "";
    public string Subtitle { get; set; } = "none";
    public string OutputDirectory { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string BatchName { get; set; } = "";
    public string BatchDirectory { get; set; } = "";
    public string CollectionTitle { get; set; } = "";
    public int CollectionItemIndex { get; set; }
    public int CollectionItemCount { get; set; }
    public string OutputFilePath { get; set; } = "";
    public List<string> OutputFilePaths { get; set; } = [];
    public double Progress { get; set; }
    public long DownloadedSize { get; set; }
    public DownloadStatus Status { get; set; }
    public bool WasRestoredFromPreviousSession { get; set; }
    public DateTimeOffset? ScheduledStartTimeUtc { get; set; }

    internal static PersistedDownloadTask FromModel(DownloadTask task)
        => new()
        {
            Id = task.Id,
            Url = task.Url,
            Title = task.Title,
            Platform = task.Platform,
            Duration = task.Duration,
            FileSize = task.FileSize,
            ThumbnailUrl = task.ThumbnailUrl,
            Format = task.Format,
            Quality = task.Quality,
            SourceFormatSelector = task.SourceFormatSelector,
            Subtitle = task.Subtitle,
            OutputDirectory = task.OutputDirectory,
            BatchId = task.BatchId,
            BatchName = task.BatchName,
            BatchDirectory = task.BatchDirectory,
            CollectionTitle = task.CollectionTitle,
            CollectionItemIndex = task.CollectionItemIndex,
            CollectionItemCount = task.CollectionItemCount,
            OutputFilePath = task.OutputFilePath,
            OutputFilePaths = (task.OutputFilePaths ?? []).ToList(),
            Progress = double.IsFinite(task.Progress) ? Math.Clamp(task.Progress, 0, 100) : 0,
            DownloadedSize = Math.Max(0, task.DownloadedSize),
            Status = task.Status,
            WasRestoredFromPreviousSession = task.WasRestoredFromPreviousSession,
            ScheduledStartTimeUtc = task.Status == DownloadStatus.Scheduled
                ? task.ScheduledStartTimeUtc?.ToUniversalTime()
                : null
        };
}
