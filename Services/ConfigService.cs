using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EasyGet.Models;
using EasyGet.Services.Cookies;

namespace EasyGet.Services;

/// <summary>
/// JSON 配置文件管理服务
/// </summary>
public class ConfigService
{
    internal const string LegacyUnscopedCookieStorageKey = "legacy-unscoped";
    private static readonly string[] SupportedFormats = ["mp4", "mkv", "webm", "mp3", "m4a"];
    private static readonly string[] SupportedQualities = ["best", "2160", "1080", "720", "480"];
    private static readonly string[] SupportedSubtitles = ["none", "auto", "all"];
    private static readonly string[] SupportedDouyinModes = ["post", "like", "mix", "music", "collect", "collectmix"];
    private static readonly string[] SupportedDouyinAuthorDirectoryModes =
        ["nickname", "sec_uid", "nickname_uid", "user_sec_uid"];
    internal static readonly string[] SupportedDouyinTemplateVariableNames =
    [
        "id",
        "title",
        "author",
        "author_id",
        "date",
        "year",
        "month",
        "day",
        "time",
        "hour",
        "minute",
        "second",
        "timestamp",
        "type",
        "mode"
    ];
    private static readonly HashSet<string> SupportedDouyinTemplateVariables = new(
        SupportedDouyinTemplateVariableNames,
        StringComparer.Ordinal);
    private static readonly char[] UnsafeDouyinTemplateCharacters =
    [
        '/',
        '\\',
        ':',
        '*',
        '?',
        '"',
        '<',
        '>',
        '|',
        '#'
    ];

    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet");

    private static readonly string ConfigDir = DefaultConfigDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private AppConfig _config = new();
    private readonly string _configDir;
    private readonly string _configFile;
    private readonly string _backupFile;
    private readonly string _lockFile;
    private readonly ISecretProtector _migrationProtector;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ConfigService()
        : this(DefaultConfigDir, new DpapiSecretProtector())
    {
    }

    internal ConfigService(string configDir)
        : this(configDir, new DpapiSecretProtector())
    {
    }

    internal ConfigService(string configDir, ISecretProtector migrationProtector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);
        ArgumentNullException.ThrowIfNull(migrationProtector);
        _configDir = configDir;
        _configFile = Path.Combine(_configDir, "config.json");
        _backupFile = Path.Combine(_configDir, "config.backup.json");
        _lockFile = Path.Combine(_configDir, ".config.lock");
        _migrationProtector = migrationProtector;
    }

    /// <summary>当前配置</summary>
    public AppConfig Config => _config;

    /// <summary>当前配置及关联应用数据的根目录。</summary>
    public string ConfigDirectory => _configDir;

    /// <summary>
    /// 加载配置（如果配置文件不存在则使用默认值）
    /// </summary>
    public async Task LoadAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var primary = await TryReadConfigAsync(_configFile, CancellationToken.None);
            var backup = await TryReadConfigAsync(_backupFile, CancellationToken.None);
            var selected = SelectConfigCandidate(primary, backup);
            _config = selected?.Config ?? new AppConfig();

            NormalizeRuntimeConfig(_config);

            if (string.IsNullOrWhiteSpace(_config.CookieContent))
            {
                try
                {
                    var quarantinedCookie = await new PlatformCookieVault(
                            _configDir,
                            _migrationProtector)
                        .LoadAsync(
                            LegacyUnscopedCookieStorageKey,
                            CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(quarantinedCookie))
                    {
                        _config.CookieContent = quarantinedCookie;
                        _config.LegacyCookiePlatform = "";
                    }
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or CryptographicException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConfigService] Encrypted legacy Cookie restore failed: {ex.Message}");
                }
            }

            TryEnsureDownloadDirectory(_config.DefaultDownloadPath);

            var recoveredFromBackup = backup is not null
                                      && ReferenceEquals(selected, backup)
                                      && !ReferenceEquals(primary, backup);
            var shouldUpgradePrimary = ReferenceEquals(selected, primary)
                                       && primary is not null
                                       && (!primary.HasExplicitVersion
                                           || primary.ExplicitVersion < AppConfig.CurrentConfigVersion)
                                       && string.IsNullOrWhiteSpace(_config.CookieContent);
            if ((recoveredFromBackup || shouldUpgradePrimary)
                && string.IsNullOrWhiteSpace(_config.CookieContent))
            {
                try
                {
                    Directory.CreateDirectory(_configDir);
                    await using var configLock = await AcquireConfigLockAsync(CancellationToken.None);
                    await PersistConfigFilesCoreAsync(
                        createBackupFromPrimary: shouldUpgradePrimary,
                        ensureBackup: false,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConfigService] Config recovery rewrite failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Load failed: {ex.Message}");
            _config = new AppConfig();
            NormalizeRuntimeConfig(_config);
            TryEnsureDownloadDirectory(_config.DefaultDownloadPath);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// 保存配置到文件
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeRuntimeConfig(_config);
            Directory.CreateDirectory(_configDir);
            await using var configLock = await AcquireConfigLockAsync(cancellationToken);

            var migrationPersistedConfig = false;
            if (!string.IsNullOrWhiteSpace(_config.CookieContent))
            {
                try
                {
                    var vault = new PlatformCookieVault(_configDir, _migrationProtector);
                    if (CookieFileSerializer.HasExplicitDomainRows(_config.CookieContent)
                        || !string.IsNullOrWhiteSpace(_config.LegacyCookiePlatform))
                    {
                        await CompleteLegacyCookieMigrationCoreAsync(
                            _config.LegacyCookiePlatform,
                            vault,
                            cancellationToken);
                        migrationPersistedConfig = true;
                    }
                    else
                    {
                        await vault.SaveAsync(
                            LegacyUnscopedCookieStorageKey,
                            _config.CookieContent,
                            cancellationToken);
                        _config.CookieContent = "";
                        _config.LegacyCookiePlatform = "";
                        _config.ConfigVersion = AppConfig.CurrentConfigVersion;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ConfigService] Cookie migration during save failed: {ex.Message}");
                    return false;
                }
            }

            if (!migrationPersistedConfig)
            {
                await PersistConfigFilesCoreAsync(
                    createBackupFromPrimary: true,
                    ensureBackup: false,
                    cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigService] Save failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task CompleteLegacyCookieMigrationAsync(
        string platformId,
        PlatformCookieVault vault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vault);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_config.CookieContent))
                return;

            Directory.CreateDirectory(_configDir);
            await using var configLock = await AcquireConfigLockAsync(cancellationToken);
            await CompleteLegacyCookieMigrationCoreAsync(
                platformId,
                vault,
                cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task CompleteLegacyCookieMigrationCoreAsync(
        string platformId,
        PlatformCookieVault vault,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_config.CookieContent))
            return;

        var originalContent = _config.CookieContent;
        var originalPlatform = _config.LegacyCookiePlatform;
        var originalVersion = _config.ConfigVersion;
        var originalJson = JsonSerializer.Serialize(_config, JsonOptions);
        var scopedContents = new List<(string StorageKey, string Content)>();
        if (CookieFileSerializer.HasExplicitDomainRows(originalContent))
        {
            foreach (var platform in MediaPlatformResolver.KnownPlatforms)
            {
                var targetHost = platform.CookieDomains.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(targetHost))
                    continue;

                var lines = CookieFileSerializer.BuildScopedLines(
                    originalContent,
                    platform,
                    targetHost);
                if (!lines.Skip(3).Any())
                    continue;

                scopedContents.Add((
                    platform.StorageKey,
                    string.Join(Environment.NewLine, lines)));
            }

            if (scopedContents.Count == 0)
            {
                throw new InvalidOperationException(
                    "Cookie 文件未包含任何受支持平台的域名，无法安全迁移。");
            }
        }
        else
        {
            CookieStorageKey.ValidatePlatformId(platformId);
            if (!string.Equals(originalPlatform, platformId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "旧版 Cookie 尚未明确绑定到当前平台，无法安全迁移。");
            }

            scopedContents.Add((platformId, originalContent));
        }

        byte[]? plaintext = null;
        byte[]? encrypted = null;
        try
        {
            foreach (var scopedContent in scopedContents)
            {
                await vault.SaveAsync(
                    scopedContent.StorageKey,
                    scopedContent.Content,
                    cancellationToken);
            }

            plaintext = Encoding.UTF8.GetBytes(originalJson);
            encrypted = _migrationProtector.Protect(plaintext);
            await WriteBytesAtomicallyAsync(
                Path.Combine(_configDir, "config.cookie-migration.backup.bin"),
                encrypted,
                restrictToCurrentUser: true,
                cancellationToken);

            _config.CookieContent = "";
            _config.LegacyCookiePlatform = "";
            _config.ConfigVersion = AppConfig.CurrentConfigVersion;
            await PersistConfigFilesCoreAsync(
                createBackupFromPrimary: true,
                ensureBackup: true,
                cancellationToken);
            try
            {
                await vault.DeleteAsync(
                    LegacyUnscopedCookieStorageKey,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ConfigService] Legacy Cookie quarantine cleanup failed: {ex.Message}");
            }
        }
        catch
        {
            _config.CookieContent = originalContent;
            _config.LegacyCookiePlatform = originalPlatform;
            _config.ConfigVersion = originalVersion;
            throw;
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    /// <summary>
    /// 获取 tools 目录（用于存放 yt-dlp / ffmpeg）
    /// </summary>
    public static string GetToolsDirectory()
    {
        var dir = Path.Combine(ConfigDir, "tools");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SerializeWithoutPlaintextCookie(AppConfig config)
    {
        var root = JsonSerializer.SerializeToNode(config, JsonOptions) as JsonObject
                   ?? throw new JsonException("The application config could not be serialized.");
        root["cookieContent"] = "";
        root["legacyCookiePlatform"] = "";
        return root.ToJsonString(JsonOptions);
    }

    private async Task PersistConfigFilesCoreAsync(
        bool createBackupFromPrimary,
        bool ensureBackup,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configJson = SerializeWithoutPlaintextCookie(_config);
        ValidateSerializedConfig(configJson);

        var primary = createBackupFromPrimary
            ? await TryReadConfigAsync(_configFile, cancellationToken)
            : null;
        if (primary is not null)
        {
            var existingBackup = await TryReadConfigAsync(_backupFile, cancellationToken);
            if (existingBackup is null
                || ReferenceEquals(
                    SelectConfigCandidate(primary, existingBackup),
                    primary))
            {
                var backupJson = SerializeWithoutPlaintextCookie(primary.Config);
                ValidateSerializedConfig(backupJson);
                await WriteTextAtomicallyAsync(_backupFile, backupJson, cancellationToken);
            }
        }
        else if (ensureBackup
                 && await TryReadConfigAsync(_backupFile, cancellationToken) is null)
        {
            await WriteTextAtomicallyAsync(_backupFile, configJson, cancellationToken);
        }

        await WriteTextAtomicallyAsync(_configFile, configJson, cancellationToken);
    }

    private static void ValidateSerializedConfig(string json)
    {
        if (JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) is null)
            throw new JsonException("The serialized application config is empty.");
    }

    private static async Task<ConfigCandidate?> TryReadConfigAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config is null)
                return null;

            var hasExplicitVersion = false;
            var explicitVersion = 0;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(
                            property.Name,
                            "configVersion",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    hasExplicitVersion = true;
                    if (property.Value.ValueKind == JsonValueKind.Number)
                        property.Value.TryGetInt32(out explicitVersion);
                    else if (property.Value.ValueKind == JsonValueKind.String)
                        int.TryParse(property.Value.GetString(), out explicitVersion);
                    break;
                }
            }

            return new ConfigCandidate(config, hasExplicitVersion, explicitVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or JsonException
                                   or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ConfigService] Ignoring unreadable config '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    private static ConfigCandidate? SelectConfigCandidate(
        ConfigCandidate? primary,
        ConfigCandidate? backup)
    {
        if (primary is null)
            return backup;
        if (backup is null)
            return primary;
        if (backup.HasExplicitVersion
            && (!primary.HasExplicitVersion
                || backup.ExplicitVersion > primary.ExplicitVersion))
        {
            return backup;
        }

        return primary;
    }

    private async Task<FileStream> AcquireConfigLockAsync(
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        Directory.CreateDirectory(_configDir);
        IOException? lastError = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt + 1 < maxAttempts)
                    await Task.Delay(50, cancellationToken);
            }
        }

        throw new IOException("Timed out while waiting to save the application config.", lastError);
    }

    private static Task WriteTextAtomicallyAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
        => WriteBytesAtomicallyAsync(
            destinationPath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content),
            restrictToCurrentUser: false,
            cancellationToken);

    private static async Task WriteBytesAtomicallyAsync(
        string destinationPath,
        byte[] content,
        bool restrictToCurrentUser,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new ArgumentException(
                            "A destination directory is required.",
                            nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (restrictToCurrentUser)
                CookieFilePermissions.RestrictToCurrentUser(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
        }
    }

    private static void TryEnsureDownloadDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ConfigService] Download directory is currently unavailable: {ex.Message}");
        }
    }

    private sealed class ConfigCandidate(
        AppConfig config,
        bool hasExplicitVersion,
        int explicitVersion)
    {
        public AppConfig Config { get; } = config;
        public bool HasExplicitVersion { get; } = hasExplicitVersion;
        public int ExplicitVersion { get; } = explicitVersion;
    }

    internal static void NormalizeRuntimeConfig(AppConfig config)
    {
        NormalizeDownloadOptions(config);

        config.ConcurrentFragments = Math.Clamp(
            config.ConcurrentFragments,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);

        config.MaxConcurrentDownloads = Math.Clamp(
            config.MaxConcurrentDownloads,
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);

        NormalizeWindowState(config);
    }

    private static void NormalizeDownloadOptions(AppConfig config)
    {
        var defaults = new AppConfig();

        config.DefaultDownloadPath = string.IsNullOrWhiteSpace(config.DefaultDownloadPath)
            ? defaults.DefaultDownloadPath
            : config.DefaultDownloadPath.Trim();

        config.DefaultFormat = NormalizeOption(config.DefaultFormat, SupportedFormats, defaults.DefaultFormat);
        config.DefaultQuality = NormalizeOption(config.DefaultQuality, SupportedQualities, defaults.DefaultQuality);
        config.DefaultSubtitle = NormalizeOption(config.DefaultSubtitle, SupportedSubtitles, defaults.DefaultSubtitle);
        config.ProxyAddress = config.ProxyAddress?.Trim() ?? "";
        config.CookieContent ??= "";
        config.ConfigVersion = config.ConfigVersion is > 0 and <= AppConfig.CurrentConfigVersion
            ? config.ConfigVersion
            : AppConfig.CurrentConfigVersion;
        config.LegacyCookiePlatform = NormalizeLegacyCookiePlatform(
            config.LegacyCookiePlatform);
        config.DouyinMode = NormalizeDouyinMode(config.DouyinMode, defaults.DouyinMode);
        config.DouyinLimit = Math.Max(0, config.DouyinLimit);
        config.DouyinMaxComments = Math.Max(0, config.DouyinMaxComments);
        config.DouyinCommentPageSize = Math.Clamp(
            config.DouyinCommentPageSize,
            1,
            AppConfig.MaxDouyinCommentPageSize);
        config.DouyinLiveMaxDurationSeconds = Math.Max(0, config.DouyinLiveMaxDurationSeconds);
        config.DouyinLiveChunkSize = config.DouyinLiveChunkSize > 0
            ? config.DouyinLiveChunkSize
            : AppConfig.DefaultDouyinLiveChunkSize;
        config.DouyinLiveIdleTimeoutSeconds = config.DouyinLiveIdleTimeoutSeconds > 0
            ? config.DouyinLiveIdleTimeoutSeconds
            : AppConfig.DefaultDouyinLiveIdleTimeoutSeconds;
        config.DouyinFilenameTemplate = NormalizeDouyinTemplate(config.DouyinFilenameTemplate);
        config.DouyinFolderTemplate = NormalizeDouyinTemplate(config.DouyinFolderTemplate);
        config.DouyinAuthorDirectoryMode = NormalizeDouyinAuthorDirectoryMode(
            config.DouyinAuthorDirectoryMode,
            defaults.DouyinAuthorDirectoryMode);
        config.DouyinStartTime = config.DouyinStartTime?.Trim() ?? "";
        config.DouyinEndTime = config.DouyinEndTime?.Trim() ?? "";

        config.ThemeColor = string.IsNullOrWhiteSpace(config.ThemeColor)
            ? "Indigo"
            : config.ThemeColor.Trim();

        if (!ThemeManager.Palettes.Any(p => p.Name.Equals(config.ThemeColor, StringComparison.OrdinalIgnoreCase)))
        {
            config.ThemeColor = "Indigo";
        }
    }

    private static string NormalizeOption(string? value, string[] supportedValues, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var candidate = value.Trim();
        foreach (var supported in supportedValues)
        {
            if (string.Equals(candidate, supported, StringComparison.OrdinalIgnoreCase))
                return supported;
        }

        return defaultValue;
    }

    private static string NormalizeLegacyCookiePlatform(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant() ?? "";
        if (candidate.Length == 0)
            return "";

        try
        {
            CookieStorageKey.ValidatePlatformId(candidate);
            return candidate;
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    internal static string NormalizeDouyinMode(string? value, string defaultValue = "post")
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var normalizedModes = new List<string>();
        foreach (var rawMode in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var mode = SupportedDouyinModes.FirstOrDefault(
                supported => string.Equals(rawMode, supported, StringComparison.OrdinalIgnoreCase));
            if (mode is null)
                return defaultValue;
            if (!normalizedModes.Contains(mode, StringComparer.Ordinal))
                normalizedModes.Add(mode);
        }

        if (normalizedModes.Count == 0)
            return defaultValue;

        var containsCollectionMode = normalizedModes.Any(mode => mode is "collect" or "collectmix");
        if (containsCollectionMode && normalizedModes.Count > 1)
            return defaultValue;

        return string.Join(",", normalizedModes);
    }

    internal static string NormalizeDouyinAuthorDirectoryMode(string? value, string defaultValue = "nickname")
        => NormalizeOption(value, SupportedDouyinAuthorDirectoryModes, defaultValue);

    internal static string NormalizeDouyinTemplate(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return AppConfig.DefaultDouyinTemplate;

        if (candidate.Length > 200
            || candidate.IndexOfAny(UnsafeDouyinTemplateCharacters) >= 0
            || candidate.Any(char.IsControl)
            || candidate.Contains("..", StringComparison.Ordinal))
        {
            return AppConfig.DefaultDouyinTemplate;
        }

        var hasId = false;
        for (var index = 0; index < candidate.Length;)
        {
            var current = candidate[index];
            if (current == '}')
                return AppConfig.DefaultDouyinTemplate;

            if (current != '{')
            {
                index++;
                continue;
            }

            var closeIndex = candidate.IndexOf('}', index + 1);
            if (closeIndex < 0)
                return AppConfig.DefaultDouyinTemplate;

            var variable = candidate.Substring(index + 1, closeIndex - index - 1);
            if (variable.Length == 0
                || variable.Contains('{', StringComparison.Ordinal)
                || variable.Contains('}', StringComparison.Ordinal)
                || !SupportedDouyinTemplateVariables.Contains(variable))
            {
                return AppConfig.DefaultDouyinTemplate;
            }

            hasId |= string.Equals(variable, "id", StringComparison.Ordinal);
            index = closeIndex + 1;
        }

        return hasId ? candidate : AppConfig.DefaultDouyinTemplate;
    }

    private static void NormalizeWindowState(AppConfig config)
    {
        config.Window ??= new WindowState();

        if (!double.IsFinite(config.Window.Left))
            config.Window.Left = double.NaN;

        if (!double.IsFinite(config.Window.Top))
            config.Window.Top = double.NaN;

        config.Window.Width = NormalizeWindowLength(
            config.Window.Width,
            WindowState.MinWidth,
            WindowState.DefaultWidth);

        config.Window.Height = NormalizeWindowLength(
            config.Window.Height,
            WindowState.MinHeight,
            WindowState.DefaultHeight);
    }

    private static double NormalizeWindowLength(double value, double minValue, double defaultValue)
    {
        if (!double.IsFinite(value) || value <= 0)
            return defaultValue;

        return Math.Max(minValue, value);
    }
}
