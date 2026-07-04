using System.Diagnostics;
using System.Text;
using EasyGet.Models;
using EasyGet.Services;
using EasyGet.ViewModels;
using Xunit;

namespace EasyGet.Tests;

public class HistoryViewModelTests
{
    [Fact]
    public async Task LoadHistory_FiltersAudioItemsWhenAudioFilterIsSelected()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (var service = new HistoryService(dbPath))
            {
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "video item",
                    Format = "mp4",
                    DownloadTime = new DateTime(2026, 6, 9, 10, 0, 0)
                });
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/audio",
                    Title = "audio item",
                    Format = "mp3",
                    DownloadTime = new DateTime(2026, 6, 9, 11, 0, 0)
                });

                var viewModel = new HistoryViewModel(service)
                {
                    SelectedMediaFilter = "音频"
                };

                await viewModel.LoadHistory();

                var item = Assert.Single(viewModel.HistoryItems);
                Assert.Equal("audio item", item.Title);
                Assert.Equal(2, viewModel.TotalHistoryCount);
            }
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task LoadHistory_FiltersVideoItemsWhenVideoFilterIsSelected()
    {
        var dbPath = CreateTempDatabasePath();

        try
        {
            using (var service = new HistoryService(dbPath))
            {
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/video",
                    Title = "video item",
                    Format = "mkv",
                    DownloadTime = new DateTime(2026, 6, 9, 10, 0, 0)
                });
                await service.AddAsync(new DownloadHistory
                {
                    Url = "https://example.com/audio",
                    Title = "audio item",
                    Format = "m4a",
                    DownloadTime = new DateTime(2026, 6, 9, 11, 0, 0)
                });

                var viewModel = new HistoryViewModel(service)
                {
                    SelectedMediaFilter = "视频"
                };

                await viewModel.LoadHistory();

                var item = Assert.Single(viewModel.HistoryItems);
                Assert.Equal("video item", item.Title);
                Assert.Equal(2, viewModel.TotalHistoryCount);
            }
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task LoadHistory_MarksFileExistsWhenAttachmentExistsButPrimaryFileIsMissing()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-attachments-{Guid.NewGuid():N}");
        var missingPrimaryPath = Path.Combine(outputDir, "missing.mp4");
        var missingPlainPath = Path.Combine(outputDir, "missing-plain.mp4");
        var attachmentPath = Path.Combine(outputDir, "comments.json");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(attachmentPath, "{}");

            using var service = new HistoryService(dbPath);
            var historyWithAttachment = new DownloadHistory
            {
                Url = "https://example.com/attached",
                Title = "attached item",
                Format = "mp4",
                FilePath = missingPrimaryPath,
                DownloadTime = new DateTime(2026, 6, 9, 11, 0, 0)
            };
            SetStringListProperty(historyWithAttachment, "AttachmentFilePaths", [attachmentPath]);
            await service.AddAsync(historyWithAttachment);

            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/missing",
                Title = "missing item",
                Format = "mp4",
                FilePath = missingPlainPath,
                DownloadTime = new DateTime(2026, 6, 9, 10, 0, 0)
            });

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var attachedItem = Assert.Single(viewModel.HistoryItems, item => item.Title == "attached item");
            var missingItem = Assert.Single(viewModel.HistoryItems, item => item.Title == "missing item");
            Assert.True(attachedItem.FileExists);
            Assert.Equal(attachmentPath, attachedItem.AvailableFilePath);
            Assert.False(missingItem.FileExists);
            Assert.Equal("", missingItem.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_BuildsDouyinManifestSummaryAndExcludesManifestFromAttachmentCount()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-manifest-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDir, "video.mp4");
        var coverPath = Path.Combine(outputDir, "cover.jpg");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(primaryPath, "video");
            await File.WriteAllTextAsync(coverPath, "cover");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                {"aweme_id":"v1","media_type":"video","desc":"视频标题","author_name":"作者 A","date":"2026-07-01","file_names":["video.mp4"],"tags":["旅行","美食"]}
                {"aweme_id":"g1","media_type":"gallery","desc":"图文标题","author_name":"作者 B","file_paths":["gallery/1.jpg","gallery/2.jpg"]}
                {"aweme_id":"m1","media_type":"music","desc":"原声","author_name":"作者 A","file_names":["music.mp3"]}
                {malformed json
                []
                """);

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/user/MS4wLjABAAAA_test",
                Title = "douyin batch",
                Platform = "Douyin",
                Format = "mp4",
                FilePath = primaryPath,
                DownloadTime = new DateTime(2026, 7, 3, 12, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, coverPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("作品 3 / 视频 1 / 图文 1 / 音乐 1 / 附属 1", GetStringProperty(item, "AttachmentSummaryText"));
            Assert.True(GetBoolProperty(item, "HasAttachmentSummary"));
            Assert.DoesNotContain("附属 2", GetStringProperty(item, "AttachmentSummaryText"), StringComparison.Ordinal);
            Assert.NotNull(item.DouyinManifestSummary);
            Assert.Equal(3, item.DouyinManifestSummary!.ItemCount);
            Assert.Equal(4, item.DouyinManifestSummary.FileCount);
            Assert.True(item.HasDouyinManifestDetails);
            Assert.Collection(
                item.DouyinManifestItems,
                detail =>
                {
                    Assert.Equal("v1", detail.AwemeId);
                    Assert.Equal("视频", detail.MediaTypeText);
                    Assert.Equal("视频标题", detail.Description);
                    Assert.Equal("作者 A", detail.AuthorName);
                    Assert.Equal("2026-07-01", detail.DateText);
                    Assert.Equal("旅行、 美食", detail.TagsText);
                    Assert.Equal("video.mp4", detail.FileNamesText);
                },
                detail =>
                {
                    Assert.Equal("图文", detail.MediaTypeText);
                    Assert.Equal("gallery/1.jpg, gallery/2.jpg", detail.FileNamesText);
                },
                detail => Assert.Equal("音乐", detail.MediaTypeText));
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_AcceptsRootManifestWhenMediaAttachmentIsNested()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-nested-manifest-{Guid.NewGuid():N}");
        var mediaPath = Path.Combine(outputDir, "author", "post", "video.mp4");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            await File.WriteAllTextAsync(mediaPath, "video");
            await File.WriteAllTextAsync(manifestPath, """{"aweme_id":"v1","media_type":"video"}""");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/video/123",
                Title = "nested douyin",
                Platform = "Douyin",
                Format = "mp4",
                DownloadTime = new DateTime(2026, 7, 3, 14, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, mediaPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("作品 1 / 视频 1 / 附属 1", GetStringProperty(item, "AttachmentSummaryText"));
            Assert.True(GetBoolProperty(item, "HasAttachmentSummary"));
            Assert.True(item.FileExists);
            Assert.Equal(mediaPath, item.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public void IsSafeDouyinManifestParentDirectory_RejectsFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(root));

        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-nonroot-manifest-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDir);

            Assert.False(HistoryViewModel.IsSafeDouyinManifestParentDirectory(root!));
            Assert.True(HistoryViewModel.IsSafeDouyinManifestParentDirectory(outputDir));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_RejectsManifestWhenAnyExistingNonManifestAnchorIsOutsideManifestParent()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-cross-anchor-{Guid.NewGuid():N}");
        var outsideDir = Path.Combine(Path.GetTempPath(), $"easyget-history-cross-anchor-outside-{Guid.NewGuid():N}");
        var mediaPath = Path.Combine(outputDir, "author", "post", "video.mp4");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");
        var outsideAttachmentPath = Path.Combine(outsideDir, "sidecar.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            Directory.CreateDirectory(outsideDir);
            await File.WriteAllTextAsync(mediaPath, "video");
            await File.WriteAllTextAsync(manifestPath, """{"aweme_id":"v1","media_type":"video"}""");
            await File.WriteAllTextAsync(outsideAttachmentPath, "{}");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/video/123",
                Title = "cross anchor douyin",
                Platform = "Douyin",
                Format = "mp4",
                DownloadTime = new DateTime(2026, 7, 3, 14, 15, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, mediaPath, outsideAttachmentPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("", GetStringProperty(item, "DouyinManifestSummaryText"));
            Assert.Null(item.DouyinManifestSummary);
            Assert.False(item.HasDouyinManifestDetails);
            Assert.DoesNotContain("作品", GetStringProperty(item, "AttachmentSummaryText"), StringComparison.Ordinal);
            Assert.True(item.FileExists);
            Assert.Equal(mediaPath, item.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
            TryDeleteDirectory(outsideDir);
        }
    }

    [Fact]
    public async Task LoadHistory_RecognizesSnapshotManifestAndExcludesItFromAttachmentsAndQuickActions()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-snapshot-manifest-{Guid.NewGuid():N}");
        var mediaPath = Path.Combine(outputDir, "author", "post", "gallery.jpg");
        var manifestPath = Path.Combine(outputDir, "download_manifest.easyget-20260703T123456Z-abcdef12.jsonl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            await File.WriteAllTextAsync(mediaPath, "image");
            await File.WriteAllTextAsync(manifestPath, """{"aweme_id":"g1","media_type":"gallery"}""");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/note/123",
                Title = "snapshot douyin",
                Platform = "Douyin",
                Format = "jpg",
                DownloadTime = new DateTime(2026, 7, 3, 14, 30, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, mediaPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("作品 1 / 图文 1 / 附属 1", GetStringProperty(item, "AttachmentSummaryText"));
            Assert.True(GetBoolProperty(item, "HasAttachmentSummary"));
            Assert.True(item.FileExists);
            Assert.Equal(mediaPath, item.AvailableFilePath);
            Assert.NotEqual(manifestPath, item.AvailableFilePath);
            Assert.DoesNotContain("附属 2", GetStringProperty(item, "AttachmentSummaryText"), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_DoesNotUseManifestAsAnchorWhenNoNonManifestPathExists()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-manifest-only-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(manifestPath, """{"aweme_id":"v1","media_type":"video"}""");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/video/123",
                Title = "manifest only",
                Platform = "Douyin",
                Format = "mp4",
                FilePath = manifestPath,
                DownloadTime = new DateTime(2026, 7, 3, 15, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("", GetStringProperty(item, "DouyinManifestSummaryText"));
            Assert.DoesNotContain("作品", GetStringProperty(item, "AttachmentSummaryText"), StringComparison.Ordinal);
            Assert.False(item.FileExists);
            Assert.Equal("", item.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_TruncatedManifestShowsPlusAndCountsFirstThousandItems()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-long-manifest-{Guid.NewGuid():N}");
        var mediaPath = Path.Combine(outputDir, "author", "post", "video.mp4");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");
        var padding = new string('x', 1100);
        var manifest = new StringBuilder();

        for (var index = 0; index < 500; index++)
            manifest.AppendLine($$"""{"aweme_id":"v{{index}}","media_type":"video","padding":"{{padding}}"}""");
        for (var index = 0; index < 300; index++)
            manifest.AppendLine($$"""{"aweme_id":"g{{index}}","media_type":"gallery","padding":"{{padding}}"}""");
        for (var index = 0; index < 200; index++)
            manifest.AppendLine($$"""{"aweme_id":"m{{index}}","media_type":"music","padding":"{{padding}}"}""");
        manifest.AppendLine($$"""{"aweme_id":"extra","media_type":"video","padding":"{{padding}}"}""");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            await File.WriteAllTextAsync(mediaPath, "video");
            await File.WriteAllTextAsync(manifestPath, manifest.ToString());

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/user/MS4wLjABAAAA_test",
                Title = "large douyin batch",
                Platform = "Douyin",
                Format = "mp4",
                DownloadTime = new DateTime(2026, 7, 3, 15, 30, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, mediaPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.Equal("作品 1000+ / 视频 500 / 图文 300 / 音乐 200 / 附属 1", GetStringProperty(item, "AttachmentSummaryText"));
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_UsesFirstNonManifestAttachmentForQuickActionsWhenPrimaryIsMissing()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-manifest-action-{Guid.NewGuid():N}");
        var missingPrimaryPath = Path.Combine(outputDir, "missing.mp4");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");
        var mediaPath = Path.Combine(outputDir, "gallery.jpg");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(manifestPath, """{"media_type":"gallery"}""");
            await File.WriteAllTextAsync(mediaPath, "image");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/note/123",
                Title = "douyin gallery",
                Platform = "Douyin",
                Format = "jpg",
                FilePath = missingPrimaryPath,
                DownloadTime = new DateTime(2026, 7, 3, 13, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, mediaPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.True(item.FileExists);
            Assert.Equal(mediaPath, item.AvailableFilePath);
            Assert.NotEqual(manifestPath, item.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_UsesFirstNonManifestAttachmentForQuickActionsWhenManifestAndSnapshotAreFirst()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-snapshot-action-{Guid.NewGuid():N}");
        var missingPrimaryPath = Path.Combine(outputDir, "missing.mp4");
        var manifestPath = Path.Combine(outputDir, "download_manifest.jsonl");
        var snapshotManifestPath = Path.Combine(outputDir, "download_manifest.easyget-20260703T123456Z-abcdef12.jsonl");
        var mediaPath = Path.Combine(outputDir, "author", "post", "video.mp4");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            await File.WriteAllTextAsync(manifestPath, """{"media_type":"video"}""");
            await File.WriteAllTextAsync(snapshotManifestPath, """{"media_type":"video"}""");
            await File.WriteAllTextAsync(mediaPath, "video");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://www.douyin.com/video/123",
                Title = "douyin quick action",
                Platform = "Douyin",
                Format = "mp4",
                FilePath = missingPrimaryPath,
                DownloadTime = new DateTime(2026, 7, 3, 16, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [manifestPath, snapshotManifestPath, mediaPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            Assert.True(item.FileExists);
            Assert.Equal(mediaPath, item.AvailableFilePath);
            Assert.NotEqual(manifestPath, item.AvailableFilePath);
            Assert.NotEqual(snapshotManifestPath, item.AvailableFilePath);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task LoadHistory_LabelsDouyinLiveHlsPlaylistOutputs()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-history-live-hls-{Guid.NewGuid():N}");
        var playlistPath = Path.Combine(outputDir, "主播甲_live_20260704.m3u8");
        var roomMetadataPath = Path.Combine(outputDir, "主播甲_live_20260704_room.json");

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(playlistPath, "#EXTM3U");
            await File.WriteAllTextAsync(roomMetadataPath, "{}");

            using var service = new HistoryService(dbPath);
            var history = new DownloadHistory
            {
                Url = "https://live.douyin.com/123456789",
                Title = "直播标题",
                Platform = "Douyin",
                Format = "m3u8",
                FilePath = playlistPath,
                DownloadTime = new DateTime(2026, 7, 4, 20, 0, 0)
            };
            SetStringListProperty(history, "AttachmentFilePaths", [roomMetadataPath]);
            await service.AddAsync(history);

            var viewModel = new HistoryViewModel(service);

            await viewModel.LoadHistory();

            var item = Assert.Single(viewModel.HistoryItems);
            var summary = GetStringProperty(item, "AttachmentSummaryText");
            Assert.Contains("直播 HLS playlist 1", summary, StringComparison.Ordinal);
            Assert.Contains("附属 1", summary, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public void HistoryViewModelExposesHistoryCardQuickActionCommands()
    {
        var commandProperties = typeof(HistoryViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.Contains("OpenFolderCommand", commandProperties);
        Assert.Contains("PreviewFileCommand", commandProperties);
        Assert.Contains("OpenSourceUrlCommand", commandProperties);
        Assert.Contains("DeleteItemCommand", commandProperties);
    }

    [Fact]
    public async Task OpenFolderCommand_UsesAttachmentPathWhenPrimaryFileIsMissing()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-open-attachment-{Guid.NewGuid():N}");
        var missingPrimaryPath = Path.Combine(outputDir, "missing.mp4");
        var attachmentPath = Path.Combine(outputDir, "comments.json");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(attachmentPath, "{}");
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.OpenFolderCommand.ExecuteAsync(attachmentPath);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal("explorer.exe", processStartInfo.FileName);
            Assert.Equal($"/select,\"{attachmentPath}\"", processStartInfo.Arguments);
            Assert.DoesNotContain(missingPrimaryPath, processStartInfo.Arguments, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task PreviewFileCommand_UsesAttachmentPathWhenPrimaryFileIsMissing()
    {
        var dbPath = CreateTempDatabasePath();
        var outputDir = Path.Combine(Path.GetTempPath(), $"easyget-preview-attachment-{Guid.NewGuid():N}");
        var attachmentPath = Path.Combine(outputDir, "cover.jpg");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(attachmentPath, "preview attachment");
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.PreviewFileCommand.ExecuteAsync(attachmentPath);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(attachmentPath, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task PreviewFileCommand_OpensExistingFileWithDefaultShellHandler()
    {
        var dbPath = CreateTempDatabasePath();
        var mediaPath = Path.Combine(Path.GetTempPath(), $"easyget-preview-{Guid.NewGuid():N}.mp4");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            await File.WriteAllTextAsync(mediaPath, "preview target");
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.PreviewFileCommand.ExecuteAsync(mediaPath);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(mediaPath, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public async Task PreviewFileCommand_OpensResolvedMediaFileWhenPathIsDirectory()
    {
        var dbPath = CreateTempDatabasePath();
        var directory = Path.Combine(Path.GetTempPath(), $"easyget-preview-dir-{Guid.NewGuid():N}");
        var startedProcesses = new List<ProcessStartInfo>();

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "metadata.txt"), "metadata");
            var mediaPath = Path.Combine(directory, "video.mp4");
            File.WriteAllText(mediaPath, "preview target");

            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.PreviewFileCommand.ExecuteAsync(directory);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(mediaPath, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task OpenSourceUrlCommand_OpensHttpUrlWithDefaultBrowser()
    {
        var dbPath = CreateTempDatabasePath();
        var startedProcesses = new List<ProcessStartInfo>();
        const string sourceUrl = "https://www.douyin.com/video/7646616475909852431";

        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service, startedProcesses.Add);

            await viewModel.OpenSourceUrlCommand.ExecuteAsync(sourceUrl);

            var processStartInfo = Assert.Single(startedProcesses);
            Assert.Equal(sourceUrl, processStartInfo.FileName);
            Assert.True(processStartInfo.UseShellExecute);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void CreateOpenFolderStartInfo_SelectsTargetFileInExplorer()
    {
        const string filePath = @"C:\Downloads\EasyGet\video.mp4";

        var startInfo = HistoryViewModel.CreateOpenFolderStartInfo(filePath);

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal(@"/select,""C:\Downloads\EasyGet\video.mp4""", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void OpenFolderCommand_UsesInjectedProcessLauncher()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("ViewModels", "HistoryViewModel.cs")));
        var openFolderStart = source.IndexOf("private async Task OpenFolder", StringComparison.Ordinal);
        var previewFileStart = source.IndexOf("private async Task PreviewFile", StringComparison.Ordinal);

        Assert.True(openFolderStart >= 0, "Expected HistoryViewModel.OpenFolder method to exist.");
        Assert.True(previewFileStart > openFolderStart, "Expected PreviewFile to follow OpenFolder in HistoryViewModel.");

        var openFolderSource = source[openFolderStart..previewFileStart];

        Assert.Contains("_startProcess(CreateOpenFolderStartInfo(filePath));", openFolderSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", openFolderSource, StringComparison.Ordinal);
    }

    private static string CreateTempDatabasePath()
        => TestTempPaths.CreateSqliteDatabasePath("easyget-history-vm");

    private static void TryDeleteDatabase(string dbPath)
        => TestTempPaths.TryDeleteSqliteDatabase(dbPath);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(25);
        }

        return condition();
    }

    private static void SetStringListProperty(object instance, string propertyName, IEnumerable<string> values)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        if (property!.CanWrite)
        {
            property.SetValue(instance, values.ToList());
            return;
        }

        var currentValue = property.GetValue(instance);
        var collection = Assert.IsAssignableFrom<ICollection<string>>(currentValue);
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }

    private static string GetStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(instance));
    }

    private static bool GetBoolProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property!.GetValue(instance));
    }

    [Fact]
    public async Task SearchKeywordChange_TriggersDebouncedSearch()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "special query match",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service);
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            // 改变 SearchKeyword 触发防抖
            viewModel.SearchKeyword = "no match expected";
            
            // 此时由于有 300ms 延时，列表应该还没有刷新
            Assert.Single(viewModel.HistoryItems);

            // 等待防抖任务完成；CI runner 上固定 350ms 容易和调度抢跑。
            var refreshed = await WaitUntilAsync(
                () => viewModel.HistoryItems.Count == 0,
                TimeSpan.FromSeconds(2));

            // 此时防抖自动查询应该已执行完毕，列表应当变空
            Assert.True(refreshed);
            Assert.Empty(viewModel.HistoryItems);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void SearchKeywordDebounce_UsesAwaitedTaskAndDisposesPreviousTokenSource()
    {
        var source = File.ReadAllText(TestRepositoryPaths.GetRootPath(
            Path.Combine("ViewModels", "HistoryViewModel.cs")));

        Assert.Contains("SearchDebounceDelay", source, StringComparison.Ordinal);
        Assert.Contains("DebouncedLoadHistoryAsync", source, StringComparison.Ordinal);
        Assert.Contains("previousSearchCts?.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ContinueWith(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(300", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearAll_WhenConfirmed_ClearsHistory()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "item to clear",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service)
            {
                ConfirmFunc = (msg, title) => true // 确认清空
            };
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            await viewModel.ClearAllCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.HistoryItems);
            Assert.Equal(0, viewModel.TotalHistoryCount);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ClearAll_WhenCancelled_KeepsHistory()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            await service.AddAsync(new DownloadHistory
            {
                Url = "https://example.com/test",
                Title = "item to clear",
                Format = "mp4",
                DownloadTime = DateTime.Now
            });

            var viewModel = new HistoryViewModel(service)
            {
                ConfirmFunc = (msg, title) => false // 取消清空
            };
            await viewModel.LoadHistory();
            Assert.Single(viewModel.HistoryItems);

            await viewModel.ClearAllCommand.ExecuteAsync(null);

            Assert.Single(viewModel.HistoryItems);
            Assert.Equal(1, viewModel.TotalHistoryCount);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void IsSearchOrFilterActive_ReturnsCorrectStatus()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service);

            // 默认状态
            Assert.False(viewModel.IsSearchOrFilterActive);

            // 仅关键字被修改
            viewModel.SearchKeyword = "test";
            Assert.True(viewModel.IsSearchOrFilterActive);

            // 仅筛选被修改
            viewModel.SearchKeyword = "";
            viewModel.SelectedMediaFilter = "视频";
            Assert.True(viewModel.IsSearchOrFilterActive);

            // 恢复默认
            viewModel.SelectedMediaFilter = "全部";
            Assert.False(viewModel.IsSearchOrFilterActive);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ClearFilterAndSearchCommand_ResetsFiltersAndKeyword()
    {
        var dbPath = CreateTempDatabasePath();
        try
        {
            using var service = new HistoryService(dbPath);
            var viewModel = new HistoryViewModel(service)
            {
                SearchKeyword = "some query",
                SelectedMediaFilter = "音频"
            };

            Assert.True(viewModel.IsSearchOrFilterActive);

            await viewModel.ClearFilterAndSearchCommand.ExecuteAsync(null);

            Assert.Equal("", viewModel.SearchKeyword);
            Assert.Equal("全部", viewModel.SelectedMediaFilter);
            Assert.False(viewModel.IsSearchOrFilterActive);
        }
        finally
        {
            TryDeleteDatabase(dbPath);
        }
    }
}
