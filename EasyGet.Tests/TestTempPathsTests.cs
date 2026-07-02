using Xunit;

namespace EasyGet.Tests;

public class TestTempPathsTests
{
    [Fact]
    public void CreateSqliteDatabasePath_UsesRequestedPrefixAndDatabaseExtension()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-helper");

        try
        {
            Assert.StartsWith(Path.GetTempPath(), dbPath);
            Assert.StartsWith("easyget-helper-", Path.GetFileName(dbPath));
            Assert.EndsWith(".db", dbPath);
        }
        finally
        {
            TestTempPaths.TryDeleteSqliteDatabase(dbPath);
        }
    }

    [Fact]
    public void TryDeleteSqliteDatabase_RemovesMainWalAndShmFiles()
    {
        var dbPath = TestTempPaths.CreateSqliteDatabasePath("easyget-helper");
        File.WriteAllText(dbPath, "");
        File.WriteAllText($"{dbPath}-wal", "");
        File.WriteAllText($"{dbPath}-shm", "");

        TestTempPaths.TryDeleteSqliteDatabase(dbPath);

        Assert.False(File.Exists(dbPath));
        Assert.False(File.Exists($"{dbPath}-wal"));
        Assert.False(File.Exists($"{dbPath}-shm"));
    }
}
