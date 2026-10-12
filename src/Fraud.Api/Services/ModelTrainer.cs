using System.Text.Json;
using Fraud.Api.Models;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;

namespace Fraud.Api.Services;

public sealed record TrainingMetrics(
    double PrAuc,
    double Precision,
    double Recall,
    double Threshold,
    int TrainCount,
    int ValidationCount,
    int TestCount,
    int FraudTestCount,
    string FeatureMeansJson,
    string FeatureStdDevsJson);

public sealed record TrainingResult(ITransformer Model, DataViewSchema TrainingSchema, TrainingMetrics Metrics);
public sealed record ChronologicalDataSplit(IReadOnlyList<TrainingExample> Train, IReadOnlyList<TrainingExample> Validation, IReadOnlyList<TrainingExample> Test);

public sealed class ModelTrainer
{
    public const int Seed = 1729;

    public TrainingResult Train(IReadOnlyList<TrainingExample> baselineRows,
        IReadOnlyList<TrainingExample> reviewedFeedback, string modelPath)
    {
        if (baselineRows.Count < 1000)
            throw new ArgumentException("At least 1,000 baseline rows are required.", nameof(baselineRows));
        if (baselineRows.Count(x => x.Label) < 20)
            throw new ArgumentException("At least 20 baseline fraud examples are required.", nameof(baselineRows));

        // Dataset order follows transaction time. Do not randomly shuffle across time.
        var split = SplitChronologically(baselineRows);
        var baseTrain = split.Train.ToList();
        var validation = split.Validation.ToList();
        var test = split.Test.ToList();
        if (validation.Count == 0 || test.Count == 0 || !validation.Any(x => x.Label) || !test.Any(x => x.Label))
            throw new InvalidDataException("Chronological validation and test slices must each contain fraud examples. Check the source labels and order.");

        // Human review labels enrich the training partition only. They never leak into validation/test.
        var trainingRows = baseTrain.Concat(reviewedFeedback).ToList();
        var positives = Math.Max(1, trainingRows.Count(x => x.Label));
        var negatives = Math.Max(1, trainingRows.Count - positives);
        // Equalize aggregate class weight so the rare positive class can influence the tree fit.
        // Threshold selection still happens on the untouched chronological validation slice.
        var weightedTrainingRows = trainingRows.Select(x => new WeightedTrainingExample
        {
            Features = x.Features,
            Label = x.Label,
            ExampleWeight = x.Label ? (float)negatives / positives : 1f
        }).ToList();
        var ml = new MLContext(seed: Seed);
        var trainingView = ml.Data.LoadFromEnumerable(weightedTrainingRows);
        var validationView = ml.Data.LoadFromEnumerable(validation);
        var testView = ml.Data.LoadFromEnumerable(test);

        var trainer = ml.BinaryClassification.Trainers.FastTree(
            labelColumnName: nameof(WeightedTrainingExample.Label),
            featureColumnName: nameof(WeightedTrainingExample.Features),
            exampleWeightColumnName: nameof(WeightedTrainingExample.ExampleWeight),
            numberOfLeaves: 32,
            numberOfTrees: 160,
            minimumExampleCountPerLeaf: 20,
            learningRate: 0.15);

        var model = trainer.Fit(trainingView);
        var validationScores = ml.Data.CreateEnumerable<ScoredTrainingExample>(
            model.Transform(validationView), reuseRowObject: false).ToArray();
        var threshold = ChooseF1Threshold(validationScores);

        var testScoredView = model.Transform(testView);
        var prAucMetrics = ml.BinaryClassification.Evaluate(testScoredView,
            labelColumnName: nameof(TrainingExample.Label),
            scoreColumnName: nameof(FraudModelPrediction.Score),
            probabilityColumnName: nameof(FraudModelPrediction.Probability));
        var testScores = ml.Data.CreateEnumerable<ScoredTrainingExample>(testScoredView, reuseRowObject: false).ToArray();
        var (precision, recall) = PrecisionRecall(testScores, threshold);
        var (means, stdDevs) = FeatureStats(baseTrain);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);
        ml.Model.Save(model, trainingView.Schema, modelPath);

        var metrics = new TrainingMetrics(
            PrAuc: double.IsFinite(prAucMetrics.AreaUnderPrecisionRecallCurve) ? prAucMetrics.AreaUnderPrecisionRecallCurve : 0,
            Precision: precision,
            Recall: recall,
            Threshold: threshold,
            TrainCount: trainingRows.Count,
            ValidationCount: validation.Count,
            TestCount: test.Count,
            FraudTestCount: test.Count(x => x.Label),
            FeatureMeansJson: JsonSerializer.Serialize(means),
            FeatureStdDevsJson: JsonSerializer.Serialize(stdDevs));
        return new TrainingResult(model, trainingView.Schema, metrics);
    }

    public static ChronologicalDataSplit SplitChronologically(IReadOnlyList<TrainingExample> rows)
    {
        if (rows.Count < 3) throw new ArgumentException("At least three rows are required for a chronological split.", nameof(rows));
        var trainEnd = Math.Clamp((int)(rows.Count * 0.70), 1, rows.Count - 2);
        var validationEnd = Math.Clamp((int)(rows.Count * 0.85), trainEnd + 1, rows.Count - 1);
        return new ChronologicalDataSplit(
            rows.Take(trainEnd).ToArray(),
            rows.Skip(trainEnd).Take(validationEnd - trainEnd).ToArray(),
            rows.Skip(validationEnd).ToArray());
    }

    public static double ChooseF1Threshold(IReadOnlyList<ScoredTrainingExample> rows)
    {
        if (rows.Count == 0 || !rows.Any(x => x.Label)) return 0.5;
        var candidates = Enumerable.Range(1, 99).Select(i => i / 100d)
            .Concat(rows.Select(x => (double)x.Probability).Where(double.IsFinite))
            .Distinct().Where(x => x is >= 0.01 and <= 0.99).OrderBy(x => x).ToArray();
        var bestThreshold = 0.5;
        var bestF1 = -1d;
        var bestPrecision = -1d;
        foreach (var threshold in candidates)
        {
            var (precision, recall) = PrecisionRecall(rows, threshold);
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            if (f1 > bestF1 || (Math.Abs(f1 - bestF1) < 1e-9 && precision > bestPrecision))
            {
                bestF1 = f1;
                bestPrecision = precision;
                bestThreshold = threshold;
            }
        }
        return bestThreshold;
    }

    public static (double Precision, double Recall) PrecisionRecall(IReadOnlyList<ScoredTrainingExample> rows, double threshold)
    {
        var truePositives = rows.Count(x => x.Label && x.Probability >= threshold);
        var falsePositives = rows.Count(x => !x.Label && x.Probability >= threshold);
        var falseNegatives = rows.Count(x => x.Label && x.Probability < threshold);
        var precision = truePositives + falsePositives == 0 ? 0 : (double)truePositives / (truePositives + falsePositives);
        var recall = truePositives + falseNegatives == 0 ? 0 : (double)truePositives / (truePositives + falseNegatives);
        return (precision, recall);
    }

    private static (float[] Means, float[] StandardDeviations) FeatureStats(IReadOnlyList<TrainingExample> rows)
    {
        var means = new float[30];
        var stdDevs = new float[30];
        for (var j = 0; j < 30; j++)
        {
            var values = rows.Select(x => x.Features[j]).ToArray();
            var mean = values.Average(v => (double)v);
            means[j] = (float)mean;
            var variance = values.Average(v => Math.Pow(v - mean, 2));
            stdDevs[j] = (float)Math.Sqrt(variance);
        }
        return (means, stdDevs);
    }
}
