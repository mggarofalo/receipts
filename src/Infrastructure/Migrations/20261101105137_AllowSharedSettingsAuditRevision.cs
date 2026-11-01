using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// A category assistance settings save that changes only a limit keeps the settings revision, so several
/// audit rows may share one. The revision index stays, without uniqueness.
/// </summary>
public partial class AllowSharedSettingsAuditRevision : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_SettingsAudit_Revision",
			schema: "category_assistance",
			table: "SettingsAudit");

		migrationBuilder.CreateIndex(
			name: "IX_SettingsAudit_Revision",
			schema: "category_assistance",
			table: "SettingsAudit",
			column: "Revision");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                LOCK TABLE category_assistance."SettingsAudit" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."SettingsAudit" GROUP BY "Revision" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Limits-only settings saves share a revision in the audit' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);

		migrationBuilder.DropIndex(
			name: "IX_SettingsAudit_Revision",
			schema: "category_assistance",
			table: "SettingsAudit");

		migrationBuilder.CreateIndex(
			name: "IX_SettingsAudit_Revision",
			schema: "category_assistance",
			table: "SettingsAudit",
			column: "Revision",
			unique: true);
	}
}
