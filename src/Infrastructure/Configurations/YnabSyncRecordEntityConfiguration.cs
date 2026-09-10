using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class YnabSyncRecordEntityConfiguration : IEntityTypeConfiguration<YnabSyncRecordEntity>
{
	public void Configure(EntityTypeBuilder<YnabSyncRecordEntity> builder)
	{
		builder.ToTable("YnabSyncRecords", "ynab", table =>
		{
			table.HasCheckConstraint(
				"CK_YnabSyncRecords_ClaimLease",
				"(\"ClaimToken\" IS NULL) = (\"ClaimedAtUtc\" IS NULL)");
			table.HasCheckConstraint(
				"CK_YnabSyncRecords_AttemptCount",
				"\"AttemptCount\" >= 0");
			table.HasCheckConstraint(
				"CK_YnabSyncRecords_PushSnapshot",
				"\"RequestPayloadJson\" IS NULL OR (\"ImportId\" IS NOT NULL AND \"PayloadHash\" IS NOT NULL AND \"SourceVersion\" IS NOT NULL AND \"YnabAccountId\" IS NOT NULL)");
		});

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.Property(e => e.LastError)
			.HasMaxLength(2000);

		builder.Property(e => e.ImportId)
			.HasMaxLength(36);

		builder.Property(e => e.PayloadHash)
			.HasMaxLength(64);

		builder.Property(e => e.SourceVersion)
			.HasMaxLength(64);

		builder.HasIndex(e => new { e.LocalTransactionId, e.SyncType, e.YnabBudgetId })
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		// YNAB de-duplicates import IDs per destination account. Keep tombstoned
		// operations in this uniqueness domain because their remote transaction and
		// consumed import ID survive a local soft delete.
		builder.HasIndex(e => new { e.YnabBudgetId, e.YnabAccountId, e.ImportId })
			.IsUnique()
			.HasFilter("\"SyncType\" = 'TransactionPush' AND \"ImportId\" IS NOT NULL AND \"YnabAccountId\" IS NOT NULL");

		// ClientCascade: EF only cascades when related entities are loaded in the
		// same change tracker. Hard-delete paths (TrashService.PurgeAllDeletedAsync)
		// must delete YnabSyncRecords before Transactions to respect FK order.
		// Soft-delete cascades via OwnedByFk attribute + HandleSoftDelete().
		builder.HasOne(e => e.Transaction)
			.WithMany()
			.HasForeignKey(e => e.LocalTransactionId)
			.OnDelete(DeleteBehavior.ClientCascade);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
