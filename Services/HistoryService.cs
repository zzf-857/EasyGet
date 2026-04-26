using System.IO;
using Microsoft.Data.Sqlite;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// SQLite 下载历史记录管理服务
/// </summary>
public class HistoryService : IDisposable
{
    private readonly SqliteConnection _connection;

    public HistoryService()
    {
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "history.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
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
    }

    /// <summary>
    /// 添加下载记录
    /// </summary>
    public async Task AddAsync(DownloadHistory history)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (url, title, platform, format, quality, file_size, file_path, download_time, thumbnail_url)
            VALUES ($url, $title, $platform, $format, $quality, $fileSize, $filePath, $downloadTime, $thumbnailUrl)
            """;
        cmd.Parameters.AddWithValue("$url", history.Url);
        cmd.Parameters.AddWithValue("$title", history.Title);
        cmd.Parameters.AddWithValue("$platform", history.Platform);
        cmd.Parameters.AddWithValue("$format", history.Format);
        cmd.Parameters.AddWithValue("$quality", history.Quality);
        cmd.Parameters.AddWithValue("$fileSize", history.FileSize);
        cmd.Parameters.AddWithValue("$filePath", history.FilePath);
        cmd.Parameters.AddWithValue("$downloadTime", history.DownloadTime.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$thumbnailUrl", history.ThumbnailUrl ?? "");
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
            cmd.CommandText = """
                SELECT * FROM download_history
                WHERE title LIKE $keyword OR url LIKE $keyword OR platform LIKE $keyword
                ORDER BY download_time DESC
                """;
            cmd.Parameters.AddWithValue("$keyword", $"%{searchKeyword}%");
        }
        else
        {
            cmd.CommandText = "SELECT * FROM download_history ORDER BY download_time DESC";
        }

        var results = new List<DownloadHistory>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var thumbnailUrl = "";
            try { thumbnailUrl = reader.GetString(reader.GetOrdinal("thumbnail_url")); } catch { }

            results.Add(new DownloadHistory
            {
                Id = reader.GetInt64(0),
                Url = reader.GetString(1),
                Title = reader.GetString(2),
                Platform = reader.GetString(3),
                Format = reader.GetString(4),
                Quality = reader.GetString(5),
                FileSize = reader.GetInt64(6),
                FilePath = reader.GetString(7),
                DownloadTime = DateTime.Parse(reader.GetString(8)),
                ThumbnailUrl = thumbnailUrl
            });
        }

        return results;
    }

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
