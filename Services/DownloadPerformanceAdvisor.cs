using EasyGet.Models;

namespace EasyGet.Services;

internal enum DownloadPerformanceRisk
{
    Normal,
    Warning,
    Critical
}

internal sealed record DownloadPerformanceRecommendation(
    int LogicalProcessorCount,
    long MemoryBudgetBytes,
    int RecommendedFragments,
    int RecommendedConcurrentDownloads,
    int ProcessorFragmentBudget,
    int ProcessorConcurrentDownloadsBudget,
    int MemoryFragmentBudget,
    int MemoryConcurrentDownloadsBudget)
{
    internal int RecommendedPeakConnections
        => RecommendedFragments * RecommendedConcurrentDownloads;

    internal DownloadPerformanceRisk EvaluateConfiguredFragments(int configuredFragments)
        => DownloadPerformanceAdvisor.EvaluateRisk(
            Math.Clamp(
                configuredFragments,
                AppConfig.MinConcurrentFragments,
                AppConfig.MaxConcurrentFragments),
            RecommendedFragments);

    internal DownloadPerformanceRisk EvaluateConcurrentDownloads(int concurrentDownloads)
        => DownloadPerformanceAdvisor.EvaluateRisk(
            Math.Clamp(
                concurrentDownloads,
                AppConfig.MinConcurrentDownloadLimit,
                AppConfig.MaxConcurrentDownloadLimit),
            RecommendedConcurrentDownloads);

    internal DownloadPerformanceAssessment Assess(
        int configuredFragments,
        int concurrentDownloads)
    {
        var normalizedDownloads = Math.Clamp(
            concurrentDownloads,
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);
        var normalizedFragments = Math.Clamp(
            configuredFragments,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);
        var effectiveFragments = DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            normalizedFragments,
            normalizedDownloads);
        var recommendedEffectiveFragments = DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            RecommendedFragments,
            RecommendedConcurrentDownloads);
        var connectionRisk = DownloadPerformanceAdvisor.EvaluateRisk(
            effectiveFragments * normalizedDownloads,
            recommendedEffectiveFragments * RecommendedConcurrentDownloads);
        var downloadRisk = EvaluateConcurrentDownloads(normalizedDownloads);

        return new DownloadPerformanceAssessment(
            normalizedFragments,
            normalizedDownloads,
            effectiveFragments,
            effectiveFragments < normalizedFragments,
            connectionRisk,
            (DownloadPerformanceRisk)Math.Max((int)downloadRisk, (int)connectionRisk));
    }
}

internal sealed record DownloadPerformanceAssessment(
    int ConfiguredFragments,
    int ConcurrentDownloads,
    int EffectiveFragments,
    bool IsSmartLimited,
    DownloadPerformanceRisk ConnectionRisk,
    DownloadPerformanceRisk Risk)
{
    internal int CurrentPeakConnections
        => EffectiveFragments * ConcurrentDownloads;
}

internal static class DownloadPerformanceAdvisor
{
    private const long Gibibyte = 1024L * 1024L * 1024L;
    private const double WarningMultiplier = 1.75;

    internal static DownloadPerformanceRecommendation GetCurrentRecommendation()
        => CreateRecommendation(
            Environment.ProcessorCount,
            GetMemoryBudgetBytes());

    internal static DownloadPerformanceRecommendation CreateRecommendation(
        int logicalProcessorCount,
        long memoryBudgetBytes)
    {
        var processors = Math.Max(1, logicalProcessorCount);
        var normalizedMemory = Math.Max(0, memoryBudgetBytes);
        var cpuDownloadBudget = processors switch
        {
            <= 2 => 1,
            <= 4 => 2,
            <= 8 => 4,
            <= 12 => 6,
            <= 16 => 8,
            _ => 10
        };
        var memoryDownloadBudget = normalizedMemory switch
        {
            <= 0 => AppConfig.MaxConcurrentDownloadLimit,
            < 4 * Gibibyte => 1,
            < 8 * Gibibyte => 2,
            < 16 * Gibibyte => 6,
            < 24 * Gibibyte => 8,
            _ => 10
        };
        var cpuFragmentBudget = processors switch
        {
            <= 2 => 2,
            <= 4 => 4,
            _ => 8
        };
        var memoryFragmentBudget = normalizedMemory switch
        {
            <= 0 => 8,
            < 4 * Gibibyte => 2,
            < 8 * Gibibyte => 4,
            _ => 8
        };
        var recommendedDownloads = Math.Clamp(
            Math.Min(cpuDownloadBudget, memoryDownloadBudget),
            AppConfig.MinConcurrentDownloadLimit,
            AppConfig.MaxConcurrentDownloadLimit);
        var recommendedFragments = DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            Math.Min(cpuFragmentBudget, memoryFragmentBudget),
            recommendedDownloads);

        return new DownloadPerformanceRecommendation(
            processors,
            normalizedMemory,
            Math.Clamp(
                recommendedFragments,
                AppConfig.MinConcurrentFragments,
                AppConfig.MaxConcurrentFragments),
            recommendedDownloads,
            cpuFragmentBudget,
            cpuDownloadBudget,
            memoryFragmentBudget,
            memoryDownloadBudget);
    }

    internal static DownloadPerformanceRisk EvaluateRisk(int value, int recommendedValue)
    {
        var normalizedRecommendation = Math.Max(1, recommendedValue);
        if (value <= normalizedRecommendation)
            return DownloadPerformanceRisk.Normal;

        var warningUpperBound = GetWarningUpperBound(normalizedRecommendation);
        return value <= warningUpperBound
            ? DownloadPerformanceRisk.Warning
            : DownloadPerformanceRisk.Critical;
    }

    internal static int GetWarningUpperBound(int recommendedValue)
        => (int)Math.Ceiling(Math.Max(1, recommendedValue) * WarningMultiplier);

    private static long GetMemoryBudgetBytes()
    {
        try
        {
            return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
        catch
        {
            return 0;
        }
    }
}
