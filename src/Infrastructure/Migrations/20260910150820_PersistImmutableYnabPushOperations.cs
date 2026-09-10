using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class PersistImmutableYnabPushOperations : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<int>(
			name: "AttemptCount",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "integer",
			nullable: false,
			defaultValue: 0);

		migrationBuilder.AddColumn<Guid>(
			name: "ClaimToken",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ClaimedAtUtc",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "ImportId",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "text",
			maxLength: 36,
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "LastAttemptAtUtc",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "PayloadHash",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "text",
			maxLength: 64,
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "RequestPayloadJson",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "SourceVersion",
			schema: "ynab",
			table: "YnabSyncRecords",
			type: "text",
			maxLength: 64,
			nullable: true);

		migrationBuilder.AddCheckConstraint(
			name: "CK_YnabSyncRecords_AttemptCount",
			schema: "ynab",
			table: "YnabSyncRecords",
			sql: "\"AttemptCount\" >= 0");

		migrationBuilder.AddCheckConstraint(
			name: "CK_YnabSyncRecords_ClaimLease",
			schema: "ynab",
			table: "YnabSyncRecords",
			sql: "(\"ClaimToken\" IS NULL) = (\"ClaimedAtUtc\" IS NULL)");

		migrationBuilder.AddCheckConstraint(
			name: "CK_YnabSyncRecords_PushSnapshot",
			schema: "ynab",
			table: "YnabSyncRecords",
			sql: "\"RequestPayloadJson\" IS NULL OR (\"ImportId\" IS NOT NULL AND \"PayloadHash\" IS NOT NULL AND \"SourceVersion\" IS NOT NULL AND \"YnabAccountId\" IS NOT NULL)");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_YnabBudgetId_YnabAccountId_ImportId",
			schema: "ynab",
			table: "YnabSyncRecords",
			columns: ["YnabBudgetId", "YnabAccountId", "ImportId"],
			unique: true,
			filter: "\"SyncType\" = 'TransactionPush' AND \"ImportId\" IS NOT NULL AND \"YnabAccountId\" IS NOT NULL");

		// Old non-terminal pushes have no durable import ID or request snapshot. Treat
		// them as ambiguous instead of silently recomputing mutable receipt data.
		migrationBuilder.Sql(
			"""
			UPDATE ynab."YnabSyncRecords"
			SET "SyncStatus" = 'Unknown',
				"LastError" = 'This YNAB attempt predates immutable operation tracking and cannot be retried automatically. Review the destination transaction before resolving it.'
				WHERE "SyncType" = 'TransactionPush'
					AND "SyncStatus" IN ('Pending', 'Failed');
			""");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// The predecessor enum cannot represent Unknown. Preserve the warning while
		// mapping ambiguous rows to its only non-terminal error state.
		migrationBuilder.Sql(
			"""
			UPDATE ynab."YnabSyncRecords"
			SET "SyncStatus" = 'Failed'
			WHERE "SyncStatus" = 'Unknown';
			""");

		migrationBuilder.DropCheckConstraint(
			name: "CK_YnabSyncRecords_AttemptCount",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_YnabBudgetId_YnabAccountId_ImportId",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropCheckConstraint(
			name: "CK_YnabSyncRecords_ClaimLease",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropCheckConstraint(
			name: "CK_YnabSyncRecords_PushSnapshot",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "AttemptCount",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "ClaimToken",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "ClaimedAtUtc",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "ImportId",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "LastAttemptAtUtc",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "PayloadHash",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "RequestPayloadJson",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "SourceVersion",
			schema: "ynab",
			table: "YnabSyncRecords");
	}
}
