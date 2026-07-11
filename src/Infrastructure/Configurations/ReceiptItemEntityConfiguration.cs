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

		// RECEIPTS-770: ApplicationDbContext.PrepareEntityTypesInModelBuilder maps EVERY decimal
		// property to the money type decimal(18,2). That is wrong for Quantity and UnitPrice:
		//   - Quantity is a count/weight, not money. Postgres silently rounds fractional
		//     quantities (e.g. 2.5 kg, 1.125 dozen) to scale 2 on insert.
		//   - UnitPrice legitimately needs sub-cent precision (e.g. fuel at 3.459/gal, or
		//     per-gram produce). Rounding it to scale 2 corrupts the value on insert.
		// TotalAmount stays decimal(18,2): it is the reconciled money amount that must land on
		// whole cents. This configuration runs AFTER PrepareEntityTypesInModelBuilder, so these
		// explicit overrides win. Widening scale 2 -> 4 is non-lossy for existing data.
		builder.Property(e => e.Quantity)
			.HasColumnType("decimal(18,4)");

		builder.Property(e => e.UnitPrice)
			.HasColumnType("decimal(18,4)");

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
