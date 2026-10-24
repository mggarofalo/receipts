using Infrastructure.Entities.NormalizationAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class NormalizationAssistancePreferenceConfiguration : IEntityTypeConfiguration<NormalizationAssistancePreferenceEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationAssistancePreferenceEntity> builder)
	{
		builder.ToTable("NormalizationAssistancePreferences", "matching");
		builder.HasKey(value => value.Id);
		builder.Property(value => value.OwnerUserId).HasMaxLength(450);
		builder.Property(value => value.Model).HasMaxLength(200);
	}
}

public sealed class NormalizationAssistanceAttemptConfiguration : IEntityTypeConfiguration<NormalizationAssistanceAttemptEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationAssistanceAttemptEntity> builder)
	{
		builder.ToTable("NormalizationAssistanceAttempts", "matching");
		builder.HasKey(value => value.Id);
		builder.Property(value => value.OwnerUserId).HasMaxLength(450);
		builder.Property(value => value.SourceHash).HasMaxLength(64);
		builder.Property(value => value.Location).HasMaxLength(1000);
		builder.Property(value => value.Description).HasMaxLength(2000);
		builder.Property(value => value.ItemCode).HasMaxLength(200);
		builder.Property(value => value.Model).HasMaxLength(200);
		builder.Property(value => value.State).HasMaxLength(32);
		builder.Property(value => value.ErrorCode).HasMaxLength(100);
		builder.Property(value => value.ErrorDetail).HasMaxLength(1000);
		builder.HasIndex(value => new { value.OwnerUserId, value.SourceHash }).IsUnique();
		builder.HasIndex(value => new { value.State, value.NextAttemptAt });
		builder.HasIndex(value => value.ClientRequestId).IsUnique();
		builder.HasOne<NormalizationAiRunEntity>().WithMany().HasForeignKey(value => value.RunId).OnDelete(DeleteBehavior.SetNull);
	}
}
