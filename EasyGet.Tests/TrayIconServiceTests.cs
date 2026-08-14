using System.Reflection;
using System.Runtime.InteropServices;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class TrayIconServiceTests
{
    [Fact]
    public void TrimBalloonText_UsesFallbackAndBoundsLongText()
    {
        Assert.Equal("EasyGet", TrayIconService.TrimBalloonText("  ", 12));
        Assert.Equal("123456789…", TrayIconService.TrimBalloonText("123456789012", 10));
    }

    [Fact]
    public void TrimBalloonText_RejectsInvalidLimit()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrayIconService.TrimBalloonText("text", 0));

    [Fact]
    public void TrayContextMenuUsesNamedThemeStylesAndFluentGlyphs()
    {
        Assert.Equal("TrayContextMenu", TrayIconService.TrayContextMenuStyleKey);
        Assert.Equal("TrayMenuItem", TrayIconService.TrayMenuItemStyleKey);
        Assert.Equal("TrayMenuSeparator", TrayIconService.TrayMenuSeparatorStyleKey);
        Assert.Equal("\uE8A7", TrayIconService.OpenMenuGlyph);
        Assert.Equal("\uE8BB", TrayIconService.ExitMenuGlyph);
    }

    [Fact]
    public void TaskbarCreatedMessageName_MatchesShellBroadcast()
        => Assert.Equal("TaskbarCreated", TrayIconService.TaskbarCreatedMessageName);

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void ShouldReAddIcon_WhenMissingOrModifyFailed(
        bool isAdded,
        bool modifySucceeded,
        bool expected)
        => Assert.Equal(expected, TrayIconService.ShouldReAddIcon(isAdded, modifySucceeded));

    [Fact]
    public void RegisterWindowMessage_UsesUnicodeUser32EntryPoint()
    {
        var method = typeof(TrayIconService).GetMethod(
            "RegisterWindowMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        var import = method?.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(import);
        Assert.Equal("user32.dll", import.Value, ignoreCase: true);
        Assert.Equal(CharSet.Unicode, import.CharSet);
    }

    [Fact]
    public void ShellNotifyIcon_UsesTheUnicodeWindowsEntryPoint()
    {
        var method = typeof(TrayIconService).GetMethod(
            "ShellNotifyIcon",
            BindingFlags.NonPublic | BindingFlags.Static);
        var import = method?.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(import);
        Assert.Equal("shell32.dll", import.Value, ignoreCase: true);
        Assert.Equal("Shell_NotifyIconW", import.EntryPoint);
        Assert.Equal(CharSet.Unicode, import.CharSet);
        Assert.True(import.ExactSpelling);
    }

    [Fact]
    public void ShellNotifyIcon_UnicodeEntryPointExistsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var library = NativeLibrary.Load("shell32.dll");
        try
        {
            Assert.True(NativeLibrary.TryGetExport(
                library,
                "Shell_NotifyIconW",
                out var address));
            Assert.NotEqual(IntPtr.Zero, address);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
