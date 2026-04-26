using EasyGet.Services;
using System.Text;
using Xunit;

namespace EasyGet.Tests;

public class DownloadFileNameBuilderTests
{
    static DownloadFileNameBuilderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void BuildOutputTemplate_RemovesTrailingPunctuationBeforeExtension()
    {
        var template = DownloadFileNameBuilder.BuildOutputTemplate(
            @"D:\Videos\YouTube",
            "比特币得益于日本央行各派信号，Rakuten Wallet官方宣布正式上线XRP。");

        Assert.Equal(
            @"D:\Videos\YouTube\比特币得益于日本央行各派信号，Rakuten Wallet官方宣布正式上线XRP.%(ext)s",
            template);
    }

    [Fact]
    public void BuildOutputTemplate_EscapesPercentSignsForYtDlpTemplates()
    {
        var template = DownloadFileNameBuilder.BuildOutputTemplate(
            @"D:\Videos\YouTube",
            "100% BTC");

        Assert.Equal(@"D:\Videos\YouTube\100%% BTC.%(ext)s", template);
    }

    [Fact]
    public void BuildOutputTemplate_FallsBackToMetadataTemplateWhenTitleMissing()
    {
        var template = DownloadFileNameBuilder.BuildOutputTemplate(
            @"D:\Videos\YouTube",
            "");

        Assert.Equal(@"D:\Videos\YouTube\%(title).150s.%(ext)s", template);
    }

    [Fact]
    public void SanitizeResolvedTitle_RemovesCharactersNotEncodableByExternalTool()
    {
        var gbk = Encoding.GetEncoding(
            936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        var sanitized = DownloadFileNameBuilder.SanitizeResolvedTitle(
            "我如何用obsidian+AI做知識管理&內容創作？🤔",
            gbk);

        Assert.Equal("我如何用obsidian+AI做知識管理&內容創作", sanitized);
    }
}
