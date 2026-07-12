using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddFilterToYnabSyncRecordUniqueIndex : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords",
			columns: new[] { "LocalTransactionId", "SyncType" },
			unique: true,
			filter: "\"DeletedAt\" IS NULL");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// This migration's Up() only re-creates the already-filtered index (the filter was
		// added by the prior migration, AddSoftDeleteFilterToYnabSyncRecordIndex), so its
		// Down() must restore that SAME filtered state — not an unfiltered index. Recreating
		// the index WITHOUT "DeletedAt" IS NULL would fail with a unique violation on exactly
		// the soft-deleted-duplicate rows the filter exists to permit, permanently blocking
		// rollback (RECEIPTS-768).
		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords",
			columns: new[] { "LocalTransactionId", "SyncType" },
			unique: true,
			filter: "\"DeletedAt\" IS NULL");
	}
}
