using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class PendingNamePromotionConfiguration : IEntityTypeConfiguration<PendingNamePromotionEntity>
{
	public void Configure(EntityTypeBuilder<PendingNamePromotionEntity> builder)
	{
		builder.ToTable("PendingNamePromotions", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => e.CreatedAt);
		builder.HasMany(e => e.Entries).WithOne().HasForeignKey(e => e.PromotionId).OnDelete(DeleteBehavior.Cascade);
	}
}

public sealed class PendingNamePromotionEntryConfiguration : IEntityTypeConfiguration<PendingNamePromotionEntryEntity>
{
	public void Configure(EntityTypeBuilder<PendingNamePromotionEntryEntity> builder)
	{
		builder.ToTable("PendingNamePromotionEntries", "matching");
		builder.HasKey(e => e.Id);
		builder.HasIndex(e => new { e.PromotionId, e.NormalizedDescriptionId }).IsUnique();
	}
}
