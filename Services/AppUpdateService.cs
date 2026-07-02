using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EasyGet.Models;

namespace EasyGet.Services;

public class AppUpdateService : IAppUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/zzf-857/EasyGet/releases/latest";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly string _updatesDir;

    public AppUpdateService()
        : this(SharedHttpClient)
    {
    }

    internal AppUpdateService(HttpClient httpClient)
        : this(httpClient, GetDefaultUpdatesDir())
    {
    }

    internal AppUpdateService(HttpClient httpClient, string updatesDir)
    {
        _httpClient = httpClient;
        _updatesDir = updatesDir;
    }

    public string CurrentVersion => GetCurrentVersion();

    public async Task<AppUpdateInfo> CheckLatestAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseLatestReleaseJson(json, CurrentVersion);
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (updateInfo.InstallerDownloadUrl is null)
            throw new InvalidOperationException("没有可下载的 EasyGet 安装包。");

        var updatesDir = _updatesDir;
        Directory.CreateDirectory(updatesDir);

        var fileName = string.IsNullOrWhiteSpace(updateInfo.InstallerFileName)
            ? $"EasyGet-Setup-v{updateInfo.LatestVersion}.exe"
            : updateInfo.InstallerFileName;
        var targetPath = Path.Combine(updatesDir, fileName);
        var tempPath = $"{targetPath}.download";

        using var response = await _httpClient.GetAsync(updateInfo.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? updateInfo.InstallerSize;
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
        }

        progress?.Report(100);

        if (File.Exists(targetPath))
            File.Delete(targetPath);
        File.Move(tempPath, targetPath);

        return targetPath;
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return false;

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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyGet-AppUpdater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
