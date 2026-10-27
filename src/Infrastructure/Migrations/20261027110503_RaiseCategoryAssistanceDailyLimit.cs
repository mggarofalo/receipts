using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class RaiseCategoryAssistanceDailyLimit : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_Limits",
			schema: "category_assistance",
			table: "Settings");

		// Only update the original, untouched seed. An administrator's
		// saved choice (including an explicit 10) has a new revision.
		migrationBuilder.Sql("""
                UPDATE category_assistance."Settings" SET "DailyLimit" = 100
                WHERE "Id" = 1 AND "DailyLimit" = 10 AND NOT "Enabled"
                  AND "Provider" IS NULL AND "Model" IS NULL AND "PendingLimit" = 3
                  AND "Revision" = '4bb4909f-fb40-433c-a98d-d48a2bd0cd48'
                  AND "RestoreGeneration" = '5c5ebfbf-0833-432d-aefc-c388be8e0460';
                """);

		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_Limits",
			schema: "category_assistance",
			table: "Settings",
			sql: "\"DailyLimit\" BETWEEN 1 AND 5000 AND \"PendingLimit\" BETWEEN 1 AND 20");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM category_assistance."Settings" WHERE "DailyLimit" > 100) THEN
                    RAISE EXCEPTION 'Lower the category assistance daily limit to 100 or less before downgrading.';
                  END IF;
                END $$;
                """);
		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_Limits",
			schema: "category_assistance",
			table: "Settings");

		migrationBuilder.Sql("""
                UPDATE category_assistance."Settings" SET "DailyLimit" = 10
                WHERE "Id" = 1 AND "DailyLimit" = 100 AND NOT "Enabled"
                  AND "Provider" IS NULL AND "Model" IS NULL AND "PendingLimit" = 3
                  AND "Revision" = '4bb4909f-fb40-433c-a98d-d48a2bd0cd48'
                  AND "RestoreGeneration" = '5c5ebfbf-0833-432d-aefc-c388be8e0460';
                """);

		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_Limits",
			schema: "category_assistance",
			table: "Settings",
			sql: "\"DailyLimit\" BETWEEN 1 AND 100 AND \"PendingLimit\" BETWEEN 1 AND 20");
	}
}
