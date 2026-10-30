using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ReceiptEntityConfiguration : IEntityTypeConfiguration<ReceiptEntity>
{
	public void Configure(EntityTypeBuilder<ReceiptEntity> builder)
	{
		builder.Property(e => e.NormalizationRevision).HasDefaultValueSql("gen_random_uuid()").ValueGeneratedOnAddOrUpdate();
		builder.Property(e => e.NormalizationRevision).Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
		builder.Property(e => e.NormalizationRevision).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
		builder.ToTable("Receipts", "receipts");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.OriginalImagePath)
			.HasMaxLength(1024);

		builder.Property(e => e.ProcessedImagePath)
			.HasMaxLength(1024);

		// RECEIPTS-787: the default receipt list sorts by Date desc with offset pagination and
		// every DashboardService/report method filters Date within a range — all under the
		// DeletedAt IS NULL soft-delete filter. Without this the Receipts table has only its PK,
		// forcing a full scan + sort. A filtered index on Date (partial to active rows) serves the
		// date-range, date-sort, and soft-delete predicate shapes in one small, hot index.
		builder.HasIndex(e => e.Date)
			.HasFilter("\"DeletedAt\" IS NULL");

		builder.HasOne<Entities.Envelopes.EnvelopeEntity>().WithMany().HasForeignKey(e => e.PaidFromEnvelopeId).OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<Entities.Envelopes.OccasionEntity>().WithMany().HasForeignKey(e => e.OccasionId).OnDelete(DeleteBehavior.Restrict);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
