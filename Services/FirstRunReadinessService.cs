namespace EasyGet.Services;

public sealed record FirstRunReadinessReport(
    EnvironmentStatus Environment,
    DownloadPreflightResult DownloadDirectory,
    IReadOnlyList<string> MissingTools)
{
    public bool IsReady => Environment.IsReady && DownloadDirectory.CanProceed;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (MissingTools.Count > 0)
                parts.Add($"缺少组件：{string.Join("、", MissingTools)}");
            if (!DownloadDirectory.CanProceed)
                parts.Add(DownloadDirectory.BlockingMessage);
            return parts.Count == 0 ? "运行环境和下载目录已就绪。" : string.Join(System.Environment.NewLine, parts);
        }
    }
}

public sealed class FirstRunReadinessService
{
    private readonly EnvironmentService _environmentService;
    private readonly DownloadPreflightService _preflightService;

    public FirstRunReadinessService(
        EnvironmentService environmentService,
        DownloadPreflightService preflightService)
    {
        _environmentService = environmentService;
        _preflightService = preflightService;
    }

    public async Task<FirstRunReadinessReport> CheckAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var environment = await _environmentService.CheckEnvironmentAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _preflightService.Check(outputDirectory);
        var missingTools = EnvironmentService.GetMissingToolNames(environment);
        return new FirstRunReadinessReport(environment, directory, missingTools);
    }
}
