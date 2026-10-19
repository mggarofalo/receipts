using Infrastructure.Entities.NormalizationAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class NormalizationAiConnectionConfiguration : IEntityTypeConfiguration<NormalizationAiConnectionEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationAiConnectionEntity> builder)
	{
		builder.ToTable("NormalizationAiConnection", "matching");
		builder.HasKey(e => e.Id);
		builder.Property(e => e.Id).ValueGeneratedNever();
	}
}

public sealed class NormalizationAiRunConfiguration : IEntityTypeConfiguration<NormalizationAiRunEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationAiRunEntity> builder)
	{
		builder.ToTable("NormalizationAiRuns", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => new { e.OwnerUserId, e.ClientRequestId }).IsUnique();
		builder.HasIndex(e => new { e.State, e.CreatedAt });
		builder.Property(e => e.OwnerUserId).HasMaxLength(450);
		builder.Property(e => e.State).HasMaxLength(24);
		builder.Property(e => e.Model).HasMaxLength(200);
		builder.HasMany(e => e.Proposals).WithOne().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
	}
}

public sealed class NormalizationAiProposalConfiguration : IEntityTypeConfiguration<NormalizationAiProposalEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationAiProposalEntity> builder)
	{
		builder.ToTable("NormalizationAiProposals", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => new { e.RunId, e.ReceiptItemId }).IsUnique();
		builder.HasIndex(e => e.ReceiptItemId);
		// Source IDs are immutable historical observations, not cascading business FKs.
		builder.Property(e => e.Action).HasMaxLength(24);
		builder.Property(e => e.State).HasMaxLength(24);
	}
}
