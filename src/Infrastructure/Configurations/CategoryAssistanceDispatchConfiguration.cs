using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class CategoryAssistanceAttemptConfiguration : IEntityTypeConfiguration<CategoryAssistanceAttemptEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceAttemptEntity> b)
	{
		b.ToTable("Attempts", "category_assistance", t => t.HasCheckConstraint("CK_CategoryAssistance_AttemptState", "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate','accepted','dismissed')"));
		b.HasKey(x => x.Id);
		b.HasIndex(x => x.RequestId).IsUnique();
		b.HasIndex(x => new { x.DecisionActor, x.DecisionOperationId }).IsUnique();
		b.Property(x => x.DecisionActor).HasMaxLength(450);
		// A bounded index protects PostgreSQL's btree tuple limit. Readers still compare the exact tuple;
		// a hypothetical SHA-256 collision is skipped rather than attached to another source.
		b.HasIndex(x => new { x.RestoreGeneration, x.SourceHash, x.TaxonomyRevision, x.Provider, x.Model, x.QuestionPolicyVersion, x.ConnectionGeneration, x.RequestScope }).IsUnique();
		b.HasIndex(x => new { x.State, x.NextEligibleAt, x.LeaseExpiresAt });
		b.Property(x => x.SourceHash).HasMaxLength(64);
		b.Property(x => x.Location).HasMaxLength(1000);
		b.Property(x => x.Description).HasMaxLength(2000);
		b.Property(x => x.ItemCodeKey).HasMaxLength(200);
		b.Property(x => x.Provider).HasMaxLength(100);
		b.Property(x => x.Model).HasMaxLength(200);
		b.Property(x => x.QuestionPolicyVersion).HasMaxLength(100);
	}
}

public sealed class CategoryAssistanceDailyUsageConfiguration : IEntityTypeConfiguration<CategoryAssistanceDailyUsageEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceDailyUsageEntity> b)
	{
		b.ToTable("DailyUsage", "category_assistance", t => t.HasCheckConstraint("CK_CategoryAssistance_NonnegativeUsage", "\"DispatchAttempts\" >= 0"));
		b.HasKey(x => x.UtcDay);
	}
}
