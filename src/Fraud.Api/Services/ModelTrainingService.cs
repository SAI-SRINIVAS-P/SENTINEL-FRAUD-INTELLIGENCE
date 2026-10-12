using System.Text.Json;
using Fraud.Api.Data;
using Fraud.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Fraud.Api.Services;

public interface IModelTrainingService
{
    Task<ModelVersionEntity> RetrainAsync(CancellationToken cancellationToken = default);
}

public sealed class ModelTrainingService(
    FraudDbContext db,
    ModelTrainer trainer,
    ModelRegistry registry,
    IConfiguration configuration,
    ILogger<ModelTrainingService> logger) : IModelTrainingService
{
    private static readonly SemaphoreSlim TrainingLock = new(1, 1);

    public async Task<ModelVersionEntity> RetrainAsync(CancellationToken cancellationToken = default)
    {
        await TrainingLock.WaitAsync(cancellationToken);
        try
        {
            var datasetPath = configuration["ML:DatasetPath"] ?? "data/raw/creditcard.csv";
            datasetPath = ProjectPaths.Resolve(datasetPath);
            // In containers, the path is configured explicitly to /app/data/raw/creditcard.csv.
            if (!File.Exists(datasetPath))
                throw new FileNotFoundException($"Training dataset is missing at '{datasetPath}'. See docs/DATASET.md.", datasetPath);

            var baselineRows = await Task.Run(() => TrainingDataReader.ReadUlbCsv(datasetPath), cancellationToken);
            var feedbackRows = await db.TrainingFeedback.AsNoTracking().ToListAsync(cancellationToken);
            var examples = new List<TrainingExample>(feedbackRows.Count);
            foreach (var feedback in feedbackRows)
            {
                try
                {
                    examples.Add(new TrainingExample
                    {
                        Features = TrainingDataReader.DeserializeFeatures(feedback.FeaturesJson),
                        Label = feedback.IsFraud
                    });
                }
                catch (InvalidDataException ex)
                {
                    logger.LogWarning(ex, "Skipping malformed training feedback {FeedbackId}", feedback.Id);
                }
            }

            var modelsDirectory = configuration["ML:ModelsDirectory"] ?? "artifacts/models";
            modelsDirectory = ProjectPaths.Resolve(modelsDirectory);
            Directory.CreateDirectory(modelsDirectory);

            var version = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            var artifactFileName = $"{version}.model.zip";
            var artifactPath = Path.Combine(modelsDirectory, artifactFileName);
            var metadataPath = Path.Combine(modelsDirectory, $"{version}.metadata.json");

            var result = await Task.Run(() => trainer.Train(baselineRows, examples, artifactPath), cancellationToken);
            var metadata = new ModelArtifactMetadata
            {
                Version = version,
                ArtifactFileName = artifactFileName,
                Threshold = result.Metrics.Threshold,
                CreatedAt = DateTimeOffset.UtcNow,
                FeatureMeansJson = result.Metrics.FeatureMeansJson,
                FeatureStdDevsJson = result.Metrics.FeatureStdDevsJson
            };
            var tempMetadataPath = metadataPath + ".tmp";
            await File.WriteAllTextAsync(tempMetadataPath,
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(tempMetadataPath, metadataPath, overwrite: true);

            var metrics = result.Metrics;
            var entity = new ModelVersionEntity
            {
                Version = version,
                ArtifactFileName = artifactFileName,
                IsActive = true,
                PrAuc = metrics.PrAuc,
                Precision = metrics.Precision,
                Recall = metrics.Recall,
                Threshold = metrics.Threshold,
                TrainCount = metrics.TrainCount,
                ValidationCount = metrics.ValidationCount,
                TestCount = metrics.TestCount,
                FraudTestCount = metrics.FraudTestCount,
                FeatureMeansJson = metrics.FeatureMeansJson,
                FeatureStdDevsJson = metrics.FeatureStdDevsJson,
                CreatedAt = metadata.CreatedAt
            };
            // Explicit transactions must execute inside EF's retry strategy when retry-on-failure is enabled.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var oldActive = await db.ModelVersions.Where(x => x.IsActive).ToListAsync(cancellationToken);
                foreach (var old in oldActive) old.IsActive = false;
                db.ModelVersions.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });

            registry.Activate(artifactPath, metadataPath, metadata);
            logger.LogInformation("Activated model {Version}: PR-AUC={PrAuc:F4}, threshold={Threshold:F2}, test={TestCount}",
                version, metrics.PrAuc, metrics.Threshold, metrics.TestCount);
            return entity;
        }
        finally
        {
            TrainingLock.Release();
        }
    }
}
