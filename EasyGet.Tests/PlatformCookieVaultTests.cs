using System.Text;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class PlatformCookieVaultTests
{
    [Fact]
    public async Task SaveAsync_EncryptsAndRestoresContentByPlatform()
    {
        using var root = new TestDirectory();
        var vault = new PlatformCookieVault(root.DirectoryPath, new XorTestProtector());

        await vault.SaveAsync("twitter", "auth_token=secret", CancellationToken.None);

        var path = root.Path("manual-cookies", "twitter.bin");
        var stored = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Equal("auth_token=secret", await vault.LoadAsync("twitter", CancellationToken.None));
        Assert.True(await vault.ExistsAsync("twitter", CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_AtomicallyOverwritesExistingPlatformContent()
    {
        using var root = new TestDirectory();
        var vault = new PlatformCookieVault(root.DirectoryPath, new XorTestProtector());
        await vault.SaveAsync("youtube", "SID=old", CancellationToken.None);

        await vault.SaveAsync("youtube", "SID=new", CancellationToken.None);

        Assert.Equal("SID=new", await vault.LoadAsync("youtube", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(root.Path("manual-cookies"), "*.tmp"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyRequestedPlatform()
    {
        using var root = new TestDirectory();
        var vault = new PlatformCookieVault(root.DirectoryPath, new XorTestProtector());
        await vault.SaveAsync("youtube", "SID=yt", CancellationToken.None);
        await vault.SaveAsync("twitter", "auth=x", CancellationToken.None);

        await vault.DeleteAsync("youtube", CancellationToken.None);

        Assert.Null(await vault.LoadAsync("youtube", CancellationToken.None));
        Assert.Equal("auth=x", await vault.LoadAsync("twitter", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../youtube")]
    [InlineData("youtube/other")]
    [InlineData("youtube\\other")]
    [InlineData("youtube:other")]
    public async Task SaveAsync_RejectsUnsafePlatformIds(string platformId)
    {
        using var root = new TestDirectory();
        var vault = new PlatformCookieVault(root.DirectoryPath, new XorTestProtector());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            vault.SaveAsync(platformId, "secret", CancellationToken.None));

        Assert.False(Directory.Exists(root.Path("manual-cookies")));
    }

    [Fact]
    public void DpapiSecretProtector_RoundTripsForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var protector = new DpapiSecretProtector();
        var plaintext = Encoding.UTF8.GetBytes("local-cookie-secret");

        var ciphertext = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(plaintext, protector.Unprotect(ciphertext));
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] input)
            => input.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }
}
