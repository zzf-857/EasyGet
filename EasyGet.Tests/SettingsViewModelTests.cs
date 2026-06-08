using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class SettingsViewModelTests
{
    [Theory]
    [InlineData("", true, "检测中")]
    [InlineData("正在安装 yt-dlp...", true, "准备安装")]
    [InlineData("yt-dlp 下载中... 45%", true, "下载中")]
    [InlineData("正在解压 ffmpeg...", true, "解压中")]
    [InlineData("ffmpeg 安装完成。", true, "完成")]
    [InlineData("环境安装完成。", false, "完成")]
    [InlineData("环境安装未完成，请检查网络或手动安装。", false, "失败")]
    [InlineData("安装失败: 网络超时", false, "失败")]
    [InlineData("", false, "")]
    public void DescribeInstallStatusStage_ClassifiesInstallProgress(string message, bool isInstalling, string expectedStage)
    {
        Assert.Equal(expectedStage, SettingsViewModel.DescribeInstallStatusStage(message, isInstalling));
    }
}
