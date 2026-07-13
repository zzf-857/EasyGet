using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyGet.Services.Cookies;

public enum CookieSourceKind
{
    Anonymous,
    LegacyScoped,
    Browser,
    ManagedSession
}

public sealed record CookieHealthRecord(
    string PlatformId,
    CookieSourceKind Source,
    string SourceId,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    int ConsecutiveFailures,
    CookieFailureCategory LastFailureCategory);

public interface ICookieHealthStore
{
    IReadOnlyList<CookieHealthRecord> Snapshot();

    Task RecordSuccessAsync(
        string platformId,
        CookieSourceKind source,
        BrowserProfile? profile,
        CancellationToken cancellationToken);

    Task RecordFailureAsync(
        string platformId,
        CookieSourceKind source,
        BrowserProfile? profile,
        CookieFailureCategory category,
        CancellationToken cancellationToken);

    Task ClearPlatformAsync(
        string platformId,
        CancellationToken cancellationToken);
}

public sealed class CookieHealthStore : ICookieHealthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private CookieHealthRecord[] _records;

    public CookieHealthStore(string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        _filePath = Path.Combine(applicationDataRoot, "cookie-health.json");
        _records = LoadRecords(_filePath);
    }

    public IReadOnlyList<CookieHealthRecord> Snapshot()
        => Volatile.Read(ref _records).ToArray();

    public async Task RecordSuccessAsync(
        string platformId,
        CookieSourceKind source,
        BrowserProfile? profile,
        CancellationToken cancellationToken)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        var sourceId = GetSourceId(source, profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var records = _records.ToList();
            var index = FindRecordIndex(records, platformId, source, sourceId);
            var previous = index >= 0 ? records[index] : null;
            var updated = new CookieHealthRecord(
                platformId,
                source,
                sourceId,
                now,
                previous?.LastFailureUtc,
                0,
                CookieFailureCategory.None);

            if (index >= 0)
                records[index] = updated;
            else
                records.Add(updated);

            await PersistAsync(records, cancellationToken);
            Volatile.Write(ref _records, records.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordFailureAsync(
        string platformId,
        CookieSourceKind source,
        BrowserProfile? profile,
        CookieFailureCategory category,
        CancellationToken cancellationToken)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        var sourceId = GetSourceId(source, profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var records = _records.ToList();
            var index = FindRecordIndex(records, platformId, source, sourceId);
            var previous = index >= 0 ? records[index] : null;
            var previousFailures = Math.Max(0, previous?.ConsecutiveFailures ?? 0);
            var updated = new CookieHealthRecord(
                platformId,
                source,
                sourceId,
                previous?.LastSuccessUtc,
                now,
                previousFailures == int.MaxValue ? int.MaxValue : previousFailures + 1,
                category);

            if (index >= 0)
                records[index] = updated;
            else
                records.Add(updated);

            await PersistAsync(records, cancellationToken);
            Volatile.Write(ref _records, records.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPlatformAsync(
        string platformId,
        CancellationToken cancellationToken)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = _records
                .Where(record => !string.Equals(
                    record.PlatformId,
                    platformId,
                    StringComparison.Ordinal))
                .ToArray();
            await PersistAsync(records, cancellationToken);
            Volatile.Write(ref _records, records);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(
        IReadOnlyList<CookieHealthRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    records,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static CookieHealthRecord[] LoadRecords(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<CookieHealthRecord[]>(
                       File.ReadAllText(filePath),
                       JsonOptions)
                   ?? [];
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException)
        {
            return [];
        }
    }

    private static int FindRecordIndex(
        IReadOnlyList<CookieHealthRecord> records,
        string platformId,
        CookieSourceKind source,
        string sourceId)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (string.Equals(record.PlatformId, platformId, StringComparison.Ordinal)
                && record.Source == source
                && string.Equals(record.SourceId, sourceId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetSourceId(CookieSourceKind source, BrowserProfile? profile)
        => source == CookieSourceKind.Browser
            ? profile?.StableId
              ?? throw new ArgumentNullException(nameof(profile), "Browser health requires a profile.")
            : source.ToString();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
        }
    }
}
