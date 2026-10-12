using System.Text.Json;
using Fraud.Api.Data;
using Fraud.Api.Models;
using Fraud.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fraud.Api.Controllers;

[ApiController]
[Route("api/models")]
[Authorize]
public sealed class ModelsController(FraudDbContext db, ModelRegistry registry,
    IModelTrainingService training, ModelEvaluationService evaluation, IConfiguration configuration, ILogger<ModelsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var models = await db.ModelVersions.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Take(20).Select(x => new
            {
                x.Version, x.IsActive, x.PrAuc, x.Precision, x.Recall, x.Threshold,
                x.TrainCount, x.ValidationCount, x.TestCount, x.FraudTestCount, x.CreatedAt
            }).ToListAsync(cancellationToken);
        return Ok(models);
    }

    [HttpGet("health")]
    public async Task<ActionResult<ModelHealthResponse>> Health(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await db.ModelVersions.AsNoTracking().Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var recent = await db.Transactions.AsNoTracking().Where(x => x.CreatedAt >= now.AddHours(-24))
            .OrderByDescending(x => x.CreatedAt).Take(1000).Select(x => x.FeaturesJson).ToListAsync(cancellationToken);
        double? meanShift = null;
        double? maxShift = null;
        var driftAlert = false;
        if (active is not null && recent.Count > 20)
        {
            var baselineMeans = JsonSerializer.Deserialize<float[]>(active.FeatureMeansJson) ?? [];
            var baselineStdDevs = JsonSerializer.Deserialize<float[]>(active.FeatureStdDevsJson) ?? [];
            var parsed = recent.Select(json =>
            {
                try { return TrainingDataReader.DeserializeFeatures(json); }
                catch (InvalidDataException) { return null; }
            }).Where(x => x is not null).Select(x => x!).ToArray();
            if (parsed.Length > 20 && baselineMeans.Length == 30 && baselineStdDevs.Length == 30)
            {
                var shifts = new List<double>(30);
                for (var j = 0; j < 30; j++)
                {
                    var observedMean = parsed.Average(row => row[j]);
                    var denominator = Math.Max(baselineStdDevs[j], 1e-6f);
                    shifts.Add(Math.Abs(observedMean - baselineMeans[j]) / denominator);
                }
                meanShift = shifts.Average();
                maxShift = shifts.Max();
                driftAlert = maxShift >= 3.0;
            }
        }

        var datasetPath = configuration["ML:DatasetPath"] ?? "data/raw/creditcard.csv";
        datasetPath = ProjectPaths.Resolve(datasetPath);
        var datasetStatus = System.IO.File.Exists(datasetPath) ? "available" : "missing; add data/raw/creditcard.csv";
        var scoredLast24 = await db.Transactions.CountAsync(x => x.CreatedAt >= now.AddHours(-24), cancellationToken);
        return Ok(new ModelHealthResponse(registry.IsLoaded, registry.Version ?? active?.Version,
            registry.Threshold ?? active?.Threshold, active?.PrAuc, active?.Precision, active?.Recall,
            scoredLast24, meanShift, maxShift, driftAlert,
            "univariate feature mean-shift z-score vs train baseline; screening signal, not a formal multivariate drift test",
            datasetStatus, active?.CreatedAt ?? registry.CreatedAt));
    }

    [HttpGet("evaluation")]
    public async Task<IActionResult> Evaluation(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await evaluation.EvaluateActiveAsync(cancellationToken));
        }
        catch (FileNotFoundException ex)
        {
            return Problem(statusCode: StatusCodes.Status424FailedDependency, title: "Evaluation input is missing", detail: ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Evaluation data is not usable", detail: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Model evaluation is unavailable", detail: ex.Message);
        }
    }

    [HttpGet("monitoring")]
    public async Task<ActionResult<ModelMonitoringResponse>> Monitoring(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-24);
        var active = await db.ModelVersions.AsNoTracking().Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var threshold = registry.Threshold ?? active?.Threshold ?? 0.5;

        var transactionCount = await db.Transactions.CountAsync(x => x.CreatedAt >= since, cancellationToken);
        var flaggedCount = await db.Transactions.CountAsync(x => x.CreatedAt >= since && x.IsFlagged, cancellationToken);
        var pendingCount = await db.Transactions.CountAsync(x => x.ReviewStatus == "Pending", cancellationToken);
        var reviewedCount = await db.ReviewDecisions.CountAsync(x => x.CreatedAt >= since, cancellationToken);
        var recent = await db.Transactions.AsNoTracking()
            .Where(x => x.CreatedAt >= since)
            .OrderByDescending(x => x.CreatedAt).Take(10000)
            .Select(x => new { x.CreatedAt, x.RiskScore, x.IsFlagged, x.FeaturesJson })
            .ToListAsync(cancellationToken);

        var low = recent.Count(x => x.RiskScore < threshold * 0.5);
        var medium = recent.Count(x => x.RiskScore >= threshold * 0.5 && x.RiskScore < threshold);
        var high = recent.Count(x => x.RiskScore >= threshold);
        // Anchor the eight three-hour buckets to the rolling 24-hour window so the newest
        // bucket always ends at "now", including around midnight.
        var timelineStart = since;
        var timeline = Enumerable.Range(0, 8).Select(i =>
        {
            var start = timelineStart.AddHours(i * 3);
            var end = start.AddHours(3);
            var bucket = recent.Where(x => x.CreatedAt >= start && x.CreatedAt < end).ToArray();
            return new MonitoringBucket(start.ToString("HH:mm"), bucket.Length, bucket.Count(x => x.IsFlagged),
                bucket.Length == 0 ? 0d : bucket.Average(x => (double)x.RiskScore));
        }).ToArray();

        double? meanShift = null;
        double? maxShift = null;
        var driftAlert = false;
        if (active is not null && recent.Count > 20)
        {
            try
            {
                var means = JsonSerializer.Deserialize<float[]>(active.FeatureMeansJson) ?? [];
                var stdDevs = JsonSerializer.Deserialize<float[]>(active.FeatureStdDevsJson) ?? [];
                var parsed = recent.Select(x =>
                {
                    try { return TrainingDataReader.DeserializeFeatures(x.FeaturesJson); }
                    catch (InvalidDataException) { return null; }
                }).Where(x => x is not null).Select(x => x!).ToArray();
                if (parsed.Length > 20 && means.Length == 30 && stdDevs.Length == 30)
                {
                    var shifts = Enumerable.Range(0, 30).Select(j =>
                    {
                        var observed = parsed.Average(row => row[j]);
                        return Math.Abs(observed - means[j]) / Math.Max(stdDevs[j], 1e-6f);
                    }).ToArray();
                    meanShift = shifts.Average();
                    maxShift = shifts.Max();
                    driftAlert = maxShift >= 3.0;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Could not read baseline feature statistics for monitoring");
            }
        }

        return Ok(new ModelMonitoringResponse(now, transactionCount, flaggedCount, pendingCount, reviewedCount,
            transactionCount == 0 ? 0d : (double)flaggedCount / transactionCount,
            low, medium, high, timeline, "Three-hour windows from the most recent 24 hours; timeline/distribution use up to 10,000 recent records.",
            driftAlert, meanShift, maxShift,
            "Univariate feature mean-shift z-score; a screening signal, not a formal multivariate drift test."));
    }

    [HttpPut("threshold")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateThreshold(ThresholdRequest request, CancellationToken cancellationToken)
    {
        if (!registry.IsLoaded) return Conflict(new { message = "Train a model before changing its threshold." });
        try { registry.UpdateThreshold(request.Threshold); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        var version = registry.Version;
        var active = await db.ModelVersions.FirstOrDefaultAsync(x => x.IsActive && x.Version == version, cancellationToken);
        if (active is not null)
        {
            active.Threshold = request.Threshold;
            await db.SaveChangesAsync(cancellationToken);
        }
        return Ok(new { version, threshold = request.Threshold });
    }

    [HttpPost("retrain")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Retrain(CancellationToken cancellationToken)
    {
        try
        {
            var model = await training.RetrainAsync(cancellationToken);
            return Ok(new
            {
                model.Version, model.PrAuc, model.Precision, model.Recall, model.Threshold,
                model.TrainCount, model.ValidationCount, model.TestCount, model.FraudTestCount, model.CreatedAt
            });
        }
        catch (FileNotFoundException ex)
        {
            return Problem(statusCode: StatusCodes.Status424FailedDependency, title: "Training dataset is missing", detail: ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Training data is not usable", detail: ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Model retraining request could not be completed");
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Training failed", detail: ex.Message);
        }
    }
}
