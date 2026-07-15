using System.Diagnostics;
using EasyGet.Services.Cookies;
using Xunit;

namespace EasyGet.Tests;

public sealed class DefaultBrowserLauncherTests
{
    [Fact]
    public void App_RegistersDefaultBrowserLauncher()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));

        Assert.Contains(
            "AddSingleton<IDefaultBrowserLauncher, DefaultBrowserLauncher>",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_UsesShellExecuteForExactHttpsAddress()
    {
        ProcessStartInfo? captured = null;
        var launcher = new DefaultBrowserLauncher(startInfo => captured = startInfo);
        var uri = new Uri("https://passport.bilibili.com/login?from=easyget#account");

        await launcher.OpenAsync(uri);

        Assert.NotNull(captured);
        Assert.Equal(uri.AbsoluteUri, captured!.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Empty(captured.ArgumentList);
    }

    [Theory]
    [InlineData("http://example.com/login")]
    [InlineData("https://user:password@example.com/login")]
    [InlineData("file:///C:/secret.html")]
    public async Task OpenAsync_RejectsUnsafeAddress(string address)
    {
        var wasStarted = false;
        var launcher = new DefaultBrowserLauncher(_ => wasStarted = true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => launcher.OpenAsync(new Uri(address)));

        Assert.False(wasStarted);
    }

    [Fact]
    public async Task OpenAsync_PropagatesStartupFailureForUiToHandle()
    {
        var launcher = new DefaultBrowserLauncher(
            _ => throw new InvalidOperationException("test launch failure"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.OpenAsync(new Uri("https://www.youtube.com/")));

        Assert.Contains("test launch failure", error.Message, StringComparison.Ordinal);
    }
}
