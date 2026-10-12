using Fraud.Api.Data;
using Fraud.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;

namespace Fraud.Api.Services;

public sealed class ModelEvaluationService(
    FraudDbContext db,
    IConfiguration configuration,
    ILogger<ModelEvaluationService> logger)
{
    public async Task<ModelEvaluationResponse> EvaluateActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = await db.ModelVersions.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (active is null)
            throw new InvalidOperationException("No active model version exists. Train a model before running evaluation.");

        var datasetPath = ProjectPaths.Resolve(configuration["ML:DatasetPath"] ?? "data/raw/creditcard.csv");
        if (!File.Exists(datasetPath))
            throw new FileNotFoundException($"Evaluation dataset is missing at '{datasetPath}'. Add the original ULB creditcard.csv file.", datasetPath);

        var modelsDirectory = ProjectPaths.Resolve(configuration["ML:ModelsDirectory"] ?? "artifacts/models");
        var modelPath = Path.Combine(modelsDirectory, active.ArtifactFileName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Active model artifact '{active.ArtifactFileName}' was not found in '{modelsDirectory}'. Retrain to restore the artifact.", modelPath);

        var baselineRows = await Task.Run(() => TrainingDataReader.ReadUlbCsv(datasetPath), cancellationToken);
        var split = ModelTrainer.SplitChronologically(baselineRows);
        if (!split.Validation.Any(x => x.Label) || !split.Test.Any(x => x.Label))
            throw new InvalidDataException("The chronological validation and test partitions must each contain fraud cases.");

        var ml = new MLContext(seed: ModelTrainer.Seed);
        var model = ml.Model.Load(modelPath, out _);
        var validationView = ml.Data.LoadFromEnumerable(split.Validation);
        var testView = ml.Data.LoadFromEnumerable(split.Test);
        var validationScored = ml.Data.CreateEnumerable<ScoredTrainingExample>(
            model.Transform(validationView), reuseRowObject: false).ToArray();
        var testScoredView = model.Transform(testView);
        var testMetrics = ml.BinaryClassification.Evaluate(testScoredView,
            labelColumnName: nameof(TrainingExample.Label),
            scoreColumnName: nameof(FraudModelPrediction.Score),
            probabilityColumnName: nameof(FraudModelPrediction.Probability));
        var testScored = ml.Data.CreateEnumerable<ScoredTrainingExample>(testScoredView, reuseRowObject: false).ToArray();
        var threshold = Math.Clamp(active.Threshold, 0.01, 0.99);
        var thresholdAnalysis = new[] { 0.05, 0.10, 0.20, threshold, 0.33, 0.50, 0.75, 0.90 }
            .Distinct().OrderBy(x => x)
            .Select(x => ClassificationMetrics.Calculate(validationScored, x))
            .Select(x => new EvaluationThresholdPoint(x.Threshold, x.Precision, x.Recall, x.F1, x.PredictedPositive))
            .ToArray();
        var prAuc = double.IsFinite(testMetrics.AreaUnderPrecisionRecallCurve)
            ? testMetrics.AreaUnderPrecisionRecallCurve : 0d;
        logger.LogInformation("Evaluated active model {Version} on {TestCount} untouched chronological test rows", active.Version, split.Test.Count);

        return new ModelEvaluationResponse(
            active.Version, DateTimeOffset.UtcNow,
            split.Validation.Count, split.Validation.Count(x => x.Label),
            split.Test.Count, split.Test.Count(x => x.Label), prAuc,
            ClassificationMetrics.Calculate(testScored, threshold), thresholdAnalysis,
            "Active threshold is evaluated on the held-out test partition; threshold comparisons use validation data only.",
            "This evaluates the dataset's chronological holdout. It is not live production performance; reviewed transactions are not treated as benchmark ground truth.");
    }
}
