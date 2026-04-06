using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddSoftDeleteFilterToYnabSyncRecordIndex : Migration
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
		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords",
			columns: new[] { "LocalTransactionId", "SyncType" },
			unique: true);
	}
}
