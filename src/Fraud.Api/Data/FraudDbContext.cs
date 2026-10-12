using Fraud.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Fraud.Api.Data;

public sealed class FraudDbContext(DbContextOptions<FraudDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<TransactionRecord> Transactions => Set<TransactionRecord>();
    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();
    public DbSet<TrainingFeedback> TrainingFeedback => Set<TrainingFeedback>();
    public DbSet<ModelVersionEntity> ModelVersions => Set<ModelVersionEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<TransactionRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ExternalId).IsUnique();
            entity.HasIndex(x => new { x.ReviewStatus, x.CreatedAt });
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.RiskScore).HasColumnType("real");
            entity.Property(x => x.ExternalId).HasMaxLength(100);
            entity.Property(x => x.ReviewStatus).HasMaxLength(40);
            entity.Property(x => x.ModelVersion).HasMaxLength(100);
            entity.Property(x => x.FeaturesJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ExplanationsJson).HasColumnType("nvarchar(max)");
        });
        builder.Entity<ReviewDecision>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.Transaction)
                .WithMany(x => x.Reviews)
                .HasForeignKey(x => x.TransactionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.ReviewerEmail).HasMaxLength(256);
            entity.Property(x => x.ReviewerId).HasMaxLength(450);
            entity.Property(x => x.Note).HasMaxLength(2000);
        });
        builder.Entity<TrainingFeedback>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TransactionId);
            entity.Property(x => x.FeaturesJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ReviewerId).HasMaxLength(450);
        });
        builder.Entity<ModelVersionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Version).IsUnique();
            entity.HasIndex(x => x.IsActive);
            entity.Property(x => x.Version).HasMaxLength(100);
            entity.Property(x => x.ArtifactFileName).HasMaxLength(255);
            entity.Property(x => x.FeatureMeansJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.FeatureStdDevsJson).HasColumnType("nvarchar(max)");
        });
    }
}
