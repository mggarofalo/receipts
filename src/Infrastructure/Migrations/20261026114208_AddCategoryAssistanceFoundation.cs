using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAssistanceFoundation : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.EnsureSchema(
			name: "category_assistance");

		migrationBuilder.CreateTable(
			name: "Observations",
			schema: "category_assistance",
			columns: table => new
			{
				SaveId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				Location = table.Column<string>(type: "text", nullable: false),
				Description = table.Column<string>(type: "text", nullable: false),
				ItemCode = table.Column<string>(type: "text", nullable: true),
				SourceHash = table.Column<string>(type: "text", nullable: false),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				SettingsRevision = table.Column<Guid>(type: "uuid", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				TaxonomyRevision = table.Column<Guid>(type: "uuid", nullable: false),
				Evidence = table.Column<string>(type: "text", nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Observations", x => new { x.SaveId, x.ReceiptItemId });
			});

		migrationBuilder.CreateTable(
			name: "Settings",
			schema: "category_assistance",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				Enabled = table.Column<bool>(type: "boolean", nullable: false),
				Provider = table.Column<string>(type: "text", nullable: true),
				Model = table.Column<string>(type: "text", nullable: true),
				DailyLimit = table.Column<int>(type: "integer", nullable: false),
				PendingLimit = table.Column<int>(type: "integer", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Settings", x => x.Id);
				table.CheckConstraint("CK_CategoryAssistance_Limits", "\"DailyLimit\" BETWEEN 1 AND 100 AND \"PendingLimit\" BETWEEN 1 AND 20");
				table.CheckConstraint("CK_CategoryAssistance_Singleton", "\"Id\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "SettingsAudit",
			schema: "category_assistance",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				PreviousRevision = table.Column<Guid>(type: "uuid", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				Before = table.Column<string>(type: "text", nullable: false),
				After = table.Column<string>(type: "text", nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_SettingsAudit", x => x.Id);
			});

		migrationBuilder.InsertData(
			schema: "category_assistance",
			table: "Settings",
			columns: new[] { "Id", "DailyLimit", "Enabled", "Model", "PendingLimit", "Provider", "RestoreGeneration", "Revision" },
			values: new object[] { 1, 10, false, null, 3, null, new Guid("5c5ebfbf-0833-432d-aefc-c388be8e0460"), new Guid("4bb4909f-fb40-433c-a98d-d48a2bd0cd48") });

		migrationBuilder.CreateIndex(
			name: "IX_Observations_SourceHash",
			schema: "category_assistance",
			table: "Observations",
			column: "SourceHash");

		migrationBuilder.CreateIndex(
			name: "IX_SettingsAudit_Revision",
			schema: "category_assistance",
			table: "SettingsAudit",
			column: "Revision",
			unique: true);
		migrationBuilder.Sql("""
                CREATE FUNCTION category_assistance.reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Category assistance settings audit is append-only'; END $$;
                CREATE TRIGGER settings_audit_immutable BEFORE UPDATE OR DELETE ON category_assistance."SettingsAudit"
                FOR EACH ROW EXECUTE FUNCTION category_assistance.reject_audit_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."SettingsAudit") THEN
                        RAISE EXCEPTION 'Category assistance settings audit must be retained; downgrade refused';
                    END IF;
                END $$;
                """);
		migrationBuilder.Sql("DROP FUNCTION category_assistance.reject_audit_mutation() CASCADE");
		migrationBuilder.DropTable(
			name: "Observations",
			schema: "category_assistance");

		migrationBuilder.DropTable(
			name: "Settings",
			schema: "category_assistance");

		migrationBuilder.DropTable(
			name: "SettingsAudit",
			schema: "category_assistance");
	}
}
