using System;
using Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;



public partial class AddStableYnabCategoryMapping : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(name: "ReceiptsCategoryId", table: "YnabCategoryMappings", schema: "ynab", type: "uuid", nullable: true);
		migrationBuilder.AddColumn<string>(name: "LegacyResolution", table: "YnabCategoryMappings", schema: "ynab", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Unmatched");
		migrationBuilder.AddColumn<string>(name: "LegacyCandidateCategoryIds", table: "YnabCategoryMappings", schema: "ynab", type: "jsonb", nullable: false, defaultValue: "[]");

		// Candidate evidence comes from the current taxonomy revision. Archived identities
		// participate, so a historical duplicate can never be silently guessed away.
		migrationBuilder.Sql("""
            WITH candidates AS (
                SELECT m."Id", count(DISTINCT n."CategoryId") AS candidate_count,
                       jsonb_agg(DISTINCT n."CategoryId" ORDER BY n."CategoryId")
                           FILTER (WHERE n."CategoryId" IS NOT NULL) AS ids,
                       (array_agg(DISTINCT n."CategoryId" ORDER BY n."CategoryId") FILTER (WHERE n."CategoryId" IS NOT NULL))[1] AS unique_id
                FROM ynab."YnabCategoryMappings" m
                LEFT JOIN taxonomy."Head" h ON h."Id" = 1
                LEFT JOIN taxonomy."Nodes" n ON n."RevisionId" = h."RevisionId"
                    AND n."Name" = m."ReceiptsCategory" COLLATE "C"
                GROUP BY m."Id"
            ), scoped AS (
                SELECT c.*, count(*) FILTER (WHERE c.candidate_count = 1)
                    OVER (PARTITION BY m."YnabBudgetId", c.unique_id) AS target_count
                FROM candidates c JOIN ynab."YnabCategoryMappings" m ON m."Id" = c."Id"
            )
            UPDATE ynab."YnabCategoryMappings" m
            SET "ReceiptsCategoryId" = CASE WHEN c.candidate_count = 1 AND c.target_count = 1 THEN c.unique_id ELSE NULL END,
                "LegacyResolution" = CASE WHEN c.candidate_count = 1 AND c.target_count = 1 THEN 'Unique'
                    WHEN c.candidate_count > 1 OR c.target_count > 1 THEN 'Ambiguous' ELSE 'Unmatched' END,
                "LegacyCandidateCategoryIds" = COALESCE(c.ids, '[]'::jsonb)
            FROM scoped c WHERE c."Id" = m."Id";
            """);
		migrationBuilder.DropIndex("IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId", "YnabCategoryMappings", "ynab");
		migrationBuilder.CreateIndex(name: "IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId", table: "YnabCategoryMappings", schema: "ynab", columns: new[] { "ReceiptsCategory", "YnabBudgetId" });
		migrationBuilder.CreateIndex(name: "IX_YnabCategoryMappings_ReceiptsCategoryId_YnabBudgetId", table: "YnabCategoryMappings", schema: "ynab", columns: new[] { "ReceiptsCategoryId", "YnabBudgetId" }, unique: true, filter: "\"ReceiptsCategoryId\" IS NOT NULL");
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// The old unique name key cannot represent valid distinct stable IDs with
		// the same display text. Abort the downgrade before dropping any columns.
		migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM ynab."YnabCategoryMappings"
                    GROUP BY "YnabBudgetId", "ReceiptsCategory"
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Cannot downgrade stable YNAB mappings: duplicate legacy names exist in a budget. Resolve them explicitly before retrying.';
                END IF;
            END $$;
            """);
		migrationBuilder.DropIndex("IX_YnabCategoryMappings_ReceiptsCategoryId_YnabBudgetId", "YnabCategoryMappings", "ynab");
		migrationBuilder.DropIndex("IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId", "YnabCategoryMappings", "ynab");
		migrationBuilder.CreateIndex(name: "IX_YnabCategoryMappings_ReceiptsCategory_YnabBudgetId", table: "YnabCategoryMappings", schema: "ynab", columns: new[] { "ReceiptsCategory", "YnabBudgetId" }, unique: true);
		migrationBuilder.DropColumn("ReceiptsCategoryId", "YnabCategoryMappings", "ynab");
		migrationBuilder.DropColumn("LegacyResolution", "YnabCategoryMappings", "ynab");
		migrationBuilder.DropColumn("LegacyCandidateCategoryIds", "YnabCategoryMappings", "ynab");
	}
}


