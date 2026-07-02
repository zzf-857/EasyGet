namespace EasyGet.Tests;

internal static class TestTempPaths
{
    public static string CreateSqliteDatabasePath(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return Path.Combine(
            Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}.db");
    }

    public static void TryDeleteSqliteDatabase(string dbPath)
    {
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
            TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
