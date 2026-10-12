namespace Fraud.Api.Models;

public sealed class TransactionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalId { get; set; } = Guid.NewGuid().ToString("N");
    public decimal Amount { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public double TimeFeature { get; set; }
    // Stored as a JSON float array in the same Time,V1..V28,Amount order used by the model.
    public string FeaturesJson { get; set; } = "[]";
    public string ExplanationsJson { get; set; } = "[]";
    public float RiskScore { get; set; }
    public bool IsFlagged { get; set; }
    public string ReviewStatus { get; set; } = "Pending";
    public string ModelVersion { get; set; } = "unavailable";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ReviewDecision> Reviews { get; set; } = new List<ReviewDecision>();
}

public sealed class ReviewDecision
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public TransactionRecord? Transaction { get; set; }
    public string ReviewerId { get; set; } = string.Empty;
    public string ReviewerEmail { get; set; } = string.Empty;
    public bool IsFraud { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TrainingFeedback
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public string FeaturesJson { get; set; } = "[]";
    public bool IsFraud { get; set; }
    public string ReviewerId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModelVersionEntity
{
    public long Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string ArtifactFileName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public double PrAuc { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double Threshold { get; set; }
    public int TrainCount { get; set; }
    public int ValidationCount { get; set; }
    public int TestCount { get; set; }
    public int FraudTestCount { get; set; }
    public string FeatureMeansJson { get; set; } = "[]";
    public string FeatureStdDevsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
