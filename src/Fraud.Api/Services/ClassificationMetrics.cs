using Fraud.Api.Models;

namespace Fraud.Api.Services;

/// <summary>Pure, deterministic threshold metrics for the binary fraud classifier.</summary>
public static class ClassificationMetrics
{
    public static BinaryThresholdMetrics Calculate(IReadOnlyList<ScoredTrainingExample> rows, double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        var tp = rows.Count(x => x.Label && double.IsFinite(x.Probability) && x.Probability >= threshold);
        var fp = rows.Count(x => !x.Label && double.IsFinite(x.Probability) && x.Probability >= threshold);
        var fn = rows.Count(x => x.Label && (!double.IsFinite(x.Probability) || x.Probability < threshold));
        var tn = rows.Count(x => !x.Label && (!double.IsFinite(x.Probability) || x.Probability < threshold));
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2d * precision * recall / (precision + recall);
        var accuracy = rows.Count == 0 ? 0d : (double)(tp + tn) / rows.Count;
        var fpr = fp + tn == 0 ? 0d : (double)fp / (fp + tn);
        return new BinaryThresholdMetrics(threshold, precision, recall, f1, accuracy, fpr,
            tp, fp, tn, fn, tp + fp, rows.Count);
    }
}
