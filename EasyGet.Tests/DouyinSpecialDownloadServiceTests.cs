using EasyGet.Models;
using EasyGet.Services;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace EasyGet.Tests;

public class DouyinSpecialDownloadServiceTests
{
    [Fact]
    public void TryParseStdoutLine_ParsesProgressJsonLine()
    {
        const string line = """
            {"event":"progress","percent":12.5,"downloaded_bytes":1024,"total_bytes":4096,"speed_bytes_per_sec":512.5,"eta_seconds":6}
            """;

        var parsed = DouyinSpecialDownloadService.TryParseStdoutLine(line, out var message);

        Assert.True(parsed);
        Assert.Equal(DouyinSidecarEventKind.Progress, message.Kind);
        Assert.Equal(12.5, message.Percent);
        Assert.Equal(1024, message.DownloadedBytes);
        Assert.Equal(4096, message.TotalBytes);
        Assert.Equal(512.5, message.SpeedBytesPerSecond);
        Assert.Equal(6, message.EtaSeconds);
        Assert.Equal(line.Trim(), message.RawLine);
    }

    [Fact]
    public void TryParseStdoutLine_ParsesOutcomeCounts()
    {
        const string line = """
            {"event":"progress","summary":{"success_count":4,"failed_count":1,"skipped_count":2},"percent":75}
            """;

        var parsed = DouyinSpecialDownloadService.TryParseStdoutLine(line, out var message);

        Assert.True(parsed);
        Assert.Equal(DouyinSidecarEventKind.Progress, message.Kind);
        Assert.Equal(4, message.SuccessCount);
        Assert.Equal(1, message.FailedCount);
        Assert.Equal(2, message.SkippedCount);
    }

    [Fact]
    public void TryParseStdoutLine_ParsesNestedDetailsCounts()
    {
        const string line = """
            {"event":"success","details":{"counts":{"success":4,"failed":1,"skipped":2}}}
            """;

        var parsed = DouyinSpecialDownloadService.TryParseStdoutLine(line, out var message);

        Assert.True(parsed);
        Assert.Equal(DouyinSidecarEventKind.Success, message.Kind);
        Assert.Equal(4, message.SuccessCount);
        Assert.Equal(1, message.FailedCount);
        Assert.Equal(2, message.SkippedCount);
    }

    [Fact]
    public void TryParseStdoutLine_IgnoresNonJsonLine()
    {
        var parsed = DouyinSpecialDownloadService.TryParseStdoutLine(
            "[douyin-sidecar] browser warmup",
            out var message);

        Assert.False(parsed);
        Assert.Equal(DouyinSidecarEventKind.Unknown, message.Kind);
    }

    [Fact]
    public void TryParseStdoutLine_ParsesDetailsOutputFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-parse-{Guid.NewGuid():N}");
        var videoPath = Path.Combine(outputDirectory, "video.mp4");
        var commentsPath = Path.Combine(outputDirectory, "comments.json");

        DouyinSpecialDownloadService.TryParseStdoutLine(
            $$"""
            {
              "event": "success",
              "output_file_path": "{{JsonEscaped(videoPath)}}",
              "details": {
                "output_files": [
                  "{{JsonEscaped(videoPath)}}",
                  "{{JsonEscaped(commentsPath)}}"
                ]
              }
            }
            """,
            out var message);

        var outputFiles = GetStringListProperty(message, "OutputFilePaths");

        Assert.Equal([videoPath, commentsPath], outputFiles);
    }

    [Fact]
    public void TryMapProgress_MapsAndClampsSidecarProgress()
    {
        DouyinSpecialDownloadService.TryParseStdoutLine(
            """{"event":"progress","percent":120,"downloaded_bytes":-10,"total_bytes":1000,"speed_bytes_per_sec":-3,"eta_seconds":-2}""",
            out var message);

        var mapped = DouyinSpecialDownloadService.TryMapProgress(message, out var progress);

        Assert.True(mapped);
        Assert.Equal(100, progress.Percent);
        Assert.Equal(0, progress.Downloaded);
        Assert.Equal(1000, progress.Total);
        Assert.Equal(0, progress.Speed);
        Assert.Equal(0, progress.Eta);
        Assert.Equal(message.RawLine, progress.RawLine);
    }

    [Fact]
    public void TryMapProgress_ComputesPercentWhenOnlyByteCountsAreProvided()
    {
        DouyinSpecialDownloadService.TryParseStdoutLine(
            """{"event":"progress","downloaded_bytes":250,"total_bytes":1000}""",
            out var message);

        var mapped = DouyinSpecialDownloadService.TryMapProgress(message, out var progress);

        Assert.True(mapped);
        Assert.Equal(25, progress.Percent);
        Assert.Equal(250, progress.Downloaded);
        Assert.Equal(1000, progress.Total);
    }

    [Fact]
    public void ApplySuccessSummary_UpdatesDownloadTask()
    {
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Title = "old title",
            Platform = "",
            OutputDirectory = "D:\\Videos",
            Status = DownloadStatus.Downloading,
            ErrorMessage = "previous error"
        };
        DouyinSpecialDownloadService.TryParseStdoutLine(
            """
            {
              "event": "success",
              "summary": {
                "title": "sidecar title",
                "platform": "Douyin",
                "duration_seconds": 18.5,
                "thumbnail_url": "https://example.test/cover.webp",
                "file_size_bytes": 2048,
                "output_file_path": "D:\\Videos\\sidecar title.mp4"
              }
            }
            """,
            out var message);

        DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(100, task.Progress);
        Assert.Equal("sidecar title", task.Title);
        Assert.Equal("Douyin", task.Platform);
        Assert.Equal(18.5, task.Duration);
        Assert.Equal("https://example.test/cover.webp", task.ThumbnailUrl);
        Assert.Equal(2048, task.FileSize);
        Assert.Equal(2048, task.DownloadedSize);
        Assert.Equal("D:\\Videos\\sidecar title.mp4", task.OutputFilePath);
        Assert.Equal("", task.ErrorMessage);
    }

    [Fact]
    public void ApplySuccessSummary_RejectsUnsafeOutputPath()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-safe-{Guid.NewGuid():N}");
        var unsafeOutputPath = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-unsafe-{Guid.NewGuid():N}", "outside.mp4");
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = outputDirectory,
            Status = DownloadStatus.Downloading
        };
        DouyinSpecialDownloadService.TryParseStdoutLine(
            $$"""{"event":"success","title":"outside","output_file_path":"{{JsonEscaped(unsafeOutputPath)}}"}""",
            out var message);

        DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.NotEqual(unsafeOutputPath, task.OutputFilePath);
        Assert.Contains("outside", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplySuccessSummary_FiltersOutputFilesOutsideOutputDirectory()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-safe-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDirectory, "video.mp4");
        var commentsPath = Path.Combine(outputDirectory, "comments.json");
        var unsafeOutputPath = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-unsafe-{Guid.NewGuid():N}", "outside.json");
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = outputDirectory,
            Status = DownloadStatus.Downloading
        };
        DouyinSpecialDownloadService.TryParseStdoutLine(
            $$"""
            {
              "event": "success",
              "output_file_path": "{{JsonEscaped(primaryPath)}}",
              "details": {
                "output_files": [
                  "{{JsonEscaped(primaryPath)}}",
                  "{{JsonEscaped(commentsPath)}}",
                  "{{JsonEscaped(unsafeOutputPath)}}"
                ]
              }
            }
            """,
            out var message);

        DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

        var outputFiles = GetStringListProperty(task, "OutputFilePaths");

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(primaryPath, task.OutputFilePath);
        Assert.Equal([primaryPath, commentsPath], outputFiles);
        Assert.DoesNotContain(unsafeOutputPath, outputFiles);
    }

    [Fact]
    public void ApplySuccessSummary_AddsSafeManifestPathToOutputFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-manifest-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDirectory, "video.mp4");
        var manifestPath = Path.Combine(outputDirectory, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(manifestPath, "{}");
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/123",
                OutputDirectory = outputDirectory,
                Status = DownloadStatus.Downloading
            };
            DouyinSpecialDownloadService.TryParseStdoutLine(
                $$"""
                {
                  "event": "success",
                  "output_file_path": "{{JsonEscaped(primaryPath)}}",
                  "details": {
                    "output_files": ["{{JsonEscaped(primaryPath)}}"],
                    "manifest_path": "{{JsonEscaped(manifestPath)}}"
                  }
                }
                """,
                out var message);

            DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

            var outputFiles = GetStringListProperty(task, "OutputFilePaths");

            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal([primaryPath, manifestPath], outputFiles);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public void IsDouyinManifestPath_MatchesExactAndContractSnapshotOnly()
    {
        Assert.True(DouyinSpecialDownloadService.IsDouyinManifestPath("download_manifest.jsonl"));
        Assert.True(DouyinSpecialDownloadService.IsDouyinManifestPath("download_manifest.easyget-20260703T123456Z-abcdef12.jsonl"));

        Assert.False(DouyinSpecialDownloadService.IsDouyinManifestPath("download_manifest.easyget-20260703-abcdef12.jsonl"));
        Assert.False(DouyinSpecialDownloadService.IsDouyinManifestPath("download_manifest.easyget-20260703T123456Z-nothexzz.jsonl"));
        Assert.False(DouyinSpecialDownloadService.IsDouyinManifestPath("download_manifest.easyget-20260703T123456Z-abcdef123.jsonl"));
    }

    [Fact]
    public void ApplySuccessSummary_DoesNotAddManifestPathOutsideOutputDirectory()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-manifest-safe-{Guid.NewGuid():N}");
        var unsafeDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-manifest-unsafe-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDirectory, "video.mp4");
        var unsafeManifestPath = Path.Combine(unsafeDirectory, "download_manifest.jsonl");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(unsafeDirectory);
            File.WriteAllText(unsafeManifestPath, "{}");
            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/123",
                OutputDirectory = outputDirectory,
                Status = DownloadStatus.Downloading
            };
            DouyinSpecialDownloadService.TryParseStdoutLine(
                $$"""
                {
                  "event": "success",
                  "output_file_path": "{{JsonEscaped(primaryPath)}}",
                  "details": {
                    "output_files": ["{{JsonEscaped(primaryPath)}}"],
                    "manifest_path": "{{JsonEscaped(unsafeManifestPath)}}"
                  }
                }
                """,
                out var message);

            DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

            var outputFiles = GetStringListProperty(task, "OutputFilePaths");

            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal([primaryPath], outputFiles);
            Assert.DoesNotContain(unsafeManifestPath, outputFiles);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
            TryDeleteDirectory(unsafeDirectory);
        }
    }

    [Fact]
    public void ApplySuccessSummary_AcceptsSnapshotManifestInsideOutputDirectoryAndRejectsOutsideSnapshot()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-snapshot-safe-{Guid.NewGuid():N}");
        var unsafeDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-snapshot-unsafe-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDirectory, "video.mp4");
        var safeManifestPath = Path.Combine(outputDirectory, "download_manifest.easyget-20260703T123456Z-abcdef12.jsonl");
        var unsafeManifestPath = Path.Combine(unsafeDirectory, "download_manifest.easyget-20260703T123456Z-abcdef12.jsonl");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(unsafeDirectory);
            File.WriteAllText(safeManifestPath, "{}");
            File.WriteAllText(unsafeManifestPath, "{}");

            var safeTask = new DownloadTask
            {
                Url = "https://www.douyin.com/video/123",
                OutputDirectory = outputDirectory,
                Status = DownloadStatus.Downloading
            };
            DouyinSpecialDownloadService.TryParseStdoutLine(
                $$"""
                {
                  "event": "success",
                  "output_file_path": "{{JsonEscaped(primaryPath)}}",
                  "details": {
                    "output_files": ["{{JsonEscaped(primaryPath)}}"],
                    "manifest_path": "{{JsonEscaped(safeManifestPath)}}"
                  }
                }
                """,
                out var safeMessage);

            DouyinSpecialDownloadService.ApplySuccessSummary(safeTask, safeMessage);

            var safeOutputFiles = GetStringListProperty(safeTask, "OutputFilePaths");
            Assert.Equal(DownloadStatus.Completed, safeTask.Status);
            Assert.Equal([primaryPath, safeManifestPath], safeOutputFiles);

            var unsafeTask = new DownloadTask
            {
                Url = "https://www.douyin.com/video/123",
                OutputDirectory = outputDirectory,
                Status = DownloadStatus.Downloading
            };
            DouyinSpecialDownloadService.TryParseStdoutLine(
                $$"""
                {
                  "event": "success",
                  "output_file_path": "{{JsonEscaped(primaryPath)}}",
                  "details": {
                    "output_files": ["{{JsonEscaped(primaryPath)}}"],
                    "manifest_path": "{{JsonEscaped(unsafeManifestPath)}}"
                  }
                }
                """,
                out var unsafeMessage);

            DouyinSpecialDownloadService.ApplySuccessSummary(unsafeTask, unsafeMessage);

            var unsafeOutputFiles = GetStringListProperty(unsafeTask, "OutputFilePaths");
            Assert.Equal(DownloadStatus.Completed, unsafeTask.Status);
            Assert.Equal([primaryPath], unsafeOutputFiles);
            Assert.DoesNotContain(unsafeManifestPath, unsafeOutputFiles);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
            TryDeleteDirectory(unsafeDirectory);
        }
    }

    [Fact]
    public void ApplySuccessSummary_DoesNotAddMalformedSnapshotManifestInsideOutputDirectory()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"easyget-sidecar-snapshot-invalid-{Guid.NewGuid():N}");
        var primaryPath = Path.Combine(outputDirectory, "video.mp4");
        var malformedManifestPath = Path.Combine(outputDirectory, "download_manifest.easyget-20260703-abcdef12.jsonl");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(malformedManifestPath, "{}");

            var task = new DownloadTask
            {
                Url = "https://www.douyin.com/video/123",
                OutputDirectory = outputDirectory,
                Status = DownloadStatus.Downloading
            };
            DouyinSpecialDownloadService.TryParseStdoutLine(
                $$"""
                {
                  "event": "success",
                  "output_file_path": "{{JsonEscaped(primaryPath)}}",
                  "details": {
                    "output_files": ["{{JsonEscaped(primaryPath)}}"],
                    "manifest_path": "{{JsonEscaped(malformedManifestPath)}}"
                  }
                }
                """,
                out var message);

            DouyinSpecialDownloadService.ApplySuccessSummary(task, message);

            var outputFiles = GetStringListProperty(task, "OutputFilePaths");
            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal([primaryPath], outputFiles);
            Assert.DoesNotContain(malformedManifestPath, outputFiles);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public void ApplyFailureSummary_UpdatesDownloadTask()
    {
        var task = new DownloadTask
        {
            Title = "kept title",
            Platform = "",
            Status = DownloadStatus.Downloading
        };
        DouyinSpecialDownloadService.TryParseStdoutLine(
            """{"event":"failed","error":"signature expired","title":"new title","platform":"Douyin"}""",
            out var message);

        DouyinSpecialDownloadService.ApplyFailureSummary(task, message);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Equal("signature expired", task.ErrorMessage);
        Assert.Equal("new title", task.Title);
        Assert.Equal("Douyin", task.Platform);
    }

    [Fact]
    public void ApplyCancelledSummary_UpdatesDownloadTask()
    {
        var task = new DownloadTask
        {
            Status = DownloadStatus.Downloading,
            ErrorMessage = "previous error"
        };
        DouyinSpecialDownloadService.TryParseStdoutLine(
            """{"event":"cancelled","message":"cancelled by caller"}""",
            out var message);

        DouyinSpecialDownloadService.ApplyCancelledSummary(task, message);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Equal("", task.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAsync_StreamsProgressAndAppliesTerminalSuccessFromRunner()
    {
        var runner = new FakeSidecarRunner(
            """{"event":"progress","percent":50,"downloaded_bytes":500,"total_bytes":1000}""",
            """{"event":"success","title":"done","file_size_bytes":1000,"output_file_path":"D:\\Videos\\done.mp4"}""");
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = "D:\\Videos"
        };
        var progressReports = new List<DownloadProgress>();

        await service.DownloadAsync(task, new Progress<DownloadProgress>(progressReports.Add));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal("done", task.Title);
        Assert.Equal("Douyin", task.Platform);
        Assert.Equal("D:\\Videos\\done.mp4", task.OutputFilePath);
        Assert.Single(progressReports);
        Assert.Equal(50, progressReports[0].Percent);
    }

    [Fact]
    public async Task DownloadAsync_CapturesRecentEventsAndOutcomeCounts()
    {
        var runner = new FakeSidecarRunner(
            """{"event":"log","message":"开始解析主页"}""",
            """{"event":"progress","percent":50,"summary":{"success_count":2,"failed_count":1,"skipped_count":3}}""",
            """{"event":"success","message":"批量下载完成","summary":{"title":"done","file_size_bytes":1000,"output_file_path":"D:\\Videos\\done.mp4","success_count":4,"failed_count":1,"skipped_count":2}}""");
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/user/MS4wLjABAAAA_test",
            OutputDirectory = "D:\\Videos"
        };

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(4, task.DouyinSuccessCount);
        Assert.Equal(1, task.DouyinFailedCount);
        Assert.Equal(2, task.DouyinSkippedCount);
        Assert.Equal("成功 4 / 失败 1 / 跳过 2", task.DouyinTaskOutcomeSummaryText);
        Assert.True(task.HasDouyinTaskOutcome);
        Assert.True(task.HasDouyinTaskEventLog);
        Assert.Contains("开始解析主页", task.DouyinTaskEventLog, StringComparison.Ordinal);
        Assert.Contains("进度 50%", task.DouyinTaskEventLog, StringComparison.Ordinal);
        Assert.Contains("已完成", task.DouyinTaskEventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_TerminalSuccessStillObservesRunnerExitFailure()
    {
        var runner = new ThrowingAfterTerminalSidecarRunner();
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = "D:\\Videos"
        };

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("exited with code", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_TerminalFailedThenRunnerExitFailurePreservesSidecarFailure()
    {
        var runner = new ThrowingAfterFailedTerminalSidecarRunner();
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = "D:\\Videos"
        };

        await service.DownloadAsync(task);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Equal("cookie expired", task.ErrorMessage);
        Assert.Equal("failed title", task.Title);
        Assert.Equal("Douyin", task.Platform);
    }

    [Fact]
    public async Task DownloadAsync_WithAppConfig_MapsSettingsIntoSidecarRequest()
    {
        var runner = new CapturingSidecarRunner(
            """{"event":"success","title":"done","file_size_bytes":1000,"output_file_path":"D:\\Videos\\done.mp4"}""");
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/user/MS4wLjABAAAA_test",
            OutputDirectory = "D:\\Videos",
            Format = "mp4",
            Quality = "1080",
            Title = "existing title"
        };
        var config = new AppConfig
        {
            CookieContent = "ttwid=abc; odin_tt=def",
            UseProxy = true,
            ProxyAddress = "http://127.0.0.1:7890",
            DouyinMode = " like, mix , music ",
            DouyinLimit = 12,
            DouyinDownloadCover = true,
            DouyinDownloadMusic = false,
            DouyinDownloadJson = true
        };
        SetAppConfigString(config, "DouyinStartTime", " 2024-01-01 ");
        SetAppConfigString(config, "DouyinEndTime", " 2024-01-31 ");
        SetAppConfigBool(config, "DouyinDownloadComments", value: true);
        SetAppConfigBool(config, "DouyinDownloadAvatar", value: true);
        SetAppConfigBool(config, "DouyinEnableDatabase", value: true);
        SetAppConfigBool(config, "DouyinIncrementalDownload", value: true);
        SetAppConfigBool(config, "DouyinDownloadPinned", value: true);
        SetAppConfigString(config, "DouyinFilenameTemplate", " {author}_{title}_{id} ");
        SetAppConfigString(config, "DouyinFolderTemplate", "{date}_{title}");

        await InvokeConfigDownloadAsync(service, task, config);

        Assert.NotNull(runner.LastRequest);
        Assert.Equal(task.Url, GetRequestValue<string>(runner.LastRequest, "Url"));
        Assert.Equal(task.OutputDirectory, GetRequestValue<string>(runner.LastRequest, "OutputDirectory"));
        Assert.Equal(task.Format, GetRequestValue<string>(runner.LastRequest, "Format"));
        Assert.Equal(task.Quality, GetRequestValue<string>(runner.LastRequest, "Quality"));
        Assert.Equal("existing title", GetRequestValue<string>(runner.LastRequest, "Title"));
        Assert.Equal("ttwid=abc; odin_tt=def", GetRequestValue<string>(runner.LastRequest, "Cookie"));
        Assert.Equal("http://127.0.0.1:7890", GetRequestValue<string>(runner.LastRequest, "Proxy"));
        Assert.Equal("like,mix,music", GetRequestValue<string>(runner.LastRequest, "Mode"));
        Assert.Equal(12, GetRequestValue<int>(runner.LastRequest, "Limit"));
        Assert.Equal("2024-01-01", GetRequestValue<string>(runner.LastRequest, "StartTime"));
        Assert.Equal("2024-01-31", GetRequestValue<string>(runner.LastRequest, "EndTime"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncludeCover"));
        Assert.False(GetRequestValue<bool>(runner.LastRequest, "IncludeMusic"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncludeJson"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncludeComments"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncludeAvatar"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncludeDatabase"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "IncrementalDownload"));
        Assert.True(GetRequestValue<bool>(runner.LastRequest, "DownloadPinned"));
        Assert.Equal("{author}_{title}_{id}", GetRequestValue<string>(runner.LastRequest, "FilenameTemplate"));
        Assert.Equal("{date}_{title}_{id}", GetRequestValue<string>(runner.LastRequest, "FolderTemplate"));
    }

    [Fact]
    public void DouyinSidecarProcessRunner_DefaultScriptPath_PrefersWorkspaceToolingSidecar()
    {
        var runner = new DouyinSidecarProcessRunner();

        var scriptPath = GetPrivateField<string>(runner, "_scriptPath");

        Assert.Equal(
            Path.GetFullPath(TestRepositoryPaths.GetRootPath(Path.Combine("tools", "douyin-sidecar", "sidecar.py"))),
            Path.GetFullPath(scriptPath));
    }

    [Fact]
    public void DouyinSidecarProcessRunner_CreateProcessStartInfo_EmitsAlignedCliArguments()
    {
        var sidecarPath = TestRepositoryPaths.GetRootPath(Path.Combine("tools", "douyin-sidecar", "sidecar.py"));
        var runner = new DouyinSidecarProcessRunner("python", sidecarPath);
        const string cookie = "ttwid=abc";
        var request = CreateSidecarRequest(new Dictionary<string, object?>
        {
            ["Url"] = "https://www.douyin.com/user/MS4wLjABAAAA_test",
            ["OutputDirectory"] = "D:\\Videos",
            ["Format"] = "mp4",
            ["Quality"] = "1080",
            ["Title"] = "sample title",
            ["Cookie"] = cookie,
            ["Proxy"] = "http://127.0.0.1:7890",
            ["Mode"] = "post",
            ["Limit"] = 12,
            ["IncludeCover"] = true,
            ["IncludeMusic"] = false,
            ["IncludeJson"] = true,
            ["IncludeComments"] = true,
            ["IncludeAvatar"] = true,
            ["IncludeDatabase"] = true,
            ["IncrementalDownload"] = true,
            ["StartTime"] = "2024-01-01",
            ["EndTime"] = "2024-01-31",
            ["DownloadPinned"] = true,
            ["FilenameTemplate"] = "{author}_{title}_{id}",
            ["FolderTemplate"] = "{date}_{id}"
        });

        var psi = CreateProcessStartInfo(runner, request);
        var args = psi.ArgumentList.ToArray();

        Assert.Equal("python", psi.FileName);
        Assert.Equal(sidecarPath, args[0]);
        AssertArgument(args, "--url", "https://www.douyin.com/user/MS4wLjABAAAA_test");
        AssertArgument(args, "--output-dir", "D:\\Videos");
        AssertArgument(args, "--format", "mp4");
        AssertArgument(args, "--quality", "1080");
        AssertArgument(args, "--title", "sample title");
        Assert.DoesNotContain("--cookie", args);
        Assert.DoesNotContain(cookie, args);
        AssertArgument(args, "--cookie-env", "EASYGET_DOUYIN_COOKIE");
        Assert.True(psi.Environment.ContainsKey("EASYGET_DOUYIN_COOKIE"));
        Assert.Equal(cookie, psi.Environment["EASYGET_DOUYIN_COOKIE"]);
        AssertArgument(args, "--proxy", "http://127.0.0.1:7890");
        AssertArgument(args, "--mode", "post");
        AssertArgument(args, "--limit", "12");
        AssertArgument(args, "--start-time", "2024-01-01");
        AssertArgument(args, "--end-time", "2024-01-31");
        AssertArgument(args, "--filename-template", "{author}_{title}_{id}");
        AssertArgument(args, "--folder-template", "{date}_{id}");
        Assert.Contains("--include-cover", args);
        Assert.DoesNotContain("--include-music", args);
        Assert.Contains("--include-json", args);
        Assert.Contains("--include-comments", args);
        Assert.Contains("--include-avatar", args);
        Assert.Contains("--enable-database", args);
        Assert.Contains("--incremental", args);
        Assert.Contains("--download-pinned", args);
    }

    [Fact]
    public void DouyinSidecarProcessRunner_CreateProcessStartInfo_OmitsCookieEnvWhenCookieIsEmpty()
    {
        var sidecarPath = TestRepositoryPaths.GetRootPath(Path.Combine("tools", "douyin-sidecar", "sidecar.py"));
        var runner = new DouyinSidecarProcessRunner("python", sidecarPath);
        var request = CreateSidecarRequest(new Dictionary<string, object?>
        {
            ["Url"] = "https://www.douyin.com/video/123",
            ["OutputDirectory"] = "D:\\Videos",
            ["Format"] = "mp4",
            ["Quality"] = "1080",
            ["Title"] = "sample title",
            ["Cookie"] = "",
            ["Proxy"] = "",
            ["Mode"] = "post",
            ["Limit"] = 1,
            ["IncludeCover"] = false,
            ["IncludeMusic"] = false,
            ["IncludeJson"] = false,
            ["IncludeComments"] = false,
            ["IncludeAvatar"] = false,
            ["IncludeDatabase"] = false,
            ["IncrementalDownload"] = false,
            ["StartTime"] = "",
            ["EndTime"] = "",
            ["DownloadPinned"] = false,
            ["FilenameTemplate"] = "{date}_{title}_{id}",
            ["FolderTemplate"] = "{date}_{title}_{id}"
        });

        var psi = CreateProcessStartInfo(runner, request);
        var args = psi.ArgumentList.ToArray();

        Assert.DoesNotContain("--cookie", args);
        Assert.DoesNotContain("--cookie-env", args);
        Assert.DoesNotContain("--include-comments", args);
        Assert.DoesNotContain("--include-avatar", args);
        Assert.DoesNotContain("--enable-database", args);
        Assert.DoesNotContain("--incremental", args);
        Assert.DoesNotContain("--start-time", args);
        Assert.DoesNotContain("--end-time", args);
        Assert.DoesNotContain("--download-pinned", args);
        Assert.False(psi.Environment.ContainsKey("EASYGET_DOUYIN_COOKIE"));
    }

    [Fact]
    public async Task DownloadAsync_RedactsCookieFromRunnerExceptionAndLog()
    {
        const string cookie = "ttwid=secret; odin_tt=hidden";
        var runner = new ThrowingCookieSidecarRunner(cookie);
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = "D:\\Videos"
        };
        var logs = new List<string>();
        var config = new AppConfig
        {
            CookieContent = cookie
        };

        await InvokeConfigDownloadAsync(service, task, config, logs.Add);

        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.DoesNotContain(cookie, task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
        Assert.Contains("[redacted]", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("success", DownloadStatus.Completed)]
    [InlineData("cancelled", DownloadStatus.Cancelled)]
    public async Task DownloadAsync_RedactsCookieFromTerminalTaskEventLog(
        string eventName,
        DownloadStatus expectedStatus)
    {
        const string cookie = "ttwid=secret; odin_tt=hidden";
        var runner = new FakeSidecarRunner(
            $$"""
            {"event":"{{eventName}}","message":"terminal {{cookie}}","title":"done","output_file_path":"D:\\Videos\\done.mp4"}
            """);
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            OutputDirectory = "D:\\Videos"
        };
        var config = new AppConfig
        {
            CookieContent = cookie
        };

        await InvokeConfigDownloadAsync(service, task, config);

        Assert.Equal(expectedStatus, task.Status);
        Assert.DoesNotContain(cookie, task.DouyinTaskEventLog, StringComparison.Ordinal);
        Assert.Contains("[redacted]", task.DouyinTaskEventLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_MarksTaskCancelledWhenRunnerObservesCancellation()
    {
        var runner = new CancellingSidecarRunner();
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Status = DownloadStatus.Waiting
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.DownloadAsync(task, cancellationToken: cts.Token);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Equal("", task.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAsync_CancellationPreservesPausedStatus()
    {
        var runner = new WaitingForCancellationSidecarRunner();
        var service = new DouyinSpecialDownloadService(runner);
        var task = new DownloadTask
        {
            Url = "https://www.douyin.com/video/123",
            Status = DownloadStatus.Waiting
        };
        using var cts = new CancellationTokenSource();

        var downloadTask = service.DownloadAsync(task, cancellationToken: cts.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        task.Status = DownloadStatus.Paused;
        await cts.CancelAsync();
        await downloadTask;

        Assert.Equal(DownloadStatus.Paused, task.Status);
        Assert.Equal("", task.ErrorMessage);
    }

    private sealed class FakeSidecarRunner(params string[] lines) : IDouyinSidecarProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return line;
            }
        }
    }

    private sealed class CapturingSidecarRunner(params string[] lines) : IDouyinSidecarProcessRunner
    {
        public DouyinSidecarRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return line;
            }
        }
    }

    private sealed class CancellingSidecarRunner : IDouyinSidecarProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ThrowingAfterTerminalSidecarRunner : IDouyinSidecarProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return """{"event":"success","title":"done","output_file_path":"D:\\Videos\\done.mp4"}""";
            await Task.Yield();
            throw new InvalidOperationException("Douyin sidecar exited with code 1.");
        }
    }

    private sealed class ThrowingAfterFailedTerminalSidecarRunner : IDouyinSidecarProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return """{"event":"failed","error":"cookie expired","title":"failed title","platform":"Douyin"}""";
            await Task.Yield();
            throw new InvalidOperationException("Douyin sidecar exited with code 1.");
        }
    }

    private sealed class ThrowingCookieSidecarRunner(string cookie) : IDouyinSidecarProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in Array.Empty<string>())
                yield return line;

            await Task.Yield();
            throw new InvalidOperationException($"sidecar failed with --cookie {cookie}");
        }
    }

    private sealed class WaitingForCancellationSidecarRunner : IDouyinSidecarProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<string> RunAsync(
            DouyinSidecarRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private static async Task InvokeConfigDownloadAsync(
        DouyinSpecialDownloadService service,
        DownloadTask task,
        AppConfig config,
        Action<string>? logCallback = null)
    {
        var overload = typeof(DouyinSpecialDownloadService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method =>
            {
                if (method.Name != nameof(DouyinSpecialDownloadService.DownloadAsync))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 5
                       && parameters[0].ParameterType == typeof(DownloadTask)
                       && parameters[1].ParameterType == typeof(AppConfig);
            });

        Assert.NotNull(overload);
        var result = overload.Invoke(
            service,
            new object?[] { task, config, null, logCallback, CancellationToken.None });
        await Assert.IsAssignableFrom<Task>(result);
    }

    private static DouyinSidecarRequest CreateSidecarRequest(IReadOnlyDictionary<string, object?> values)
    {
        var constructor = typeof(DouyinSidecarRequest)
            .GetConstructors()
            .SingleOrDefault(ctor =>
                ctor.GetParameters().Any(parameter => parameter.Name == "Cookie")
                && ctor.GetParameters().Any(parameter => parameter.Name == "IncludeJson")
                && ctor.GetParameters().Any(parameter => parameter.Name == "IncludeAvatar"));

        Assert.NotNull(constructor);
        var arguments = constructor
            .GetParameters()
            .Select(parameter =>
            {
                if (values.TryGetValue(parameter.Name!, out var value))
                    return value;

                if (parameter.HasDefaultValue)
                    return parameter.DefaultValue;

                throw new KeyNotFoundException($"Missing constructor test value for {parameter.Name}.");
            })
            .ToArray();
        return Assert.IsType<DouyinSidecarRequest>(constructor.Invoke(arguments));
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        DouyinSidecarProcessRunner runner,
        DouyinSidecarRequest request)
    {
        var method = typeof(DouyinSidecarProcessRunner).GetMethod(
            "CreateProcessStartInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<ProcessStartInfo>(method.Invoke(runner, [request]));
    }

    private static T GetRequestValue<T>(DouyinSidecarRequest? request, string propertyName)
    {
        Assert.NotNull(request);
        var property = typeof(DouyinSidecarRequest).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(request));
    }

    private static void SetAppConfigBool(AppConfig config, string propertyName, bool value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static void SetAppConfigString(AppConfig config, string propertyName, string value)
    {
        var property = typeof(AppConfig).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(config, value);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static string JsonEscaped(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static IReadOnlyList<string> GetStringListProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property!.GetValue(instance);
        var strings = Assert.IsAssignableFrom<IEnumerable<string>>(value);
        return strings.ToList();
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

    private static void AssertArgument(IReadOnlyList<string> args, string name, string expectedValue)
    {
        var index = Enumerable.Range(0, args.Count).FirstOrDefault(i => args[i] == name, -1);
        Assert.True(index >= 0, $"Expected argument {name}.");
        Assert.True(index + 1 < args.Count, $"Expected value after argument {name}.");
        Assert.Equal(expectedValue, args[index + 1]);
    }
}
