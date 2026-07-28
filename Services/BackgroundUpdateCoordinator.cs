using EasyGet.Models;
using System.IO;

namespace EasyGet.Services;

public sealed class BackgroundUpdateCoordinator
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly IAppUpdateService _updateService;
    private readonly ConfigService _configService;
    private readonly Func<DateTimeOffset> _utcNow;

    public BackgroundUpdateCoordinator(
        IAppUpdateService updateService,
        ConfigService configService)
        : this(updateService, configService, () => DateTimeOffset.UtcNow)
    {
    }

    internal BackgroundUpdateCoordinator(
        IAppUpdateService updateService,
        ConfigService configService,
        Func<DateTimeOffset> utcNow)
    {
        _updateService = updateService;
        _configService = configService;
        _utcNow = utcNow;
    }

    public async Task<AppUpdateInfo?> CheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Config;
        var now = _utcNow().ToUniversalTime();
        if (!config.AutomaticUpdateChecksEnabled
            || !IsDue(config.LastAutomaticUpdateCheckUtc, now))
        {
            return null;
        }

        CleanupStalePackages(
            Path.Combine(_configService.ConfigDirectory, "updates"),
            now);

        try
        {
            return await _updateService.CheckLatestAsync(cancellationToken);
        }
        finally
        {
            config.LastAutomaticUpdateCheckUtc = now;
            _ = await _configService.SaveAsync(cancellationToken);
        }
    }

    internal static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc)
        => lastCheckUtc is null || nowUtc - lastCheckUtc.Value.ToUniversalTime() >= CheckInterval;

    internal static int CleanupStalePackages(string updatesDirectory, DateTimeOffset nowUtc)
    {
        if (!Directory.Exists(updatesDirectory))
            return 0;

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(updatesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = new FileInfo(path);
                var age = nowUtc.UtcDateTime - file.LastWriteTimeUtc;
                var shouldDelete = file.Name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
                    ? age >= TimeSpan.FromDays(1)
                    : file.Name.StartsWith("EasyGet-Setup-v", StringComparison.OrdinalIgnoreCase)
                      && file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                      && age >= TimeSpan.FromDays(30);
                if (!shouldDelete)
                    continue;

                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
            }
        }

        return deleted;
    }
}
