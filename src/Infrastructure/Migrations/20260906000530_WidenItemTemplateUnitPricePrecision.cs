using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class WidenItemTemplateUnitPricePrecision : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// Preflight and alteration share the migration transaction and table lock.
		migrationBuilder.Sql("""
                LOCK TABLE library."ItemTemplates" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                DECLARE incompatible_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO incompatible_count
                    FROM library."ItemTemplates"
                    WHERE abs("DefaultUnitPrice") >= 100000000000000;
                    RAISE NOTICE 'RECEIPTS-965 precision precondition: % out-of-range template price(s), including trash.', incompatible_count;
                    IF incompatible_count <> 0 THEN
                        RAISE EXCEPTION 'RECEIPTS-965 cannot widen template price scale: % price(s) exceed the new range, including trash. Reconcile these values before retrying.', incompatible_count;
                    END IF;
                END $migration$;
                """);

		migrationBuilder.AlterColumn<decimal>(
			name: "DefaultUnitPrice",
			schema: "library",
			table: "ItemTemplates",
			type: "numeric(18,4)",
			nullable: true,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,2)",
			oldNullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Refuse silent sub-cent rounding, including values on soft-deleted templates.
		migrationBuilder.Sql("""
                LOCK TABLE library."ItemTemplates" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                DECLARE incompatible_count bigint;
                BEGIN
                    SELECT COUNT(*) INTO incompatible_count
                    FROM library."ItemTemplates"
                    WHERE "DefaultUnitPrice" <> round("DefaultUnitPrice", 2);
                    RAISE NOTICE 'RECEIPTS-965 rollback precondition: % sub-cent template price(s), including trash.', incompatible_count;
                    IF incompatible_count <> 0 THEN
                        RAISE EXCEPTION 'RECEIPTS-965 cannot roll back template price precision: % sub-cent price(s), including trash. Preserve the current schema or reconcile these values before retrying.', incompatible_count;
                    END IF;
                END $migration$;
                """);

		migrationBuilder.AlterColumn<decimal>(
			name: "DefaultUnitPrice",
			schema: "library",
			table: "ItemTemplates",
			type: "numeric(18,2)",
			nullable: true,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,4)",
			oldNullable: true);
	}
}
