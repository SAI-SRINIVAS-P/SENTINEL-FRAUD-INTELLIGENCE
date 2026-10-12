using System.Text.Json;
using Fraud.Api.Models;
using Fraud.Api.Services;

namespace Fraud.Api.Tests;

public sealed class ModelTrainerTests
{
    [Fact]
    public void PrecisionRecall_UsesConfiguredProbabilityThreshold()
    {
        var rows = new[]
        {
            new ScoredTrainingExample { Label = true, Probability = 0.9f },
            new ScoredTrainingExample { Label = true, Probability = 0.6f },
            new ScoredTrainingExample { Label = false, Probability = 0.8f },
            new ScoredTrainingExample { Label = false, Probability = 0.1f }
        };

        var (precision, recall) = ModelTrainer.PrecisionRecall(rows, threshold: 0.7);

        Assert.Equal(0.5, precision, precision: 8);
        Assert.Equal(0.5, recall, precision: 8);
    }

    [Fact]
    public void ClassificationMetrics_CalculateConfusionMatrixAndF1()
    {
        var rows = new[]
        {
            new ScoredTrainingExample { Label = true, Probability = 0.9f },
            new ScoredTrainingExample { Label = true, Probability = 0.2f },
            new ScoredTrainingExample { Label = false, Probability = 0.8f },
            new ScoredTrainingExample { Label = false, Probability = 0.1f }
        };

        var metrics = ClassificationMetrics.Calculate(rows, 0.5);

        Assert.Equal(1, metrics.TruePositive);
        Assert.Equal(1, metrics.FalseNegative);
        Assert.Equal(1, metrics.FalsePositive);
        Assert.Equal(1, metrics.TrueNegative);
        Assert.Equal(0.5, metrics.Precision, precision: 8);
        Assert.Equal(0.5, metrics.Recall, precision: 8);
        Assert.Equal(0.5, metrics.F1, precision: 8);
        Assert.Equal(0.5, metrics.Accuracy, precision: 8);
    }

    [Fact]
    public void SplitChronologically_PreservesOrderWithoutOverlap()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new TrainingExample
        {
            Features = Enumerable.Repeat((float)i, 30).ToArray(),
            Label = i % 10 == 0
        }).ToArray();

        var split = ModelTrainer.SplitChronologically(rows);

        Assert.Equal(70, split.Train.Count);
        Assert.Equal(15, split.Validation.Count);
        Assert.Equal(15, split.Test.Count);
        Assert.Equal(69f, split.Train[^1].Features[0]);
        Assert.Equal(70f, split.Validation[0].Features[0]);
        Assert.Equal(84f, split.Validation[^1].Features[0]);
        Assert.Equal(85f, split.Test[0].Features[0]);
        Assert.Equal(99f, split.Test[^1].Features[0]);
    }

    [Fact]
    public void ChooseF1Threshold_StaysInsideSupportedRange()
    {
        var rows = new[]
        {
            new ScoredTrainingExample { Label = true, Probability = 0.91f },
            new ScoredTrainingExample { Label = true, Probability = 0.77f },
            new ScoredTrainingExample { Label = false, Probability = 0.12f },
            new ScoredTrainingExample { Label = false, Probability = 0.08f }
        };

        var threshold = ModelTrainer.ChooseF1Threshold(rows);

        Assert.InRange(threshold, 0.01, 0.99);
    }

    [Fact]
    public void ReadUlbCsv_AcceptsBinaryLabelsWrittenAsDecimals()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fraud-csv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "creditcard.csv");
        try
        {
            string[] header = ["Time", .. Enumerable.Range(1, 28).Select(i => $"V{i}"), "Amount", "Class"];
            var lines = new List<string> { string.Join(",", header) };
            for (var i = 0; i < 1_000; i++)
            {
                var features = Enumerable.Repeat("0.0", 30).ToArray();
                features[0] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                features[29] = "42.50";
                var label = i < 25 ? "1.0" : "0.0";
                lines.Add(string.Join(",", features) + "," + label);
            }
            File.WriteAllLines(path, lines);

            var rows = TrainingDataReader.ReadUlbCsv(path);

            Assert.Equal(1_000, rows.Count);
            Assert.Equal(25, rows.Count(row => row.Label));
            Assert.Equal(30, rows[0].Features.Length);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadUlbCsv_ReportsActualInvalidClassLabel()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fraud-csv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "creditcard.csv");
        try
        {
            string[] header = ["Time", .. Enumerable.Range(1, 28).Select(i => $"V{i}"), "Amount", "Class"];
            var features = Enumerable.Repeat("0", 30).ToArray();
            File.WriteAllLines(path, new[] { string.Join(",", header), string.Join(",", features) + ",Fraud" });

            var error = Assert.Throws<InvalidDataException>(() => TrainingDataReader.ReadUlbCsv(path));
            Assert.Contains("Invalid Class label 'Fraud' at CSV line 2", error.Message);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FeedbackFeatureArray_RejectsIncorrectSchema()
    {
        var malformed = JsonSerializer.Serialize(Enumerable.Range(0, 29).Select(x => (float)x).ToArray());
        Assert.Throws<InvalidDataException>(() => TrainingDataReader.DeserializeFeatures(malformed));
    }

    [Fact]
    public void TrainingMetrics_AreRepeatableWithFixedSeed()
    {
        // Fixed synthetic fixture validates pipeline determinism only, not model quality on real fraud data.
        const int count = 1400;
        var random = new Random(20261012);
        var rows = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var label = i % 19 == 0 || i % 113 == 0;
            var features = new float[30];
            features[0] = i;
            for (var j = 1; j < 30; j++) features[j] = (float)(random.NextDouble() * 2 - 1);
            features[1] = label ? (float)(3.5 + random.NextDouble()) : (float)(random.NextDouble() * 1.5 - 1);
            rows.Add(new TrainingExample { Features = features, Label = label });
        }

        var temp = Path.Combine(Path.GetTempPath(), "fraud-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var trainer = new ModelTrainer();
            var first = trainer.Train(rows, Array.Empty<TrainingExample>(), Path.Combine(temp, "first.zip")).Metrics;
            var second = trainer.Train(rows, Array.Empty<TrainingExample>(), Path.Combine(temp, "second.zip")).Metrics;

            Assert.Equal(first.PrAuc, second.PrAuc, precision: 8);
            Assert.Equal(first.Threshold, second.Threshold, precision: 8);
            Assert.InRange(first.PrAuc, 0, 1);
            Assert.InRange(first.Threshold, 0.01, 0.99);
            Assert.Equal(30, JsonSerializer.Deserialize<float[]>(first.FeatureMeansJson)?.Length);
            Assert.Equal(30, JsonSerializer.Deserialize<float[]>(first.FeatureStdDevsJson)?.Length);
            Assert.True(first.FraudTestCount > 0);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
