using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ReceiptItemEntityConfiguration : IEntityTypeConfiguration<ReceiptItemEntity>
{
	public void Configure(EntityTypeBuilder<ReceiptItemEntity> builder)
	{
		builder.ToTable("ReceiptItems", "receipts");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Navigation(e => e.Receipt)
			.AutoInclude();

		builder.HasOne(e => e.NormalizedDescription)
			.WithMany()
			.HasForeignKey(e => e.NormalizedDescriptionId)
			.OnDelete(DeleteBehavior.SetNull);

		builder.Navigation(e => e.NormalizedDescription)
			.AutoInclude();

		builder.HasIndex(e => e.NormalizedDescriptionId);

		// Threshold-impact previews aggregate ReceiptItem counts by bucketing on match score.
		// An index on NormalizedDescriptionMatchScore keeps those aggregates fast even as the
		// table grows; the column is populated by the resolver (RECEIPTS-578) with the cosine
		// similarity at resolve time and remains NULL for unresolved / newly-created items.
		builder.HasIndex(e => e.NormalizedDescriptionMatchScore);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
