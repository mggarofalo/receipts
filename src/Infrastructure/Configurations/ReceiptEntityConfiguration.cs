using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ReceiptEntityConfiguration : IEntityTypeConfiguration<ReceiptEntity>
{
	public void Configure(EntityTypeBuilder<ReceiptEntity> builder)
	{
		builder.ToTable("Receipts", "receipts");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.OriginalImagePath)
			.HasMaxLength(1024);

		builder.Property(e => e.ProcessedImagePath)
			.HasMaxLength(1024);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
