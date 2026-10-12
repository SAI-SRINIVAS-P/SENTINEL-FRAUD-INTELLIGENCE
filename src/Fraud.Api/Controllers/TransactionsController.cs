using System.Security.Claims;
using System.Text.Json;
using Fraud.Api.Data;
using Fraud.Api.Hubs;
using Fraud.Api.Models;
using Fraud.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Fraud.Api.Controllers;

[ApiController]
[Route("api/transactions")]
[Authorize]
public sealed class TransactionsController(FraudDbContext db, ModelRegistry registry,
    IHubContext<TransactionHub> hub, ILogger<TransactionsController> logger) : ControllerBase
{
    [HttpPost("score")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Score(ScoreTransactionRequest request, CancellationToken cancellationToken)
    {
        if (!registry.IsLoaded)
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No trained model is active", detail: "Add the ULB CSV and call the admin retrain endpoint before scoring.");
        if (request.V.Length != 28 || request.V.Any(x => !float.IsFinite(x)))
            return BadRequest(new { message = "V must contain exactly 28 finite numeric values (V1 through V28)." });

        var externalId = string.IsNullOrWhiteSpace(request.ExternalId) ? Guid.NewGuid().ToString("N") : request.ExternalId.Trim();
        if (await db.Transactions.AnyAsync(x => x.ExternalId == externalId, cancellationToken))
            return Conflict(new { message = "This external transaction ID has already been scored." });

        var features = new float[30];
        features[0] = (float)request.Time;
        Array.Copy(request.V, 0, features, 1, 28);
        features[29] = (float)request.Amount;
        var prediction = registry.PredictWithExplanations(features);
        var record = new TransactionRecord
        {
            ExternalId = externalId,
            Amount = request.Amount,
            TimeFeature = request.Time,
            OccurredAt = DateTimeOffset.UtcNow,
            FeaturesJson = JsonSerializer.Serialize(features),
            ExplanationsJson = JsonSerializer.Serialize(prediction.Explanations),
            RiskScore = prediction.Probability,
            IsFlagged = prediction.IsFlagged,
            ReviewStatus = prediction.IsFlagged ? "Pending" : "NotFlagged",
            ModelVersion = prediction.ModelVersion
        };
        db.Transactions.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var response = new
        {
            transaction = ToListItem(record),
            probability = prediction.Probability,
            threshold = registry.Threshold,
            explanations = prediction.Explanations,
            explanationMethod = "single-feature ablation against a zeroed feature; sensitivity only, not causal attribution"
        };
        if (record.IsFlagged)
        {
            await hub.Clients.All.SendAsync("transactionFlagged", response, cancellationToken);
            logger.LogInformation("Flagged transaction {TransactionId} with risk {RiskScore:F3} using {ModelVersion}",
                record.Id, record.RiskScore, record.ModelVersion);
        }
        return CreatedAtAction(nameof(GetById), new { id = record.Id }, response);
    }

    [HttpGet]
    public async Task<ActionResult<TransactionPage>> GetQueue([FromQuery] string status = "Pending",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Transactions.AsNoTracking();
        if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.ReviewStatus == status);
        var total = await query.CountAsync(cancellationToken);
        var records = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = records.Select(ToListItem).ToList();
        return Ok(new TransactionPage(items, page, pageSize, total));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return item is null ? NotFound() : Ok(ToListItem(item));
    }

    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, ReviewRequest request, CancellationToken cancellationToken)
    {
        var record = await db.Transactions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (record is null) return NotFound();
        if (!record.IsFlagged) return Conflict(new { message = "Only flagged transactions can be manually reviewed." });
        if (record.ReviewStatus != "Pending") return Conflict(new { message = "This transaction has already been reviewed." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var email = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.Email) ?? "analyst";
        record.ReviewStatus = request.IsFraud ? "ConfirmedFraud" : "Legitimate";
        db.ReviewDecisions.Add(new ReviewDecision
        {
            TransactionId = record.Id,
            ReviewerId = userId,
            ReviewerEmail = email,
            IsFraud = request.IsFraud,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TrainingFeedback.Add(new TrainingFeedback
        {
            TransactionId = record.Id,
            FeaturesJson = record.FeaturesJson,
            IsFraud = request.IsFraud,
            ReviewerId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.All.SendAsync("transactionReviewed", new { id = record.Id, record.ReviewStatus }, cancellationToken);
        return Ok(new { id = record.Id, record.ReviewStatus, message = "Review saved and added to the next training run." });
    }

    [HttpGet("reviews/recent")]
    public async Task<ActionResult<IReadOnlyList<ReviewAuditItem>>> RecentReviewAudit(
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var decisions = await db.ReviewDecisions.AsNoTracking()
            .Include(x => x.Transaction)
            .OrderByDescending(x => x.CreatedAt)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = decisions.Where(x => x.Transaction is not null).Select(x => new ReviewAuditItem(
            x.Id, x.TransactionId, x.Transaction!.ExternalId, x.Transaction.Amount, x.Transaction.RiskScore,
            x.Transaction.ModelVersion, x.IsFraud, x.ReviewerEmail, x.Note, x.CreatedAt)).ToArray();
        return Ok(items);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummary>> Summary(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var total = await db.Transactions.CountAsync(cancellationToken);
        var flagged = await db.Transactions.CountAsync(x => x.IsFlagged, cancellationToken);
        var pending = await db.Transactions.CountAsync(x => x.ReviewStatus == "Pending", cancellationToken);
        var reviewed = await db.Transactions.CountAsync(x => x.ReviewStatus == "ConfirmedFraud" || x.ReviewStatus == "Legitimate", cancellationToken);
        var average = total == 0 ? 0 : await db.Transactions.AverageAsync(x => (double)x.RiskScore, cancellationToken);
        var scored24 = await db.Transactions.CountAsync(x => x.CreatedAt >= since, cancellationToken);
        var active = await db.ModelVersions.AsNoTracking().Where(x => x.IsActive).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        // scored24 is used by the health card; response summary keeps dashboard fields compact.
        _ = scored24;
        return Ok(new DashboardSummary(total, flagged, pending, reviewed, average, active?.Version,
            active?.PrAuc, active?.Precision, active?.Recall, registry.Threshold, registry.IsLoaded));
    }

    private static TransactionListItem ToListItem(TransactionRecord record)
    {
        IReadOnlyList<FeatureSensitivity> explanations;
        try { explanations = JsonSerializer.Deserialize<FeatureSensitivity[]>(record.ExplanationsJson) ?? []; }
        catch (JsonException) { explanations = []; }
        return new TransactionListItem(record.Id, record.ExternalId, record.Amount, record.OccurredAt,
            record.RiskScore, record.IsFlagged, record.ReviewStatus, record.ModelVersion, record.CreatedAt,
            explanations, "single-feature ablation against a zeroed feature; sensitivity only, not causal attribution");
    }
}
