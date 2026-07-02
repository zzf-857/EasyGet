using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EasyGet.Models;
using EasyGet.Services;
using Xunit;

namespace EasyGet.Tests;

public class XiaohongshuImageDownloadServiceTests
{
    [Theory]
    [InlineData("https://www.xiaohongshu.com/discovery/item/6a1d4bd30000000008024b72?source=webshare", "6a1d4bd30000000008024b72")]
    [InlineData("https://www.xiaohongshu.com/explore/6a1d4bd30000000008024b72", "6a1d4bd30000000008024b72")]
    [InlineData("https://www.xiaohongshu.com/explore/abc123XYZ", "abc123XYZ")]
    [InlineData("https://www.xiaohongshu.com/user/profile/123", "")]
    public void ExtractNoteId_ExtractsCorrectId(string url, string expectedId)
    {
        var noteId = XiaohongshuImageDownloadService.ExtractNoteId(url);
        Assert.Equal(expectedId, noteId);
    }

    [Fact]
    public void ExtractNoteDataFromJson_ExtractsExpectedProperties_And_ReplacesUndefined()
    {
        const string noteId = "6a1d4bd30000000008024b72";
        const string html = """
            <html>
            <body>
            <script>
            window.__INITIAL_STATE__ = {
              "note": {
                "noteDetailMap": {
                  "6a1d4bd30000000008024b72": {
                    "note": {
                      "title": "测试图文标题",
                      "desc": "测试图文描述",
                      "imageList": [
                        {
                          "urlDefault": "http://sns-webpic-qc.xhscdn.com/img1.jpg",
                          "urlPre": undefined
                        }
                      ]
                    }
                  }
                }
              }
            };
            </script>
            </body>
            </html>
            """;

        var noteData = XiaohongshuImageDownloadService.ExtractNoteDataFromJson(html, noteId);
        
        Assert.NotNull(noteData);
        Assert.True(noteData.Value.TryGetProperty("title", out var titleProp));
        Assert.Equal("测试图文标题", titleProp.GetString());

        Assert.True(noteData.Value.TryGetProperty("desc", out var descProp));
        Assert.Equal("测试图文描述", descProp.GetString());

        Assert.True(noteData.Value.TryGetProperty("imageList", out var imageListProp));
        Assert.Equal(JsonValueKind.Array, imageListProp.ValueKind);
        Assert.Equal(1, imageListProp.GetArrayLength());

        var firstImage = imageListProp[0];
        Assert.True(firstImage.TryGetProperty("urlDefault", out var urlDefaultProp));
        Assert.Equal("http://sns-webpic-qc.xhscdn.com/img1.jpg", urlDefaultProp.GetString());

        Assert.True(firstImage.TryGetProperty("urlPre", out var urlPreProp));
        Assert.Equal(JsonValueKind.Null, urlPreProp.ValueKind); // undefined was replaced with null
    }

    [Fact]
    public void IsXiaohongshuUrl_IdentifiesCorrectDomains()
    {
        Assert.True(YtDlpService.IsXiaohongshuUrl("https://www.xiaohongshu.com/explore/abc"));
        Assert.True(YtDlpService.IsXiaohongshuUrl("http://xiaohongshu.com/discovery/item/123"));
        Assert.True(YtDlpService.IsXiaohongshuUrl("https://xhslink.com/aBc123XYZ"));
        Assert.False(YtDlpService.IsXiaohongshuUrl("https://www.bilibili.com/video/BV123"));
        Assert.False(YtDlpService.IsXiaohongshuUrl(""));
        Assert.False(YtDlpService.IsXiaohongshuUrl(null!));
    }

    [Fact(Skip = "Live external-site test. Run manually when validating Xiaohongshu network behavior.")]
    public async Task LiveDownloadTest()
    {
        var configService = new ConfigService();
        await configService.LoadAsync();
        
        var service = new XiaohongshuImageDownloadService(configService);
        var url = "https://www.xiaohongshu.com/discovery/item/6a1d4bd30000000008024b72?source=webshare&xhsshare=pc_web&xsec_token=ABIb_Pvhngcei7NkLQkQ95_-UO3w49ylotO02t3FD-J-U=&xsec_source=pc_share";
        
        var info = await service.GetImageNoteInfoAsync(url);
        Assert.NotNull(info);
        Assert.Contains("AI", info.Title);
        Assert.Equal("XiaoHongShu", info.Platform);
        
        var tempDir = Path.Combine(Path.GetTempPath(), $"easyget-xhs-live-test-{Guid.NewGuid():N}");
        var task = new DownloadTask
        {
            Url = url,
            Title = info.Title,
            OutputDirectory = tempDir
        };
        
        try
        {
            var success = await service.TryDownloadAsync(task);
            Assert.True(success);
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.True(task.Format == "jpg" || task.Format == "png" || task.Format == "webp");
            Assert.True(Directory.Exists(task.OutputDirectory));
            
            var subfolder = Path.Combine(task.OutputDirectory, DownloadFileNameBuilder.SanitizeResolvedTitle(info.Title));
            Assert.True(Directory.Exists(subfolder));
            var files = Directory.GetFiles(subfolder);
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                Assert.True(new FileInfo(file).Length > 0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
