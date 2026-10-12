using System.ComponentModel.DataAnnotations;
using Microsoft.ML.Data;

namespace Fraud.Api.Models;

public sealed class RegisterRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
    [Required, MinLength(12), MaxLength(128)] public string Password { get; init; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required, EmailAddress] public string Email { get; init; } = string.Empty;
    [Required] public string Password { get; init; } = string.Empty;
}

public sealed class ScoreTransactionRequest
{
    [MaxLength(100)] public string? ExternalId { get; init; }
    [Range(0, 100000000)] public decimal Amount { get; init; }
    // ULB benchmark convention: seconds from the dataset start, not a wall-clock timestamp.
    [Range(0, 100000000)] public double Time { get; init; }
    [Required, MinLength(28), MaxLength(28)] public float[] V { get; init; } = Array.Empty<float>();
}

public sealed class ReviewRequest
{
    public bool IsFraud { get; init; }
    [MaxLength(2000)] public string? Note { get; init; }
}

public sealed class ThresholdRequest
{
    [Range(0.01, 0.99)] public double Threshold { get; init; }
}

public sealed class TrainingExample
{
    [VectorType(30)] public float[] Features { get; init; } = new float[30];
    [ColumnName("Label")] public bool Label { get; init; }
}


public sealed class WeightedTrainingExample
{
    [VectorType(30)] public float[] Features { get; init; } = new float[30];
    [ColumnName("Label")] public bool Label { get; init; }
    [ColumnName("ExampleWeight")] public float ExampleWeight { get; init; } = 1f;
}

public sealed class FraudModelInput
{
    [VectorType(30)] public float[] Features { get; set; } = new float[30];
    // The fit schema also includes the sample-weight column; inference uses the neutral weight.
    [ColumnName("ExampleWeight")] public float ExampleWeight { get; set; } = 1f;
}

public sealed class FraudModelPrediction
{
    [ColumnName("PredictedLabel")] public bool PredictedLabel { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}

public sealed class ScoredTrainingExample
{
    [ColumnName("Label")] public bool Label { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}

public sealed record FeatureSensitivity(string Name, float ProbabilityDelta);
public sealed record ModelPredictionResult(float Probability, bool IsFlagged, string ModelVersion,
    IReadOnlyList<FeatureSensitivity> Explanations);

public sealed record AuthResponse(string Token, string Email, string[] Roles, DateTimeOffset ExpiresAtUtc);

public sealed record TransactionListItem(Guid Id, string ExternalId, decimal Amount, DateTimeOffset OccurredAt,
    float RiskScore, bool IsFlagged, string ReviewStatus, string ModelVersion, DateTimeOffset CreatedAt,
    IReadOnlyList<FeatureSensitivity> Explanations, string ExplanationMethod);

public sealed record TransactionPage(IReadOnlyList<TransactionListItem> Items, int Page, int PageSize, int Total);

public sealed record DashboardSummary(int TotalScored, int Flagged, int PendingReview, int Reviewed,
    double AverageRisk, string? ActiveModelVersion, double? PrAuc, double? Precision, double? Recall,
    double? Threshold, bool ModelLoaded);

public sealed record ModelHealthResponse(bool ModelLoaded, string? ActiveVersion, double? Threshold,
    double? PrAuc, double? Precision, double? Recall, int ScoredLast24Hours,
    double? MeanAbsoluteFeatureShift, double? MaxAbsoluteFeatureShift, bool DriftAlert,
    string DriftMethod, string DatasetStatus, DateTimeOffset? ModelCreatedAt);


public sealed record ConfusionMatrixCounts(int TruePositive, int FalsePositive, int TrueNegative, int FalseNegative);

public sealed record BinaryThresholdMetrics(
    double Threshold, double Precision, double Recall, double F1, double Accuracy, double FalsePositiveRate,
    int TruePositive, int FalsePositive, int TrueNegative, int FalseNegative, int PredictedPositive, int Total);

public sealed record EvaluationThresholdPoint(double Threshold, double Precision, double Recall, double F1, int PredictedPositive);

public sealed record ModelEvaluationResponse(
    string Version, DateTimeOffset EvaluatedAt, int ValidationCount, int ValidationFraudCount,
    int TestCount, int TestFraudCount, double PrAuc, BinaryThresholdMetrics TestMetrics,
    IReadOnlyList<EvaluationThresholdPoint> ValidationThresholdAnalysis,
    string ThresholdMethod, string TestSetCaveat);

public sealed record ReviewAuditItem(
    long Id, Guid TransactionId, string ExternalId, decimal Amount, float RiskScore, string ModelVersion,
    bool IsFraud, string ReviewerEmail, string? Note, DateTimeOffset CreatedAt);

public sealed record MonitoringBucket(string Window, int Scored, int Flagged, double AverageRisk);

public sealed record ModelMonitoringResponse(
    DateTimeOffset GeneratedAt, int ScoredLast24Hours, int FlaggedLast24Hours, int PendingReview,
    int ReviewedLast24Hours, double FlagRate, int LowRiskCount, int MediumRiskCount, int HighRiskCount,
    IReadOnlyList<MonitoringBucket> Timeline, string TimelineNote, bool DriftAlert,
    double? MeanFeatureShift, double? MaxFeatureShift, string DriftMethod);
