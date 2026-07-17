using System.IO;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// SQLite 下载历史记录管理服务
/// </summary>
public class HistoryService : IDisposable
{
    private const string HistoryColumns = "id, url, title, platform, format, quality, file_size, file_path, attachment_file_paths, download_time, thumbnail_url, batch_id, batch_name, batch_directory, folder_id";

    private readonly SqliteConnection _connection;

    public HistoryService()
        : this(GetDefaultDatabasePath())
    {
    }

    internal HistoryService(string dbPath)
    {
        var dbDir = Path.GetDirectoryName(dbPath)
            ?? throw new ArgumentException("Database path must include a directory.", nameof(dbPath));
        Directory.CreateDirectory(dbDir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
    }

    private static string GetDefaultDatabasePath()
    {
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet");
        return Path.Combine(dbDir, "history.db");
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS download_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                platform TEXT NOT NULL DEFAULT '',
                format TEXT NOT NULL DEFAULT '',
                quality TEXT NOT NULL DEFAULT '',
                file_size INTEGER NOT NULL DEFAULT 0,
                file_path TEXT NOT NULL DEFAULT '',
                attachment_file_paths TEXT NOT NULL DEFAULT '[]',
                download_time TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
                thumbnail_url TEXT NOT NULL DEFAULT '',
                batch_id TEXT NOT NULL DEFAULT '',
                batch_name TEXT NOT NULL DEFAULT '',
                batch_directory TEXT NOT NULL DEFAULT '',
                folder_id INTEGER NOT NULL DEFAULT 0
            )
            """;
        cmd.ExecuteNonQuery();

        // 兼容旧版数据库：尝试添加 thumbnail_url 列（已存在则忽略）
        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE download_history ADD COLUMN thumbnail_url TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
        catch { /* 列已存在，忽略 */ }

        EnsureTextColumn("batch_id");
        EnsureTextColumn("batch_name");
        EnsureTextColumn("batch_directory");
        EnsureIntegerColumn("folder_id");

        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE download_history ADD COLUMN attachment_file_paths TEXT NOT NULL DEFAULT '[]'";
            alter.ExecuteNonQuery();
        }
        catch { /* 列已存在，忽略 */ }

        using var index = _connection.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_download_history_download_time_desc
            ON download_history (download_time DESC)
            """;
        index.ExecuteNonQuery();

        using var batchIndex = _connection.CreateCommand();
        batchIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_download_history_batch_id
            ON download_history (batch_id)
            """;
        batchIndex.ExecuteNonQuery();

        using var folderTable = _connection.CreateCommand();
        folderTable.CommandText = """
            CREATE TABLE IF NOT EXISTS history_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE,
                created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
            )
            """;
        folderTable.ExecuteNonQuery();

        using var folderNameIndex = _connection.CreateCommand();
        folderNameIndex.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_history_folders_name
            ON history_folders (name COLLATE NOCASE)
            """;
        folderNameIndex.ExecuteNonQuery();

        using var historyFolderIndex = _connection.CreateCommand();
        historyFolderIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_download_history_folder_id
            ON download_history (folder_id)
            """;
        historyFolderIndex.ExecuteNonQuery();
    }

    private void EnsureTextColumn(string columnName)
    {
        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE download_history ADD COLUMN {columnName} TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 旧库升级时列已存在即可继续；后续查询会再次验证数据库结构。
        }
    }

    private void EnsureIntegerColumn(string columnName)
    {
        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE download_history ADD COLUMN {columnName} INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 旧库升级时列已存在即可继续。
        }
    }

    /// <summary>
    /// 添加下载记录
    /// </summary>
    public async Task AddAsync(DownloadHistory history)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, attachment_file_paths, download_time, thumbnail_url, batch_id, batch_name, batch_directory, folder_id)
            VALUES ($url, $title, $platform, $format, $quality, $fileSize, $filePath, $attachmentFilePaths, $downloadTime, $thumbnailUrl, $batchId, $batchName, $batchDirectory, $folderId)
            """;
        cmd.Parameters.AddWithValue("$url", NormalizeHistoryText(history.Url));
        cmd.Parameters.AddWithValue("$title", NormalizeHistoryText(history.Title));
        cmd.Parameters.AddWithValue("$platform", NormalizeHistoryText(history.Platform));
        cmd.Parameters.AddWithValue("$format", NormalizeHistoryText(history.Format));
        cmd.Parameters.AddWithValue("$quality", NormalizeHistoryText(history.Quality));
        cmd.Parameters.AddWithValue("$fileSize", history.FileSize);
        cmd.Parameters.AddWithValue("$filePath", NormalizeHistoryText(history.FilePath));
        cmd.Parameters.AddWithValue("$attachmentFilePaths", SerializeAttachmentFilePaths(history.AttachmentFilePaths));
        cmd.Parameters.AddWithValue(
            "$downloadTime",
            history.DownloadTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$thumbnailUrl", NormalizeHistoryText(history.ThumbnailUrl));
        cmd.Parameters.AddWithValue("$batchId", NormalizeHistoryText(history.BatchId));
        cmd.Parameters.AddWithValue("$batchName", NormalizeHistoryText(history.BatchName));
        cmd.Parameters.AddWithValue("$batchDirectory", NormalizeHistoryText(history.BatchDirectory));
        cmd.Parameters.AddWithValue("$folderId", Math.Max(0, history.FolderId));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 获取所有下载记录（按时间倒序）
    /// </summary>
    public async Task<List<DownloadHistory>> GetAllAsync(string? searchKeyword = null)
    {
        using var cmd = _connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(searchKeyword))
        {
            cmd.CommandText = $"""
                SELECT {HistoryColumns} FROM download_history
                WHERE title LIKE $keyword
                   OR url LIKE $keyword
                   OR platform LIKE $keyword
                   OR batch_name LIKE $keyword
                ORDER BY download_time DESC
                """;
            cmd.Parameters.AddWithValue("$keyword", $"%{searchKeyword}%");
        }
        else
        {
            cmd.CommandText = $"SELECT {HistoryColumns} FROM download_history ORDER BY download_time DESC";
        }

        var results = new List<DownloadHistory>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var thumbnailUrl = "";
            try { thumbnailUrl = reader.GetString(reader.GetOrdinal("thumbnail_url")); } catch { }

            results.Add(new DownloadHistory
            {
                Id = ReadNonNegativeInt64(reader, "id"),
                Url = ReadString(reader, "url"),
                Title = ReadString(reader, "title"),
                Platform = ReadString(reader, "platform"),
                Format = ReadString(reader, "format"),
                Quality = ReadString(reader, "quality"),
                FileSize = ReadNonNegativeInt64(reader, "file_size"),
                FilePath = ReadString(reader, "file_path"),
                AttachmentFilePaths = DeserializeAttachmentFilePaths(ReadString(reader, "attachment_file_paths")),
                DownloadTime = ParseDownloadTime(ReadString(reader, "download_time")),
                ThumbnailUrl = thumbnailUrl,
                BatchId = ReadString(reader, "batch_id"),
                BatchName = ReadString(reader, "batch_name"),
                BatchDirectory = ReadString(reader, "batch_directory"),
                FolderId = ReadNonNegativeInt64(reader, "folder_id")
            });
        }

        return results;
    }

    private static DateTime ParseDownloadTime(string value)
    {
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static string NormalizeHistoryText(string? value)
        => value ?? string.Empty;

    private static string SerializeAttachmentFilePaths(IEnumerable<string>? attachmentFilePaths)
        => JsonSerializer.Serialize(NormalizeAttachmentFilePaths(attachmentFilePaths));

    private static List<string> DeserializeAttachmentFilePaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(value);
            return NormalizeAttachmentFilePaths(parsed);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> NormalizeAttachmentFilePaths(IEnumerable<string>? attachmentFilePaths)
    {
        var normalized = new List<string>();
        if (attachmentFilePaths is null)
            return normalized;

        foreach (var rawPath in attachmentFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var path = rawPath.Trim();
            if (!normalized.Contains(path, StringComparer.Ordinal))
                normalized.Add(path);
        }

        return normalized;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return string.Empty;

        return reader.GetValue(ordinal)?.ToString() ?? string.Empty;
    }

    private static string ReadString(SqliteDataReader reader, string columnName)
        => ReadString(reader, reader.GetOrdinal(columnName));

    private static long ReadNonNegativeInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        var value = reader.GetValue(ordinal);
        var parsed = value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            double doubleValue when doubleValue >= long.MinValue && doubleValue <= long.MaxValue => (long)doubleValue,
            string text when long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var textValue) => textValue,
            _ => 0
        };

        return Math.Max(0, parsed);
    }

    private static long ReadNonNegativeInt64(SqliteDataReader reader, string columnName)
        => ReadNonNegativeInt64(reader, reader.GetOrdinal(columnName));

    /// <summary>
    /// 清空所有历史记录
    /// </summary>
    public async Task ClearAllAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM download_history";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 删除单条记录
    /// </summary>
    public async Task DeleteAsync(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM download_history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetCountAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM download_history";
        var result = await cmd.ExecuteScalarAsync();
        return result is long count ? (int)Math.Min(int.MaxValue, Math.Max(0, count)) : 0;
    }

    public async Task<int> GetUnfiledCountAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM download_history WHERE folder_id = 0";
        var result = await cmd.ExecuteScalarAsync();
        return result is long count ? (int)Math.Min(int.MaxValue, Math.Max(0, count)) : 0;
    }

    public async Task<List<HistoryFolder>> GetFoldersAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.id, f.name, f.created_at, COUNT(h.id) AS item_count
            FROM history_folders f
            LEFT JOIN download_history h ON h.folder_id = f.id
            GROUP BY f.id, f.name, f.created_at
            ORDER BY f.created_at ASC, f.id ASC
            """;

        var folders = new List<HistoryFolder>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            folders.Add(new HistoryFolder
            {
                Id = ReadNonNegativeInt64(reader, "id"),
                Name = ReadString(reader, "name"),
                CreatedAt = ParseDownloadTime(ReadString(reader, "created_at")),
                ItemCount = (int)Math.Min(int.MaxValue, ReadNonNegativeInt64(reader, "item_count"))
            });
        }

        return folders;
    }

    public async Task<HistoryFolder> CreateFolderAsync(string name)
    {
        var normalizedName = NormalizeFolderName(name);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history_folders (name, created_at)
            VALUES ($name, $createdAt);
            SELECT last_insert_rowid();
            """;
        var createdAt = DateTime.Now;
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue(
            "$createdAt",
            createdAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var result = await cmd.ExecuteScalarAsync();
        var id = result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        return new HistoryFolder { Id = id, Name = normalizedName, CreatedAt = createdAt };
    }

    public async Task RenameFolderAsync(long folderId, string name)
    {
        if (folderId <= 0)
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE history_folders SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$name", NormalizeFolderName(name));
        cmd.Parameters.AddWithValue("$id", folderId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteFolderAsync(long folderId)
    {
        if (folderId <= 0)
            return;

        using var transaction = _connection.BeginTransaction();
        using (var unfile = _connection.CreateCommand())
        {
            unfile.Transaction = transaction;
            unfile.CommandText = "UPDATE download_history SET folder_id = 0 WHERE folder_id = $id";
            unfile.Parameters.AddWithValue("$id", folderId);
            await unfile.ExecuteNonQueryAsync();
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM history_folders WHERE id = $id";
            delete.Parameters.AddWithValue("$id", folderId);
            await delete.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    public async Task MoveToFolderAsync(IEnumerable<long> historyIds, long folderId)
    {
        var ids = NormalizeHistoryIds(historyIds);
        if (ids.Count == 0 || folderId < 0)
            return;

        if (folderId > 0 && !await FolderExistsAsync(folderId))
            throw new InvalidOperationException("目标整理文件夹不存在。");

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE download_history SET folder_id = $folderId WHERE id = $id";
        var folderParameter = cmd.Parameters.Add("$folderId", SqliteType.Integer);
        var idParameter = cmd.Parameters.Add("$id", SqliteType.Integer);
        folderParameter.Value = folderId;
        foreach (var id in ids)
        {
            idParameter.Value = id;
            await cmd.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task DeleteManyAsync(IEnumerable<long> historyIds)
    {
        var ids = NormalizeHistoryIds(historyIds);
        if (ids.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM download_history WHERE id = $id";
        var idParameter = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            idParameter.Value = id;
            await cmd.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private async Task<bool> FolderExistsAsync(long folderId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM history_folders WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", folderId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static List<long> NormalizeHistoryIds(IEnumerable<long> historyIds)
        => historyIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    private static string NormalizeFolderName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("文件夹名称不能为空。", nameof(value));
        if (name.Length > 40)
            throw new ArgumentException("文件夹名称不能超过 40 个字符。", nameof(value));
        return name;
    }

    /// <summary>删除同一批量/合集任务的全部历史记录</summary>
    public async Task DeleteBatchAsync(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM download_history WHERE batch_id = $batchId";
        cmd.Parameters.AddWithValue("$batchId", batchId.Trim());
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
