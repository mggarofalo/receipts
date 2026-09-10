using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class ScopeYnabDestinationIdentity : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// Earlier request validators accepted every Guid.TryParse spelling and mapping
		// endpoints accepted arbitrary strings. Canonicalize only UUID-shaped values;
		// leave legacy non-UUID values intact so this migration never destroys ownership.
		migrationBuilder.Sql(
			"""
			UPDATE ynab."YnabSelectedBudgets"
			SET "BudgetId" = CASE
				WHEN btrim("BudgetId") ~* '^\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\}$'
				THEN regexp_replace(btrim("BudgetId"), '0x|[{},]', '', 'gi')::uuid::text
				ELSE trim(both '{}()' from btrim("BudgetId"))::uuid::text END
			WHERE btrim("BudgetId") ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)|\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\})$';

			UPDATE ynab."YnabAccountMappings"
			SET "YnabBudgetId" = CASE
				WHEN btrim("YnabBudgetId") ~* '^\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\}$'
				THEN regexp_replace(btrim("YnabBudgetId"), '0x|[{},]', '', 'gi')::uuid::text
				ELSE trim(both '{}()' from btrim("YnabBudgetId"))::uuid::text END
			WHERE btrim("YnabBudgetId") ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)|\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\})$';

			UPDATE ynab."YnabCategoryMappings"
			SET "YnabBudgetId" = CASE
				WHEN btrim("YnabBudgetId") ~* '^\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\}$'
				THEN regexp_replace(btrim("YnabBudgetId"), '0x|[{},]', '', 'gi')::uuid::text
				ELSE trim(both '{}()' from btrim("YnabBudgetId"))::uuid::text END
			WHERE btrim("YnabBudgetId") ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)|\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\})$';

			UPDATE ynab."YnabSyncRecords"
			SET "YnabBudgetId" = CASE
				WHEN btrim("YnabBudgetId") ~* '^\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\}$'
				THEN regexp_replace(btrim("YnabBudgetId"), '0x|[{},]', '', 'gi')::uuid::text
				ELSE trim(both '{}()' from btrim("YnabBudgetId"))::uuid::text END
			WHERE btrim("YnabBudgetId") ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)|\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\{0x[0-9a-f]{2}(,0x[0-9a-f]{2}){7}\}\})$';
			""");

		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropIndex(
			name: "IX_YnabCategoryMappings_ReceiptsCategory",
			schema: "ynab",
			table: "YnabCategoryMappings");

		migrationBuilder.DropIndex(
			name: "IX_YnabAccountMappings_ReceiptsAccountId",
			schema: "ynab",
			table: "YnabAccountMappings");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType_YnabBudgetId",
			schema: "ynab",
			table: "YnabSyncRecords",
			columns: new[] { "LocalTransactionId", "SyncType", "YnabBudgetId" },
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId",
			schema: "ynab",
			table: "YnabCategoryMappings",
			columns: new[] { "ReceiptsCategory", "YnabBudgetId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_YnabAccountMappings_ReceiptsAccountId_YnabBudgetId",
			schema: "ynab",
			table: "YnabAccountMappings",
			columns: new[] { "ReceiptsAccountId", "YnabBudgetId" },
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Refuse atomically before changing indexes when normal multi-budget use has
		// created rows the destination-agnostic predecessor cannot represent.
		migrationBuilder.Sql(
			"""
			DO $$
			BEGIN
				IF EXISTS (
					SELECT 1 FROM ynab."YnabAccountMappings"
					GROUP BY "ReceiptsAccountId" HAVING count(*) > 1)
					OR EXISTS (
					SELECT 1 FROM ynab."YnabCategoryMappings"
					GROUP BY "ReceiptsCategory" HAVING count(*) > 1)
					OR EXISTS (
					SELECT 1 FROM ynab."YnabSyncRecords"
					WHERE "DeletedAt" IS NULL
					GROUP BY "LocalTransactionId", "SyncType" HAVING count(*) > 1)
				THEN
					RAISE EXCEPTION 'Cannot downgrade ScopeYnabDestinationIdentity while multiple YNAB budget bindings exist. Remove the extra destination bindings explicitly before retrying.';
				END IF;
			END $$;
			""");

		migrationBuilder.DropIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType_YnabBudgetId",
			schema: "ynab",
			table: "YnabSyncRecords");

		migrationBuilder.DropIndex(
			name: "IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId",
			schema: "ynab",
			table: "YnabCategoryMappings");

		migrationBuilder.DropIndex(
			name: "IX_YnabAccountMappings_ReceiptsAccountId_YnabBudgetId",
			schema: "ynab",
			table: "YnabAccountMappings");

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			schema: "ynab",
			table: "YnabSyncRecords",
			columns: new[] { "LocalTransactionId", "SyncType" },
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_YnabCategoryMappings_ReceiptsCategory",
			schema: "ynab",
			table: "YnabCategoryMappings",
			column: "ReceiptsCategory",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_YnabAccountMappings_ReceiptsAccountId",
			schema: "ynab",
			table: "YnabAccountMappings",
			column: "ReceiptsAccountId",
			unique: true);
	}
}
