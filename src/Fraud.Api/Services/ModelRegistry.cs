using System.Text.Json;
using Fraud.Api.Models;
using Microsoft.ML;

namespace Fraud.Api.Services;

public sealed class ModelArtifactMetadata
{
    public string Version { get; set; } = string.Empty;
    public string ArtifactFileName { get; set; } = string.Empty;
    public double Threshold { get; set; } = 0.5;
    public DateTimeOffset CreatedAt { get; set; }
    public string FeatureMeansJson { get; set; } = "[]";
    public string FeatureStdDevsJson { get; set; } = "[]";
}

public sealed class ModelRegistry
{
    private readonly object _sync = new();
    private readonly MLContext _ml = new(seed: ModelTrainer.Seed);
    private PredictionEngine<FraudModelInput, FraudModelPrediction>? _engine;
    private ModelArtifactMetadata? _metadata;
    private string? _metadataPath;

    public bool IsLoaded { get { lock (_sync) return _engine is not null; } }
    public string? Version { get { lock (_sync) return _metadata?.Version; } }
    public double? Threshold { get { lock (_sync) return _metadata?.Threshold; } }
    public string? FeatureMeansJson { get { lock (_sync) return _metadata?.FeatureMeansJson; } }
    public string? FeatureStdDevsJson { get { lock (_sync) return _metadata?.FeatureStdDevsJson; } }
    public DateTimeOffset? CreatedAt { get { lock (_sync) return _metadata?.CreatedAt; } }

    public bool TryLoadLatest(string modelsDirectory)
    {
        if (!Directory.Exists(modelsDirectory)) return false;
        foreach (var metadataPath in Directory.GetFiles(modelsDirectory, "*.metadata.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<ModelArtifactMetadata>(File.ReadAllText(metadataPath));
                if (metadata is null) continue;
                var modelPath = Path.Combine(modelsDirectory, metadata.ArtifactFileName);
                if (!File.Exists(modelPath)) continue;
                Activate(modelPath, metadataPath, metadata);
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                // Ignore an incomplete/corrupt candidate and try the next version.
            }
        }
        return false;
    }

    public void Activate(string modelPath, string metadataPath, ModelArtifactMetadata metadata)
    {
        var transformer = _ml.Model.Load(modelPath, out _);
        var engine = _ml.Model.CreatePredictionEngine<FraudModelInput, FraudModelPrediction>(transformer);
        lock (_sync)
        {
            _engine = engine;
            _metadata = metadata;
            _metadataPath = metadataPath;
        }
    }

    public ModelPredictionResult PredictWithExplanations(float[] features)
    {
        if (features.Length != 30 || features.Any(x => !float.IsFinite(x)))
            throw new ArgumentException("Exactly 30 finite features are required.", nameof(features));
        lock (_sync)
        {
            var engine = _engine ?? throw new InvalidOperationException("No trained model is active. Upload the dataset and train a model first.");
            var metadata = _metadata!;
            var probability = engine.Predict(new FraudModelInput { Features = features }).Probability;
            var explanations = new List<FeatureSensitivity>(30);
            for (var i = 0; i < features.Length; i++)
            {
                var ablated = (float[])features.Clone();
                ablated[i] = 0f;
                var ablatedProbability = engine.Predict(new FraudModelInput { Features = ablated }).Probability;
                explanations.Add(new FeatureSensitivity(TrainingDataReader.FeatureNames[i], probability - ablatedProbability));
            }
            return new ModelPredictionResult(probability, probability >= metadata.Threshold, metadata.Version,
                explanations.OrderByDescending(x => Math.Abs(x.ProbabilityDelta)).Take(5).ToArray());
        }
    }

    public void UpdateThreshold(double threshold)
    {
        if (threshold is < 0.01 or > 0.99 || !double.IsFinite(threshold))
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0.01 and 0.99.");
        lock (_sync)
        {
            if (_metadata is null || _metadataPath is null)
                throw new InvalidOperationException("No active model exists.");
            _metadata.Threshold = threshold;
            var tempPath = _metadataPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_metadata, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _metadataPath, overwrite: true);
        }
    }
}
