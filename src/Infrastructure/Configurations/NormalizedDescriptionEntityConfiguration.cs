using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class NormalizedDescriptionEntityConfiguration : IEntityTypeConfiguration<NormalizedDescriptionEntity>
{
	public void Configure(EntityTypeBuilder<NormalizedDescriptionEntity> builder)
	{
		builder.ToTable("NormalizedDescriptions", "matching");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		// Status stored as string via HasConversion, mirroring the ReceiptItemEntity.PricingMode pattern.
		builder.Property(e => e.Status)
			.HasConversion(
				v => v.ToString(),
				v => Enum.Parse<NormalizedDescriptionStatus>(v, ignoreCase: true))
			.HasMaxLength(32);

		builder.Property(e => e.Embedding)
			.HasColumnType($"vector({OnnxEmbeddingService.EmbeddingDimension})");

		// The unique functional index on lower(CanonicalName) and the partial HNSW index on
		// Embedding are added via raw SQL in the migration — EF cannot natively express
		// functional indexes or pgvector operator classes.
	}
}
