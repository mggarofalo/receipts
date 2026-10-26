using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public sealed class CategoryAssistanceHistoricalRequestConfiguration : IEntityTypeConfiguration<CategoryAssistanceHistoricalRequestEntity>
{
	public void Configure(EntityTypeBuilder<CategoryAssistanceHistoricalRequestEntity> b)
	{
		b.ToTable("HistoricalRequests", "category_assistance");
		b.HasKey(x => x.OperationId);
		b.Property(x => x.Actor).HasMaxLength(450);
		b.Property(x => x.RequestHash).HasMaxLength(64);
		b.HasIndex(x => new { x.Actor, x.CreatedAt });
	}
}
