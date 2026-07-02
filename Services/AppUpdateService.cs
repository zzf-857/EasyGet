using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EasyGet.Models;
using Microsoft.Win32;

namespace EasyGet.Services;

public class AppUpdateService : IAppUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/zzf-857/EasyGet/releases/latest";
    private const string InnoUninstallKeyName = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5A8E1D83-9FE4-41A8-8F8D-DED866E53335}_is1";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly object LogSync = new();
    private readonly HttpClient _httpClient;
    private readonly string _updatesDir;
    private readonly string _logFilePath;

    public AppUpdateService()
        : this(SharedHttpClient)
    {
    }

    internal AppUpdateService(HttpClient httpClient)
        : this(httpClient, GetDefaultUpdatesDir(), GetDefaultUpdateLogPath())
    {
    }

    internal AppUpdateService(HttpClient httpClient, string updatesDir)
        : this(httpClient, updatesDir, GetDefaultUpdateLogPath())
    {
    }

    internal AppUpdateService(HttpClient httpClient, string updatesDir, string logFilePath)
    {
        _httpClient = httpClient;
        _updatesDir = updatesDir;
        _logFilePath = logFilePath;
    }

    public string CurrentVersion => GetCurrentVersion();
    public string CurrentExecutablePath => GetCurrentExecutablePath();
    public string RuntimeDescription => DescribeRuntime(CurrentExecutablePath, AppContext.BaseDirectory, GetRegisteredInstallDirectory());

    public async Task<AppUpdateInfo> CheckLatestAsync(CancellationToken ct = default)
    {
        LogUpdateEvent(
            "CheckLatestAsync started",
            ("api", LatestReleaseUrl),
            ("currentVersion", CurrentVersion),
            ("executablePath", CurrentExecutablePath),
            ("baseDirectory", AppContext.BaseDirectory),
            ("runtime", RuntimeDescription));

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUrl, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = ParseLatestReleaseJson(json, CurrentVersion);
            LogUpdateEvent(
                "CheckLatestAsync completed",
                ("latestVersion", result.LatestVersion),
                ("isUpdateAvailable", result.IsUpdateAvailable.ToString()),
                ("installerAsset", result.InstallerFileName),
                ("installerUrl", result.InstallerDownloadUrl?.ToString() ?? ""));
            return result;
        }
        catch (Exception ex)
        {
            LogUpdateEvent("CheckLatestAsync failed", ("exception", ex.ToString()));
            throw;
        }
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (updateInfo.InstallerDownloadUrl is null)
            throw new InvalidOperationException("没有可下载的 EasyGet 安装包。");

        var fileName = string.IsNullOrWhiteSpace(updateInfo.InstallerFileName)
            ? $"EasyGet-Setup-v{updateInfo.LatestVersion}.exe"
            : updateInfo.InstallerFileName;
        var targetPath = Path.Combine(_updatesDir, fileName);
        var tempPath = $"{targetPath}.download";

        LogUpdateEvent(
            "DownloadInstallerAsync started",
            ("latestVersion", updateInfo.LatestVersion),
            ("installerAsset", fileName),
            ("downloadUrl", updateInfo.InstallerDownloadUrl.ToString()),
            ("tempPath", tempPath),
            ("targetPath", targetPath),
            ("currentVersion", CurrentVersion),
            ("executablePath", CurrentExecutablePath),
            ("baseDirectory", AppContext.BaseDirectory),
            ("runtime", RuntimeDescription));

        try
        {
            Directory.CreateDirectory(_updatesDir);

            using var response = await _httpClient.GetAsync(updateInfo.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? updateInfo.InstallerSize;
            LogUpdateEvent(
                "Download response accepted",
                ("contentLength", (response.Content.Headers.ContentLength ?? 0).ToString()),
                ("expectedSize", total.ToString()),
                ("tempPath", tempPath),
                ("targetPath", targetPath));

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    if (total > 0)
                        progress?.Report(Math.Clamp(totalRead * 100d / total, 0, 100));
                }

                await target.FlushAsync(ct);
            }

            LogUpdateEvent(
                "Download streams disposed before move",
                ("completedAt", DateTimeOffset.Now.ToString("O")),
                ("tempPath", tempPath),
                ("targetPath", targetPath),
                ("tempSize", GetFileSizeDescription(tempPath)),
                ("targetSizeBeforeMove", GetFileSizeDescription(targetPath)));

            progress?.Report(100);
            await MoveTempInstallerAsync(tempPath, targetPath, ct);

            LogUpdateEvent(
                "DownloadInstallerAsync completed",
                ("targetPath", targetPath),
                ("targetSizeAfterMove", GetFileSizeDescription(targetPath)),
                ("tempExistsAfterMove", File.Exists(tempPath).ToString()));
            return targetPath;
        }
        catch (Exception ex)
        {
            LogUpdateEvent(
                "DownloadInstallerAsync failed",
                ("tempPath", tempPath),
                ("targetPath", targetPath),
                ("tempSize", GetFileSizeDescription(tempPath)),
                ("targetSize", GetFileSizeDescription(targetPath)),
                ("exception", ex.ToString()));
            throw;
        }
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return false;

        LogUpdateEvent(
            "Launching installer",
            ("installerPath", installerPath),
            ("currentVersion", CurrentVersion),
            ("executablePath", CurrentExecutablePath),
            ("runtime", RuntimeDescription));

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
        return true;
    }

    public static AppUpdateInfo ParseLatestReleaseJson(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tagName = GetString(root, "tag_name");
        var latestVersion = NormalizeVersionText(tagName);
        var releaseUrl = TryCreateUri(GetString(root, "html_url"));

        string installerFileName = "";
        long installerSize = 0;
        Uri? installerUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (!IsInstallerAsset(name))
                    continue;

                installerFileName = name;
                installerSize = GetInt64(asset, "size");
                installerUrl = TryCreateUri(GetString(asset, "browser_download_url"));
                break;
            }
        }

        return new AppUpdateInfo
        {
            CurrentVersion = NormalizeVersionText(currentVersion),
            LatestVersion = latestVersion,
            IsUpdateAvailable = CompareVersions(latestVersion, currentVersion) > 0,
            ReleasePageUrl = releaseUrl,
            InstallerDownloadUrl = installerUrl,
            InstallerFileName = installerFileName,
            InstallerSize = installerSize
        };
    }

    public static int CompareVersions(string left, string right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);

        for (var i = 0; i < 3; i++)
        {
            var delta = leftParts[i].CompareTo(rightParts[i]);
            if (delta != 0)
                return delta;
        }

        return 0;
    }

    internal static string DescribeRuntime(
        string executablePath,
        string baseDirectory,
        string? registeredInstallDirectory = null)
    {
        var normalizedBase = NormalizeDirectory(baseDirectory);
        var normalizedRegistered = NormalizeDirectory(registeredInstallDirectory ?? "");

        if (!string.IsNullOrWhiteSpace(normalizedRegistered)
            && AreSameDirectory(normalizedBase, normalizedRegistered))
        {
            return "安装版运行";
        }

        if (ContainsPathSegment(normalizedBase, "artifacts")
            && ContainsPathSegment(normalizedBase, "publish"))
        {
            return "发布目录运行";
        }

        if (ContainsPathSegment(normalizedBase, "bin")
            || ContainsPathSegment(normalizedBase, "obj"))
        {
            return "开发构建运行";
        }

        if (IsProjectExeDirectory(normalizedBase))
            return "项目 EXE 目录运行";

        if (string.Equals(Path.GetFileName(executablePath), "EasyGet.exe", StringComparison.OrdinalIgnoreCase))
            return "自定义目录运行";

        return "未知运行模式";
    }

    private async Task MoveTempInstallerAsync(string tempPath, string targetPath, CancellationToken ct)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                LogUpdateEvent(
                    "File.Move attempt",
                    ("attempt", attempt.ToString()),
                    ("tempPath", tempPath),
                    ("targetPath", targetPath),
                    ("tempSizeBeforeMove", GetFileSizeDescription(tempPath)),
                    ("targetSizeBeforeMove", GetFileSizeDescription(targetPath)));

                EnsureFileCanBeOpenedExclusively(tempPath);
                if (File.Exists(targetPath))
                {
                    EnsureFileCanBeOpenedExclusively(targetPath);
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
                EnsureFileCanBeOpenedExclusively(targetPath);

                LogUpdateEvent(
                    "File.Move completed",
                    ("attempt", attempt.ToString()),
                    ("targetPath", targetPath),
                    ("targetSizeAfterMove", GetFileSizeDescription(targetPath)),
                    ("tempExistsAfterMove", File.Exists(tempPath).ToString()));
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                LogUpdateEvent(
                    "File.Move retry scheduled",
                    ("attempt", attempt.ToString()),
                    ("tempPath", tempPath),
                    ("targetPath", targetPath),
                    ("tempSize", GetFileSizeDescription(tempPath)),
                    ("targetSize", GetFileSizeDescription(targetPath)),
                    ("exception", ex.ToString()));
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                LogUpdateEvent(
                    "File.Move retry scheduled",
                    ("attempt", attempt.ToString()),
                    ("tempPath", tempPath),
                    ("targetPath", targetPath),
                    ("tempSize", GetFileSizeDescription(tempPath)),
                    ("targetSize", GetFileSizeDescription(targetPath)),
                    ("exception", ex.ToString()));
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }

        throw new IOException(
            $"更新包落盘失败，临时文件无法移动到目标安装包。Temp: {tempPath}; Target: {targetPath}",
            lastException);
    }

    private static void EnsureFileCanBeOpenedExclusively(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    private static bool IsInstallerAsset(string name)
        => name.StartsWith("EasyGet-Setup-v", StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static int[] ParseVersionParts(string version)
    {
        var normalized = NormalizeVersionText(version);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new[] { 0, 0, 0 };

        for (var i = 0; i < Math.Min(parts.Length, result.Length); i++)
        {
            if (int.TryParse(parts[i], out var number))
                result[i] = Math.Max(0, number);
        }

        return result;
    }

    private static string NormalizeVersionText(string version)
    {
        var value = (version ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        var metadataIndex = value.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            value = value[..metadataIndex];

        return string.IsNullOrWhiteSpace(value) ? "0.0.0" : value;
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(AppUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(AppUpdateService).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        return NormalizeVersionText(version);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            return 0;
        }

        return Math.Max(0, number);
    }

    private static Uri? TryCreateUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string GetDefaultUpdatesDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyGet",
            "updates");

    private static string GetDefaultUpdateLogPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyGet",
            "logs",
            "update.log");

    private static string GetCurrentExecutablePath()
        => Environment.ProcessPath
           ?? Process.GetCurrentProcess().MainModule?.FileName
           ?? "";

    private static string GetRegisteredInstallDirectory()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(InnoUninstallKeyName);
                var installLocation = key?.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(installLocation))
                    return installLocation;

                var appPath = key?.GetValue("Inno Setup: App Path")?.ToString();
                if (!string.IsNullOrWhiteSpace(appPath))
                    return appPath;
            }
            catch
            {
                // Registry access can fail under restricted user contexts; runtime
                // detection falls back to path heuristics in that case.
            }
        }

        return "";
    }

    private void LogUpdateEvent(string message, params (string Key, string Value)[] values)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var lines = new List<string>
            {
                $"[{DateTimeOffset.Now:O}] {message}"
            };

            foreach (var (key, value) in values)
                lines.Add($"  {key}: {value}");

            lock (LogSync)
            {
                File.AppendAllText(_logFilePath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
        }
        catch
        {
            // Update logging must never break update checks or downloads.
        }
    }

    private static string GetFileSizeDescription(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length.ToString() : "missing";
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.Message}";
        }
    }

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var fullPath = Path.GetFullPath(value);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool AreSameDirectory(string left, string right)
        => string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPathSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectExeDirectory(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2)
            return false;

        return string.Equals(parts[^1], "EXE", StringComparison.OrdinalIgnoreCase)
               && parts.Any(part => string.Equals(part, "EasyGet", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyGet-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
