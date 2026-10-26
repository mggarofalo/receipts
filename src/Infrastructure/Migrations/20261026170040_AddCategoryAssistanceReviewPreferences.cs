using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAssistanceReviewPreferences : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "ReviewOperations",
			schema: "category_assistance",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				ResultJson = table.Column<string>(type: "text", nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ReviewOperations", x => new { x.Actor, x.OperationId });
				table.ForeignKey(
					name: "FK_ReviewOperations_Attempts_AttemptId",
					column: x => x.AttemptId,
					principalSchema: "category_assistance",
					principalTable: "Attempts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "ReviewPreferences",
			schema: "category_assistance",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
				State = table.Column<string>(type: "text", maxLength: 20, nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				ExcludedItemIdsJson = table.Column<string>(type: "text", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ReviewPreferences", x => new { x.Actor, x.AttemptId });
				table.CheckConstraint("CK_CategoryAssistance_ReviewState", "\"State\" IN ('active','later','left_as_is')");
				table.ForeignKey(
					name: "FK_ReviewPreferences_Attempts_AttemptId",
					column: x => x.AttemptId,
					principalSchema: "category_assistance",
					principalTable: "Attempts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_ReviewOperations_AttemptId",
			schema: "category_assistance",
			table: "ReviewOperations",
			column: "AttemptId");

		migrationBuilder.CreateIndex(
			name: "IX_ReviewPreferences_AttemptId",
			schema: "category_assistance",
			table: "ReviewPreferences",
			column: "AttemptId");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."ReviewPreferences") OR
                       EXISTS (SELECT 1 FROM category_assistance."ReviewOperations") THEN
                        RAISE EXCEPTION 'Cannot downgrade: category assistance review preferences must be retained.';
                    END IF;
                END $$;
                """);
		migrationBuilder.DropTable(
			name: "ReviewOperations",
			schema: "category_assistance");

		migrationBuilder.DropTable(
			name: "ReviewPreferences",
			schema: "category_assistance");
	}
}
