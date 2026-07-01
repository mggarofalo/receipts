using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class AdjustmentEntityConfiguration : IEntityTypeConfiguration<AdjustmentEntity>
{
	public void Configure(EntityTypeBuilder<AdjustmentEntity> builder)
	{
		builder.ToTable("Adjustments", "receipts");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Navigation(e => e.Receipt)
			.AutoInclude();

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
