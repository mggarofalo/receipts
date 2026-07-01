using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class DistinctDescriptionEntityConfiguration : IEntityTypeConfiguration<DistinctDescriptionEntity>
{
	public void Configure(EntityTypeBuilder<DistinctDescriptionEntity> builder)
	{
		builder.ToTable("DistinctDescriptions", "matching");

		builder.HasKey(e => e.Description);

		builder.Property(e => e.Description)
			.IsRequired();

		// Trigram GIN index is added in the migration's Up() via raw SQL — EF does not model
		// custom index methods like `USING gin (col gin_trgm_ops)`.
	}
}
