using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class SingleInstanceServiceTests
{
    [Fact]
    public void DefaultNames_UseLocalMutexAndNamedPipe()
    {
        Assert.Equal(@"Local\EasyGet.SingleInstance", SingleInstanceService.DefaultMutexName);
        Assert.Equal("EasyGet.SingleInstance.Pipe", SingleInstanceService.DefaultPipeName);

        using var service = new SingleInstanceService();
        Assert.Equal(SingleInstanceService.DefaultMutexName, service.MutexName);
        Assert.Equal(SingleInstanceService.DefaultPipeName, service.PipeName);
    }

    [Fact]
    public void InjectedNames_DoNotUseProductionMutex()
    {
        var names = CreateIsolatedNames();
        using var service = new SingleInstanceService(names.Mutex, names.Pipe);

        Assert.Equal(names.Mutex, service.MutexName);
        Assert.Equal(names.Pipe, service.PipeName);
        Assert.NotEqual(SingleInstanceService.DefaultMutexName, service.MutexName);
        Assert.NotEqual(SingleInstanceService.DefaultPipeName, service.PipeName);
        Assert.DoesNotContain(
            SingleInstanceService.DefaultMutexName,
            service.MutexName,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "https://youtu.be/abc123" }, "https://youtu.be/abc123")]
    [InlineData(new[] { "--silent", "https://www.youtube.com/watch?v=abc" }, "https://www.youtube.com/watch?v=abc")]
    [InlineData(new[] { "复制打开： https://v.douyin.com/vi3b7QpNklg/ 看看" }, "https://v.douyin.com/vi3b7QpNklg/")]
    [InlineData(new[] { "HTTPS://example.com/video" }, "HTTPS://example.com/video")]
    public void ExtractUrlFromArguments_ReturnsFirstHttpUrl(string[] arguments, string expected)
        => Assert.Equal(expected, SingleInstanceService.ExtractUrlFromArguments(arguments));

    public static TheoryData<string[]?> NoHttpUrlArguments() =>
    [
        null,
        [],
        [""],
        ["   "],
        ["--help", "ftp://example.com/file"]
    ];

    [Theory]
    [MemberData(nameof(NoHttpUrlArguments))]
    public void ExtractUrlFromArguments_ReturnsNullWhenNoHttpUrl(string[]? arguments)
        => Assert.Null(SingleInstanceService.ExtractUrlFromArguments(arguments));

    [Fact]
    public void SerializePipeMessage_IsEmptyWhenThereAreNoArguments()
    {
        Assert.Equal(string.Empty, SingleInstanceService.SerializePipeMessage(null));
        Assert.Equal(string.Empty, SingleInstanceService.SerializePipeMessage([]));
        Assert.Equal(string.Empty, SingleInstanceService.SerializePipeMessage(["", "  "]));
    }

    [Fact]
    public void SerializePipeMessage_JoinsArgumentsForThePipe()
    {
        var payload = SingleInstanceService.SerializePipeMessage(
            ["--silent", "https://youtu.be/abc123"]);

        Assert.Equal("--silent\nhttps://youtu.be/abc123", payload);
    }

    [Fact]
    public void DeserializePipeMessage_EmptyPayloadMeansActivateOnly()
    {
        Assert.Equal(SingleInstanceActivation.ActivateOnly, SingleInstanceService.DeserializePipeMessage(null));
        Assert.Equal(SingleInstanceActivation.ActivateOnly, SingleInstanceService.DeserializePipeMessage(""));
        Assert.Equal(SingleInstanceActivation.ActivateOnly, SingleInstanceService.DeserializePipeMessage("   "));
        Assert.Equal(SingleInstanceActivation.ActivateOnly, SingleInstanceService.DeserializePipeMessage("--help"));
        Assert.False(SingleInstanceService.DeserializePipeMessage("").HasUrl);
    }

    [Fact]
    public void DeserializePipeMessage_ExtractsHttpUrlFromSerializedArguments()
    {
        var payload = SingleInstanceService.SerializePipeMessage(
            ["--silent", "看这个 https://youtu.be/xyz"]);
        var activation = SingleInstanceService.DeserializePipeMessage(payload);

        Assert.True(activation.HasUrl);
        Assert.Equal("https://youtu.be/xyz", activation.Url);
    }

    [Fact]
    public void TryClaimPrimary_WithIsolatedNames_SecondInstanceLosesAndDisposeReleases()
    {
        var names = CreateIsolatedNames();
        using var first = new SingleInstanceService(names.Mutex, names.Pipe);
        Assert.True(first.TryClaimPrimary());

        using (var second = new SingleInstanceService(names.Mutex, names.Pipe))
        {
            Assert.False(second.TryClaimPrimary());
        }

        first.Dispose();

        using var third = new SingleInstanceService(names.Mutex, names.Pipe);
        Assert.True(third.TryClaimPrimary());
    }

    [Fact]
    public async Task TryNotifyPrimary_DeliversExtractedUrlToListener()
    {
        var names = CreateIsolatedNames();
        using var primary = new SingleInstanceService(names.Mutex, names.Pipe);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.TryStartListening(url => received.TrySetResult(url)));

        using var secondary = new SingleInstanceService(names.Mutex, names.Pipe);
        Assert.True(secondary.TryNotifyPrimary(
            ["--silent", "https://youtu.be/pipe-test"],
            TimeSpan.FromSeconds(3)));

        var url = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://youtu.be/pipe-test", url);
    }

    [Fact]
    public async Task TryNotifyPrimary_SendsNullUrlWhenActivatingOnly()
    {
        var names = CreateIsolatedNames();
        using var primary = new SingleInstanceService(names.Mutex, names.Pipe);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.TryStartListening(url => received.TrySetResult(url)));

        using var secondary = new SingleInstanceService(names.Mutex, names.Pipe);
        Assert.True(secondary.TryNotifyPrimary([], TimeSpan.FromSeconds(3)));

        var url = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(url);
    }

    [Fact]
    public void TryNotifyPrimary_ReturnsFalseWhenNoListener()
    {
        var names = CreateIsolatedNames();
        using var secondary = new SingleInstanceService(names.Mutex, names.Pipe);

        Assert.False(secondary.TryNotifyPrimary(
            ["https://youtu.be/nobody"],
            TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void AppStartup_ClaimsSingleInstanceBeforeServiceProviderAndRestore()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath("App.xaml.cs"));
        var enter = source.IndexOf("BecomePrimaryOrNotify", StringComparison.Ordinal);
        var build = source.IndexOf("BuildServiceProvider", StringComparison.Ordinal);
        var restore = source.IndexOf("RestoreAsync", StringComparison.Ordinal);
        var onExit = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        var dispose = source.IndexOf("_singleInstance?.Dispose()", StringComparison.Ordinal);

        Assert.True(enter >= 0 && enter < build && enter < restore);
        Assert.Contains("TryClaimPrimary", source, StringComparison.Ordinal);
        Assert.Contains("TryNotifyPrimary", source, StringComparison.Ordinal);
        Assert.Contains("TryStartListening", source, StringComparison.Ordinal);
        Assert.True(dispose > onExit);
    }

    private static (string Mutex, string Pipe) CreateIsolatedNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return (
            $@"Local\EasyGet.Tests.SingleInstance.{id}",
            $"EasyGet.Tests.SingleInstance.Pipe.{id}");
    }
}
