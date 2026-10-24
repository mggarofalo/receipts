using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class NormalizationReviewWorkConfiguration : IEntityTypeConfiguration<NormalizationReviewWorkEntity>
{
	public void Configure(EntityTypeBuilder<NormalizationReviewWorkEntity> builder)
	{
		builder.ToTable("NormalizationReviewWork", "matching");
		builder.HasKey(value => value.ItemId);
		builder.Property(value => value.SourceHash).HasMaxLength(64);
		builder.HasIndex(value => new { value.SourceHash, value.CreatedAt });
		builder.HasOne<ReceiptItemEntity>().WithMany().HasForeignKey(value => value.ItemId).OnDelete(DeleteBehavior.Cascade);
	}
}
