using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSyncEventEntityConfiguration : IEntityTypeConfiguration<YnabSyncEventEntity>
{
	public void Configure(EntityTypeBuilder<YnabSyncEventEntity> builder)
	{
		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.ErrorMessage)
			.HasMaxLength(2000);

		builder.Property(e => e.YnabBudgetId)
			.HasMaxLength(64);

		builder.Property(e => e.YnabTransactionId)
			.HasMaxLength(64);

		// Activity-log queries always filter or sort by time descending; an
		// index on OccurredAt (desc) makes the recent-N feed and the
		// rolling-window status aggregates cheap.
		builder.HasIndex(e => e.OccurredAt)
			.IsDescending(true);

		// Drill-down from receipt detail to its event history is a likely
		// future query; index on ReceiptId keeps that cheap too.
		builder.HasIndex(e => e.ReceiptId);
	}
}
