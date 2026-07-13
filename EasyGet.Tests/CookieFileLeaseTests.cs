using System.Security.AccessControl;
using System.Security.Principal;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class CookieFileLeaseTests
{
    [Fact]
    public async Task CreateLegacyAsync_WritesOnlyTargetPlatformAndDeletesFileOnDispose()
    {
        using var root = new TestDirectory();
        var platform = MediaPlatformResolver.Resolve("https://x.com/user/status/1");
        string path;

        await using (var lease = await CookieFileLease.CreateLegacyAsync(
                         "Cookie: auth_token=secret",
                         platform,
                         "x.com",
                         root.DirectoryPath,
                         CancellationToken.None))
        {
            path = lease.FilePath;
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains(".x.com\tTRUE\t/\tTRUE\t0\tauth_token\tsecret", text);
            Assert.DoesNotContain("youtube.com", text);
            Assert.DoesNotContain("instagram.com", text);
            Assert.Equal(["--cookies", path], lease.Arguments);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void BuildScopedLines_NetscapeInputDropsOtherPlatformsAndPreservesHttpOnly()
    {
        const string input = """
            # Netscape HTTP Cookie File
            #HttpOnly_.youtube.com	TRUE	/	TRUE	0	SID	yt
            .x.com	TRUE	/	TRUE	0	auth_token	x
            """;

        var lines = CookieFileSerializer.BuildScopedLines(
            input,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            "youtube.com");

        Assert.Contains(lines, line => line.StartsWith("#HttpOnly_.youtube.com", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(".x.com", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildScopedLines_JsonInputDropsOtherPlatforms()
    {
        const string input = """
            [
              { "domain": ".youtube.com", "path": "/", "secure": true, "name": "SID", "value": "yt" },
              { "domain": ".x.com", "path": "/", "secure": true, "name": "auth_token", "value": "x" }
            ]
            """;

        var lines = CookieFileSerializer.BuildScopedLines(
            input,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            "youtube.com");

        Assert.Contains(lines, line => line.Contains(".youtube.com", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(".x.com", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildScopedLines_BrowserCookiesDropOtherPlatforms()
    {
        var cookies = new BrowserCookie[]
        {
            new(".x.com", "/", "auth_token", "x", true, 0),
            new(".instagram.com", "/", "sessionid", "ig", true, 0)
        };

        var lines = CookieFileSerializer.BuildScopedLines(
            cookies,
            MediaPlatformResolver.Resolve("https://x.com/user/status/1"));

        Assert.Contains(lines, line => line.Contains(".x.com", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("instagram.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateLegacyAsync_CancellationLeavesNoTemporaryFile()
    {
        using var root = new TestDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CookieFileLease.CreateLegacyAsync(
                "Cookie: SID=secret",
                MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
                "youtube.com",
                root.DirectoryPath,
                cts.Token));

        Assert.Empty(Directory.EnumerateFiles(root.DirectoryPath));
    }

    [Fact]
    public async Task CreateLegacyAsync_RejectsUnsafePlatformIdBeforeWriting()
    {
        using var root = new TestDirectory();
        var unsafePlatform = new MediaPlatformDefinition(
            "youtube/other",
            "unsafe",
            new Uri("https://youtube.com/"),
            ["youtube.com"]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CookieFileLease.CreateLegacyAsync(
                "Cookie: SID=secret",
                unsafePlatform,
                "youtube.com",
                root.DirectoryPath,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(root.DirectoryPath));
    }

    [Fact]
    public void BuildScopedLines_ClampsHugeJsonExpiryToInt64Maximum()
    {
        const string input = """
            [{ "domain": ".youtube.com", "expirationDate": 1e100, "name": "SID", "value": "yt" }]
            """;

        var lines = CookieFileSerializer.BuildScopedLines(
            input,
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            "youtube.com");

        Assert.Contains(lines, line => line.Contains($"\t{long.MaxValue}\tSID\tyt", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupStaleFiles_DeletesOnlyExpiredCookieTextFiles()
    {
        using var root = new TestDirectory();
        root.Touch("old.txt");
        root.Touch("current.txt");
        root.Touch("old.bin");
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(root.Path("old.txt"), now.AddDays(-2));
        File.SetLastWriteTimeUtc(root.Path("current.txt"), now);
        File.SetLastWriteTimeUtc(root.Path("old.bin"), now.AddDays(-2));

        var deleted = CookieFileLease.CleanupStaleFiles(root.DirectoryPath, now, TimeSpan.FromDays(1));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(root.Path("old.txt")));
        Assert.True(File.Exists(root.Path("current.txt")));
        Assert.True(File.Exists(root.Path("old.bin")));
    }

    [Fact]
    public async Task CreateLegacyAsync_RestrictsFileAclToCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var root = new TestDirectory();
        await using var lease = await CookieFileLease.CreateLegacyAsync(
            "Cookie: SID=secret",
            MediaPlatformResolver.Resolve("https://youtube.com/watch?v=1"),
            "youtube.com",
            root.DirectoryPath,
            CancellationToken.None);

        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(lease.FilePath));
        var currentSid = WindowsIdentity.GetCurrent().User;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Contains(rules, rule =>
            Equals(rule.IdentityReference, currentSid)
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public void AppStartup_CleansStaleCookieLeaseFiles()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));

        Assert.Contains("CookieFileLease.CleanupStaleFiles", source, StringComparison.Ordinal);
        Assert.Contains("CookieFileLease.DefaultTemporaryDirectory", source, StringComparison.Ordinal);
    }
}
