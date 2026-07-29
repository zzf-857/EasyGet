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
