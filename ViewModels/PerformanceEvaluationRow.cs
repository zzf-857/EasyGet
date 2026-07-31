namespace EasyGet.ViewModels;

public sealed record PerformanceEvaluationRow(
    string Metric,
    string CurrentValue,
    string ReferenceValue,
    string Rationale,
    string RiskLevel = "");
