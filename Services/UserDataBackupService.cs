using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyGet.Models;
using Microsoft.Data.Sqlite;

namespace EasyGet.Services;

public sealed class UserDataBackupPaths
{
    public UserDataBackupPaths(
        string historyDatabasePath,
        string settingsFilePath,
        string safetyBackupDirectory,
        string workingDirectory)
    {
        HistoryDatabasePath = NormalizeFilePath(historyDatabasePath, nameof(historyDatabasePath));
        SettingsFilePath = NormalizeFilePath(settingsFilePath, nameof(settingsFilePath));
        SafetyBackupDirectory = NormalizeDirectoryPath(safetyBackupDirectory, nameof(safetyBackupDirectory));
        WorkingDirectory = NormalizeDirectoryPath(workingDirectory, nameof(workingDirectory));
    }

    public string HistoryDatabasePath { get; }
    public string SettingsFilePath { get; }
    public string SafetyBackupDirectory { get; }
    public string WorkingDirectory { get; }

    public static UserDataBackupPaths FromConfigDirectory(
        string configDirectory,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        var root = Path.GetFullPath(configDirectory);
        return new UserDataBackupPaths(
            Path.Combine(root, "history.db"),
            Path.Combine(root, "config.json"),
            Path.Combine(root, "backups"),
            workingDirectory ?? Path.Combine(Path.GetTempPath(), "EasyGet", "backup-work"));
    }

    private static string NormalizeFilePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            throw new ArgumentException("A file path is required.", parameterName);
        return fullPath;
    }

    private static string NormalizeDirectoryPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }
}

public sealed record UserDataBackupPreview(
    DateTimeOffset CreatedUtc,
    long HistoryDatabaseBytes,
    long HistoryRecordCount,
    IReadOnlyList<string> IncludedSettingNames,
    IReadOnlyList<string> ExplicitlyExcludedData);

public sealed record UserDataBackupValidationResult(
    bool IsValid,
    UserDataBackupPreview? Preview,
    IReadOnlyList<string> Errors);

public sealed record UserDataRestoreResult(
    UserDataBackupPreview RestoredBackup,
    string? SafetyBackupPath);

/// <summary>
/// Creates and restores a deliberately narrow user-data archive. Credentials and
/// browser/login state are excluded by construction rather than redacted later.
/// </summary>
public sealed class UserDataBackupService
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string HistoryEntryName = "data/history.db";
    public const string SettingsEntryName = "settings/settings.json";

    private const long MaxManifestBytes = 256 * 1024;
    private const long MaxSettingsBytes = 2 * 1024 * 1024;
    private const long MaxHistoryBytes = 8L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExcludedDataCategories =
    [
        "Cookie content and browser-derived credentials",
        "Managed login sessions and browser profiles",
        "Telegram API credentials, phone number, and session files",
        "Proxy addresses that may contain credentials"
    ];

    private static readonly HashSet<string> AllowedSettings = BuildAllowedSettings();

    private readonly UserDataBackupPaths _paths;
    private readonly Func<DateTimeOffset> _utcNow;

    public UserDataBackupService(
        UserDataBackupPaths paths,
        Func<DateTimeOffset>? utcNow = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<UserDataBackupPreview> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var destinationPath = Path.GetFullPath(backupPath);
        if (!File.Exists(_paths.HistoryDatabasePath))
            throw new FileNotFoundException("The history database does not exist.", _paths.HistoryDatabasePath);

        Directory.CreateDirectory(_paths.WorkingDirectory);
        var stagingDirectory = CreateWorkingDirectory("create");
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The backup path must include a directory.", nameof(backupPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryArchivePath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var historySnapshotPath = Path.Combine(stagingDirectory, "history.db");
            await CreateSqliteSnapshotAsync(
                _paths.HistoryDatabasePath,
                historySnapshotPath,
                cancellationToken);

            var safeSettings = await CreateSafeSettingsSnapshotAsync(cancellationToken);
            var createdUtc = _utcNow().ToUniversalTime();
            var historyDescriptor = await DescribeFileAsync(historySnapshotPath, cancellationToken);
            var settingsDescriptor = DescribeBytes(safeSettings);
            var manifest = new BackupManifest
            {
                FormatVersion = CurrentFormatVersion,
                Product = "EasyGet",
                CreatedUtc = createdUtc,
                History = historyDescriptor,
                Settings = settingsDescriptor,
                ExplicitlyExcludedData = ExcludedDataCategories
            };

            await CreateArchiveAsync(
                temporaryArchivePath,
                historySnapshotPath,
                safeSettings,
                manifest,
                cancellationToken);

            var validation = await ValidateBackupAsync(temporaryArchivePath, cancellationToken);
            if (!validation.IsValid || validation.Preview is null)
            {
                throw new InvalidDataException(
                    "The generated backup failed validation: " + string.Join("; ", validation.Errors));
            }

            File.Move(temporaryArchivePath, destinationPath, overwrite: true);
            return validation.Preview;
        }
        finally
        {
            TryDeleteFile(temporaryArchivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<UserDataBackupValidationResult> ValidateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullPath = Path.GetFullPath(backupPath);
        if (!File.Exists(fullPath))
        {
            return new UserDataBackupValidationResult(
                false,
                null,
                ["The backup file does not exist."]);
        }

        Directory.CreateDirectory(_paths.WorkingDirectory);
        var stagingDirectory = CreateWorkingDirectory("validate");
        try
        {
            return await ValidateCoreAsync(fullPath, stagingDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidDataException
                                   or JsonException
                                   or SqliteException
                                   or UnauthorizedAccessException
                                   or CryptographicException)
        {
            return new UserDataBackupValidationResult(false, null, [ex.Message]);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<UserDataBackupPreview> PreviewBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateBackupAsync(backupPath, cancellationToken);
        if (!validation.IsValid || validation.Preview is null)
        {
            throw new InvalidDataException(
                "The backup is not valid: " + string.Join("; ", validation.Errors));
        }

        return validation.Preview;
    }

    public async Task<UserDataRestoreResult> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        var preview = await PreviewBackupAsync(fullBackupPath, cancellationToken);
        Directory.CreateDirectory(_paths.WorkingDirectory);
        var stagingDirectory = CreateWorkingDirectory("restore");
        string? safetyBackupPath = null;

        try
        {
            var stagedHistoryPath = Path.Combine(stagingDirectory, "history.db");
            var safeSettings = await ExtractRestorePayloadAsync(
                fullBackupPath,
                stagedHistoryPath,
                cancellationToken);
            var mergedSettings = await MergeWithCurrentSettingsAsync(safeSettings, cancellationToken);

            if (File.Exists(_paths.HistoryDatabasePath))
            {
                Directory.CreateDirectory(_paths.SafetyBackupDirectory);
                safetyBackupPath = Path.Combine(
                    _paths.SafetyBackupDirectory,
                    $"EasyGet-before-restore-{_utcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
                await CreateBackupAsync(safetyBackupPath, cancellationToken);
            }

            await ReplaceUserDataAsync(stagedHistoryPath, mergedSettings, cancellationToken);
            return new UserDataRestoreResult(preview, safetyBackupPath);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task<UserDataBackupValidationResult> ValidateCoreAsync(
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        var entriesByName = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var allowedEntries = new HashSet<string>(
            [ManifestEntryName, HistoryEntryName, SettingsEntryName],
            StringComparer.Ordinal);

        foreach (var duplicate in entriesByName.Where(pair => pair.Value.Length != 1))
            errors.Add($"Archive entry '{duplicate.Key}' occurs more than once.");
        foreach (var unexpected in entriesByName.Keys.Where(name => !allowedEntries.Contains(name)))
            errors.Add($"Archive contains unexpected entry '{unexpected}'.");
        foreach (var required in allowedEntries.Where(name => !entriesByName.ContainsKey(name)))
            errors.Add($"Archive is missing required entry '{required}'.");
        if (errors.Count > 0)
            return new UserDataBackupValidationResult(false, null, errors);

        var manifestEntry = entriesByName[ManifestEntryName][0];
        var historyEntry = entriesByName[HistoryEntryName][0];
        var settingsEntry = entriesByName[SettingsEntryName][0];
        ValidateEntrySize(manifestEntry, MaxManifestBytes, errors);
        ValidateEntrySize(historyEntry, MaxHistoryBytes, errors);
        ValidateEntrySize(settingsEntry, MaxSettingsBytes, errors);
        if (errors.Count > 0)
            return new UserDataBackupValidationResult(false, null, errors);

        BackupManifest? manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                manifestStream,
                JsonOptions,
                cancellationToken);
        }

        if (manifest is null)
            errors.Add("The backup manifest is empty.");
        else
            ValidateManifest(manifest, historyEntry, settingsEntry, errors);
        if (errors.Count > 0 || manifest is null)
            return new UserDataBackupValidationResult(false, null, errors);

        var stagedHistoryPath = Path.Combine(stagingDirectory, "history.db");
        await ExtractEntryToFileAsync(historyEntry, stagedHistoryPath, cancellationToken);
        var historyHash = await ComputeFileHashAsync(stagedHistoryPath, cancellationToken);
        if (!FixedTimeHashEquals(historyHash, manifest.History.Sha256))
            errors.Add("The history database checksum does not match the manifest.");

        var safeSettings = await ReadEntryBytesAsync(settingsEntry, MaxSettingsBytes, cancellationToken);
        var settingsHash = ComputeHash(safeSettings);
        if (!FixedTimeHashEquals(settingsHash, manifest.Settings.Sha256))
            errors.Add("The settings checksum does not match the manifest.");

        var includedSettings = ValidateSafeSettings(safeSettings, errors);
        long historyRecordCount = 0;
        if (errors.Count == 0)
            historyRecordCount = await ValidateHistoryDatabaseAsync(stagedHistoryPath, cancellationToken);
        if (errors.Count > 0)
            return new UserDataBackupValidationResult(false, null, errors);

        var preview = new UserDataBackupPreview(
            manifest.CreatedUtc,
            manifest.History.Size,
            historyRecordCount,
            includedSettings,
            manifest.ExplicitlyExcludedData.ToArray());
        return new UserDataBackupValidationResult(true, preview, []);
    }

    private static void ValidateManifest(
        BackupManifest manifest,
        ZipArchiveEntry historyEntry,
        ZipArchiveEntry settingsEntry,
        ICollection<string> errors)
    {
        if (manifest.FormatVersion != CurrentFormatVersion)
            errors.Add($"Unsupported backup format version {manifest.FormatVersion}.");
        if (!string.Equals(manifest.Product, "EasyGet", StringComparison.Ordinal))
            errors.Add("The archive was not created for EasyGet.");
        if (manifest.CreatedUtc == default)
            errors.Add("The manifest creation time is missing.");
        ValidateDescriptor(manifest.History, historyEntry, "history database", errors);
        ValidateDescriptor(manifest.Settings, settingsEntry, "settings", errors);

        var exclusions = manifest.ExplicitlyExcludedData ?? [];
        foreach (var expected in ExcludedDataCategories)
        {
            if (!exclusions.Contains(expected, StringComparer.Ordinal))
                errors.Add($"The manifest does not declare exclusion of '{expected}'.");
        }
    }

    private static void ValidateDescriptor(
        BackupFileDescriptor? descriptor,
        ZipArchiveEntry entry,
        string displayName,
        ICollection<string> errors)
    {
        if (descriptor is null)
        {
            errors.Add($"The manifest {displayName} descriptor is missing.");
            return;
        }

        if (descriptor.Size != entry.Length)
            errors.Add($"The manifest {displayName} integrity/checksum validation failed: size does not match the archive entry.");
        if (!IsSha256(descriptor.Sha256))
            errors.Add($"The manifest {displayName} checksum is invalid.");
    }

    private static void ValidateEntrySize(
        ZipArchiveEntry entry,
        long maximum,
        ICollection<string> errors)
    {
        if (entry.Length <= 0)
            errors.Add($"Archive entry '{entry.FullName}' is empty.");
        else if (entry.Length > maximum)
            errors.Add($"Archive entry '{entry.FullName}' exceeds the supported size.");
    }

    private async Task<byte[]> CreateSafeSettingsSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.SettingsFilePath))
            return SerializeJsonObject(new JsonObject());

        var fileInfo = new FileInfo(_paths.SettingsFilePath);
        if (fileInfo.Length > MaxSettingsBytes)
            throw new InvalidDataException("The settings file exceeds the supported backup size.");

        await using var stream = new FileStream(
            _paths.SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var root = await JsonNode.ParseAsync(
            stream,
            documentOptions: default,
            cancellationToken: cancellationToken) as JsonObject
            ?? throw new InvalidDataException("The settings file must contain a JSON object.");
        return SerializeJsonObject(CreateSafeSettingsObject(root));
    }

    private static JsonObject CreateSafeSettingsObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var canonicalName in AllowedSettings.OrderBy(name => name, StringComparer.Ordinal))
        {
            var sourceProperty = source.FirstOrDefault(pair =>
                string.Equals(pair.Key, canonicalName, StringComparison.OrdinalIgnoreCase));
            if (sourceProperty.Key is not null)
                result[canonicalName] = sourceProperty.Value?.DeepClone();
        }

        return result;
    }

    private static IReadOnlyList<string> ValidateSafeSettings(
        byte[] settingsBytes,
        ICollection<string> errors)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(settingsBytes) as JsonObject;
        }
        catch (JsonException ex)
        {
            errors.Add($"The safe settings JSON is invalid: {ex.Message}");
            return [];
        }

        if (root is null)
        {
            errors.Add("The safe settings entry must contain a JSON object.");
            return [];
        }

        var names = new List<string>();
        foreach (var property in root)
        {
            if (!AllowedSettings.Contains(property.Key))
                errors.Add($"The safe settings entry contains unsupported property '{property.Key}'.");
            if (ContainsSensitiveKey(property.Key))
                errors.Add($"The safe settings entry contains sensitive property '{property.Key}'.");
            if (property.Value is JsonObject or JsonArray)
                errors.Add($"The safe setting '{property.Key}' must be a scalar value.");
            names.Add(property.Key);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private async Task<byte[]> ExtractRestorePayloadAsync(
        string archivePath,
        string stagedHistoryPath,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var historyEntry = archive.GetEntry(HistoryEntryName)
            ?? throw new InvalidDataException("The backup history entry is missing.");
        var settingsEntry = archive.GetEntry(SettingsEntryName)
            ?? throw new InvalidDataException("The backup settings entry is missing.");
        await ExtractEntryToFileAsync(historyEntry, stagedHistoryPath, cancellationToken);
        return await ReadEntryBytesAsync(settingsEntry, MaxSettingsBytes, cancellationToken);
    }

    private async Task<byte[]> MergeWithCurrentSettingsAsync(
        byte[] safeSettingsBytes,
        CancellationToken cancellationToken)
    {
        var safeSettings = JsonNode.Parse(safeSettingsBytes) as JsonObject
            ?? throw new InvalidDataException("The restored settings entry is invalid.");
        JsonObject currentSettings = new();
        if (File.Exists(_paths.SettingsFilePath))
        {
            try
            {
                await using var currentStream = new FileStream(
                    _paths.SettingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                currentSettings = await JsonNode.ParseAsync(
                    currentStream,
                    documentOptions: default,
                    cancellationToken: cancellationToken) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                currentSettings = new JsonObject();
            }
        }

        foreach (var property in safeSettings)
        {
            var existingKeys = currentSettings
                .Select(pair => pair.Key)
                .Where(key => string.Equals(key, property.Key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var key in existingKeys)
                currentSettings.Remove(key);
            currentSettings[property.Key] = property.Value?.DeepClone();
        }

        return SerializeJsonObject(currentSettings);
    }

    private async Task ReplaceUserDataAsync(
        string stagedHistoryPath,
        byte[] mergedSettings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var historyDirectory = Path.GetDirectoryName(_paths.HistoryDatabasePath)
            ?? throw new InvalidOperationException("The history database path has no parent directory.");
        var settingsDirectory = Path.GetDirectoryName(_paths.SettingsFilePath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(historyDirectory);
        Directory.CreateDirectory(settingsDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var historyReplacement = Path.Combine(historyDirectory, $".history.restore-{operationId}.tmp");
        var settingsReplacement = Path.Combine(settingsDirectory, $".settings.restore-{operationId}.tmp");
        File.Copy(stagedHistoryPath, historyReplacement, overwrite: false);
        await File.WriteAllBytesAsync(settingsReplacement, mergedSettings, cancellationToken);

        ReplacementState? historyState = null;
        ReplacementState? settingsState = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            historyState = ReplaceFile(historyReplacement, _paths.HistoryDatabasePath, operationId);
            settingsState = ReplaceFile(settingsReplacement, _paths.SettingsFilePath, operationId);
            DeleteRollback(historyState);
            DeleteRollback(settingsState);
        }
        catch (Exception replacementError)
        {
            var rollbackErrors = new List<Exception>();
            TryRollback(settingsState, rollbackErrors);
            TryRollback(historyState, rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "User data restore failed and one or more rollback operations also failed.",
                    [replacementError, .. rollbackErrors]);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(historyReplacement);
            TryDeleteFile(settingsReplacement);
        }
    }

    private static ReplacementState ReplaceFile(
        string replacementPath,
        string targetPath,
        string operationId)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(replacementPath, targetPath);
            return new ReplacementState(targetPath, TargetExisted: false, RollbackPath: null);
        }

        var rollbackPath = $"{targetPath}.restore-{operationId}.bak";
        File.Replace(replacementPath, targetPath, rollbackPath, ignoreMetadataErrors: true);
        return new ReplacementState(targetPath, TargetExisted: true, rollbackPath);
    }

    private static void TryRollback(
        ReplacementState? state,
        ICollection<Exception> rollbackErrors)
    {
        if (state is null)
            return;

        try
        {
            if (!state.TargetExisted)
            {
                TryDeleteFile(state.TargetPath);
                return;
            }

            if (state.RollbackPath is null || !File.Exists(state.RollbackPath))
                throw new IOException($"Rollback copy for '{state.TargetPath}' is missing.");
            if (File.Exists(state.TargetPath))
                File.Replace(state.RollbackPath, state.TargetPath, null, ignoreMetadataErrors: true);
            else
                File.Move(state.RollbackPath, state.TargetPath);
        }
        catch (Exception ex)
        {
            rollbackErrors.Add(ex);
        }
    }

    private static void DeleteRollback(ReplacementState? state)
    {
        if (state?.RollbackPath is not null)
            TryDeleteFile(state.RollbackPath);
    }

    private static async Task CreateSqliteSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
    }

    private static async Task<long> ValidateHistoryDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check";
            var result = (await check.ExecuteScalarAsync(cancellationToken))?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The history database integrity check failed: {result ?? "no result"}.");
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM download_history";
        var value = await count.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CreateArchiveAsync(
        string archivePath,
        string historySnapshotPath,
        byte[] safeSettings,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var historyEntry = archive.CreateEntry(HistoryEntryName, CompressionLevel.Optimal);
        await using (var entryStream = historyEntry.Open())
        await using (var historyStream = new FileStream(
                         historySnapshotPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await historyStream.CopyToAsync(entryStream, cancellationToken);
        }

        var settingsEntry = archive.CreateEntry(SettingsEntryName, CompressionLevel.Optimal);
        await using (var entryStream = settingsEntry.Open())
            await entryStream.WriteAsync(safeSettings, cancellationToken);

        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using (var entryStream = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
    }

    private static async Task ExtractEntryToFileAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await using var source = entry.Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumBytes)
            throw new InvalidDataException($"Archive entry '{entry.FullName}' is too large.");
        await using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        await source.CopyToAsync(destination, cancellationToken);
        if (destination.Length != entry.Length)
            throw new InvalidDataException($"Archive entry '{entry.FullName}' was truncated.");
        return destination.ToArray();
    }

    private static async Task<BackupFileDescriptor> DescribeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        return new BackupFileDescriptor
        {
            Size = info.Length,
            Sha256 = await ComputeFileHashAsync(path, cancellationToken)
        };
    }

    private static BackupFileDescriptor DescribeBytes(byte[] bytes)
        => new()
        {
            Size = bytes.LongLength,
            Sha256 = ComputeHash(bytes)
        };

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeHashEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static byte[] SerializeJsonObject(JsonObject value)
        => Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions) + Environment.NewLine);

    private string CreateWorkingDirectory(string operation)
    {
        var path = Path.Combine(_paths.WorkingDirectory, $"{operation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static HashSet<string> BuildAllowedSettings()
    {
        var propertyNames = new[]
        {
            nameof(AppConfig.ConfigVersion),
            nameof(AppConfig.DefaultDownloadPath),
            nameof(AppConfig.DefaultFormat),
            nameof(AppConfig.DefaultQuality),
            nameof(AppConfig.DefaultSubtitle),
            nameof(AppConfig.ConcurrentFragments),
            nameof(AppConfig.MaxConcurrentDownloads),
            nameof(AppConfig.UseAria2c),
            nameof(AppConfig.EnableDouyinSpecialEngine),
            nameof(AppConfig.DouyinMode),
            nameof(AppConfig.DouyinLimit),
            nameof(AppConfig.DouyinFilenameTemplate),
            nameof(AppConfig.DouyinFolderTemplate),
            nameof(AppConfig.DouyinAuthorDirectoryMode),
            nameof(AppConfig.DouyinGroupByMode),
            nameof(AppConfig.DouyinStartTime),
            nameof(AppConfig.DouyinEndTime),
            nameof(AppConfig.DouyinDownloadPinned),
            nameof(AppConfig.DouyinDownloadCover),
            nameof(AppConfig.DouyinDownloadAvatar),
            nameof(AppConfig.DouyinDownloadMusic),
            nameof(AppConfig.DouyinDownloadComments),
            nameof(AppConfig.DouyinCommentIncludeReplies),
            nameof(AppConfig.DouyinMaxComments),
            nameof(AppConfig.DouyinCommentPageSize),
            nameof(AppConfig.DouyinDownloadJson),
            nameof(AppConfig.DouyinEnableDatabase),
            nameof(AppConfig.DouyinIncrementalDownload),
            nameof(AppConfig.DouyinEnableBrowserFallback),
            nameof(AppConfig.DouyinLiveMaxDurationSeconds),
            nameof(AppConfig.DouyinLiveChunkSize),
            nameof(AppConfig.DouyinLiveIdleTimeoutSeconds),
            nameof(AppConfig.AutoCategorizeByPlatform),
            nameof(AppConfig.ClipboardMonitoringEnabled),
            nameof(AppConfig.PreventSleepDuringDownloads),
            nameof(AppConfig.MinimizeToTray),
            nameof(AppConfig.SystemNotificationsEnabled),
            nameof(AppConfig.AutomaticUpdateChecksEnabled),
            nameof(AppConfig.ThemeColor)
        };

        return propertyNames
            .Select(JsonNamingPolicy.CamelCase.ConvertName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ContainsSensitiveKey(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Contains("cookie", StringComparison.Ordinal)
               || normalized.Contains("session", StringComparison.Ordinal)
               || normalized.Contains("telegram", StringComparison.Ordinal)
               || normalized.StartsWith("tgapi", StringComparison.Ordinal)
               || normalized.StartsWith("tghash", StringComparison.Ordinal)
               || normalized.StartsWith("tgphone", StringComparison.Ordinal)
               || normalized.Contains("phonenumber", StringComparison.Ordinal)
               || normalized.Contains("proxyaddress", StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BackupManifest
    {
        public int FormatVersion { get; init; }
        public string Product { get; init; } = "";
        public DateTimeOffset CreatedUtc { get; init; }
        public BackupFileDescriptor History { get; init; } = new();
        public BackupFileDescriptor Settings { get; init; } = new();
        public IReadOnlyList<string> ExplicitlyExcludedData { get; init; } = [];
    }

    private sealed class BackupFileDescriptor
    {
        public long Size { get; init; }
        public string Sha256 { get; init; } = "";
    }

    private sealed record ReplacementState(
        string TargetPath,
        bool TargetExisted,
        string? RollbackPath);
}
