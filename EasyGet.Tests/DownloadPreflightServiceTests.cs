using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class DownloadPreflightServiceTests
{
    [Fact]
    public void Check_BlocksWhenDirectoryIsMissing()
    {
        var service = CreateService();

        var result = service.Check("  ");

        Assert.False(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Code == "output-directory-missing");
    }

    [Fact]
    public void Check_CreatesDirectoryAndChecksWriteAccess()
    {
        var created = new List<string>();
        var service = CreateService(
            directoryExists: _ => false,
            createDirectory: created.Add);

        var result = service.Check(@"C:\Downloads");

        Assert.True(result.CanProceed);
        Assert.Single(created);
        Assert.Equal(Path.GetFullPath(@"C:\Downloads"), result.OutputDirectory);
    }

    [Fact]
    public void Check_BlocksReadOnlyDirectory()
    {
        var service = CreateService(canWriteDirectory: _ => false);

        var result = service.Check(@"C:\Downloads");

        Assert.False(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Code == "output-directory-readonly");
    }

    [Fact]
    public void Check_BlocksWhenExpectedOutputWouldConsumeReserve()
    {
        var service = CreateService(getAvailableBytes: _ => 700L * 1024 * 1024);

        var result = service.Check(@"C:\Downloads", 500L * 1024 * 1024);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Code == "insufficient-space");
    }

    [Fact]
    public void Check_WarnsButAllowsUnknownSizeOnLowSpace()
    {
        var service = CreateService(getAvailableBytes: _ => 700L * 1024 * 1024);

        var result = service.Check(@"C:\Downloads");

        Assert.True(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Code == "low-space");
    }

    private static DownloadPreflightService CreateService(
        Func<string, bool>? directoryExists = null,
        Action<string>? createDirectory = null,
        Func<string, bool>? canWriteDirectory = null,
        Func<string, long?>? getAvailableBytes = null)
        => new(
            directoryExists ?? (_ => true),
            createDirectory ?? (_ => { }),
            canWriteDirectory ?? (_ => true),
            getAvailableBytes ?? (_ => 10L * 1024 * 1024 * 1024));
}
