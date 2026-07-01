using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ItemSimilarityEdgeEntityConfiguration : IEntityTypeConfiguration<ItemSimilarityEdgeEntity>
{
	public void Configure(EntityTypeBuilder<ItemSimilarityEdgeEntity> builder)
	{
		builder.ToTable("ItemSimilarityEdges", "matching", t => t.HasCheckConstraint(
			"CK_ItemSimilarityEdges_CanonicalOrder",
			"\"DescA\" < \"DescB\""));

		builder.HasKey(e => new { e.DescA, e.DescB });

		builder.Property(e => e.DescA).IsRequired();
		builder.Property(e => e.DescB).IsRequired();

		builder.HasIndex(e => e.Score);

		builder.HasOne<DistinctDescriptionEntity>()
			.WithMany()
			.HasForeignKey(e => e.DescA)
			.OnDelete(DeleteBehavior.Cascade);

		builder.HasOne<DistinctDescriptionEntity>()
			.WithMany()
			.HasForeignKey(e => e.DescB)
			.OnDelete(DeleteBehavior.Cascade);
	}
}
