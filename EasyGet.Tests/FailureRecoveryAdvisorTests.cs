using System.ComponentModel;
using System.IO;
using System.Net.Http;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public sealed class FailureRecoveryAdvisorTests
{
    public static TheoryData<string, FailureCategory, string> TextCases => new()
    {
        { "Login required; cookie expired", FailureCategory.Authentication, FailureRecoveryActionKeys.OpenAccountSettings },
        { "Connection timed out", FailureCategory.Network, FailureRecoveryActionKeys.Retry },
        { "Proxy authentication failed", FailureCategory.Proxy, FailureRecoveryActionKeys.OpenProxySettings },
        { "ffmpeg executable not found", FailureCategory.Tooling, FailureRecoveryActionKeys.RepairTools },
        { "No space left on device", FailureCategory.Storage, FailureRecoveryActionKeys.ChooseOutputFolder },
        { "Access denied", FailureCategory.Access, FailureRecoveryActionKeys.OpenAccessHelp },
        { "HTTP 429 Too Many Requests", FailureCategory.RateLimit, FailureRecoveryActionKeys.RetryLater },
        { "Unsupported URL", FailureCategory.Unsupported, FailureRecoveryActionKeys.OpenSupport },
        { "unexpected failure", FailureCategory.Unknown, FailureRecoveryActionKeys.OpenDiagnostics }
    };

    [Theory]
    [MemberData(nameof(TextCases))]
    public void Advise_ClassifiesTextAndReturnsStableAction(
        string error,
        FailureCategory expectedCategory,
        string expectedAction)
    {
        var advice = new FailureRecoveryAdvisor().Advise(error);

        Assert.Equal(expectedCategory, advice.Category);
        Assert.Equal(expectedAction, advice.SuggestedActionKey);
        Assert.False(string.IsNullOrWhiteSpace(advice.UserMessage));
    }

    [Theory]
    [MemberData(nameof(ExceptionCases))]
    public void Advise_UsesExceptionTypeFallback(Exception exception, FailureCategory expectedCategory)
    {
        var advice = new FailureRecoveryAdvisor().Advise(exception);

        Assert.Equal(expectedCategory, advice.Category);
    }

    public static TheoryData<Exception, FailureCategory> ExceptionCases => new()
    {
        { new UnauthorizedAccessException(), FailureCategory.Access },
        { new DirectoryNotFoundException(), FailureCategory.Storage },
        { new TimeoutException(), FailureCategory.Network },
        { new HttpRequestException(), FailureCategory.Network },
        { new NotSupportedException(), FailureCategory.Unsupported },
        { new FileNotFoundException("missing", "ffmpeg.exe"), FailureCategory.Tooling },
        { new Win32Exception(2), FailureCategory.Tooling }
    };

    [Fact]
    public void Advise_TraversesAggregateAndInnerExceptions()
    {
        var exception = new AggregateException(
            new InvalidOperationException("outer", new TimeoutException()),
            new UnauthorizedAccessException());

        var advice = new FailureRecoveryAdvisor().Advise(exception);

        Assert.Equal(FailureCategory.Network, advice.Category);
    }

    [Fact]
    public void Advise_DoesNotExposeRawFailureOrSecret()
    {
        const string secret = "private-cookie-value";
        var advice = new FailureRecoveryAdvisor().Advise(
            new InvalidOperationException($"Cookie: {secret}"));

        Assert.Equal(FailureCategory.Authentication, advice.Category);
        Assert.DoesNotContain(secret, advice.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, advice.SuggestedActionKey, StringComparison.Ordinal);
    }
}
