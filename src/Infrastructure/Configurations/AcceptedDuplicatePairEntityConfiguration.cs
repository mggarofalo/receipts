using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class AcceptedDuplicatePairEntityConfiguration : IEntityTypeConfiguration<AcceptedDuplicatePairEntity>
{
	public void Configure(EntityTypeBuilder<AcceptedDuplicatePairEntity> builder)
	{
		// RECEIPTS-746 schema convention: bounded-context schema, never the default.
		builder.ToTable("AcceptedDuplicatePairs", "receipts", t => t.HasCheckConstraint(
			"CK_AcceptedDuplicatePairs_CanonicalOrder",
			"\"ReceiptIdA\" < \"ReceiptIdB\""));

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.ReceiptIdA).IsRequired();
		builder.Property(e => e.ReceiptIdB).IsRequired();
		builder.Property(e => e.AcceptedAt).IsRequired();

		// One ACTIVE row per unordered pair. Filtered to active rows so un-accepting (soft delete)
		// and then re-accepting the same pair does not collide with the tombstone.
		builder.HasIndex(e => new { e.ReceiptIdA, e.ReceiptIdB })
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		// Cascade on BOTH ends: a permanently purged receipt can never be flagged again, so its
		// acceptances are dead weight. Soft-deleting a receipt does NOT reach here (the repository
		// converts deletes to soft deletes and this entity is not an IOwnedBy child), which is what
		// keeps a restored receipt's group suppressed. Postgres allows multiple cascade paths to the
		// same table, unlike SQL Server.
		builder.HasOne<ReceiptEntity>()
			.WithMany()
			.HasForeignKey(e => e.ReceiptIdA)
			.OnDelete(DeleteBehavior.Cascade);

		builder.HasOne<ReceiptEntity>()
			.WithMany()
			.HasForeignKey(e => e.ReceiptIdB)
			.OnDelete(DeleteBehavior.Cascade);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
