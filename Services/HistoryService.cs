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
    private const string HistoryColumns = "id, url, title, platform, format, quality, file_size, file_path, attachment_file_paths, download_time, thumbnail_url";

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
                thumbnail_url TEXT NOT NULL DEFAULT ''
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
    }

    /// <summary>
    /// 添加下载记录
    /// </summary>
    public async Task AddAsync(DownloadHistory history)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, attachment_file_paths, download_time, thumbnail_url)
            VALUES ($url, $title, $platform, $format, $quality, $fileSize, $filePath, $attachmentFilePaths, $downloadTime, $thumbnailUrl)
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
                WHERE title LIKE $keyword OR url LIKE $keyword OR platform LIKE $keyword
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
                ThumbnailUrl = thumbnailUrl
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

    public void Dispose()
    {
        _connection.Dispose();
    }
}
