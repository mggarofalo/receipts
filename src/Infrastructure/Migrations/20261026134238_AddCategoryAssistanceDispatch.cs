using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAssistanceDispatch : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "AttemptId",
			schema: "category_assistance",
			table: "Observations",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<bool>(
			name: "DispatchSkipped",
			schema: "category_assistance",
			table: "Observations",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.CreateTable(
			name: "Attempts",
			schema: "category_assistance",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				RequestId = table.Column<Guid>(type: "uuid", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				SourceHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Location = table.Column<string>(type: "text", maxLength: 1000, nullable: false),
				Description = table.Column<string>(type: "text", maxLength: 2000, nullable: false),
				ItemCodeKey = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				SourceRevision = table.Column<Guid>(type: "uuid", nullable: false),
				TaxonomyRevision = table.Column<Guid>(type: "uuid", nullable: false),
				Provider = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				Model = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				QuestionPolicyVersion = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				SettingsRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ConnectionGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				RequestJson = table.Column<string>(type: "text", nullable: false),
				ResultJson = table.Column<string>(type: "text", nullable: true),
				State = table.Column<string>(type: "text", nullable: false),
				ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
				LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				NextEligibleAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				Deferrals = table.Column<int>(type: "integer", nullable: false),
				DebitUtcDay = table.Column<DateOnly>(type: "date", nullable: true),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				CompletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Attempts", x => x.Id);
				table.CheckConstraint("CK_CategoryAssistance_AttemptState", "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate')");
			});

		migrationBuilder.CreateTable(
			name: "Connection",
			schema: "category_assistance",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				Provider = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				Model = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				ConfigurationFingerprint = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Generation = table.Column<Guid>(type: "uuid", nullable: false),
				Changing = table.Column<bool>(type: "boolean", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Connection", x => x.Id);
				table.CheckConstraint("CK_CategoryAssistance_ConnectionSingleton", "\"Id\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "DailyUsage",
			schema: "category_assistance",
			columns: table => new
			{
				UtcDay = table.Column<DateOnly>(type: "date", nullable: false),
				DispatchAttempts = table.Column<int>(type: "integer", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_DailyUsage", x => x.UtcDay);
				table.CheckConstraint("CK_CategoryAssistance_NonnegativeUsage", "\"DispatchAttempts\" >= 0");
			});

		migrationBuilder.InsertData(
			schema: "category_assistance",
			table: "Connection",
			columns: new[] { "Id", "Changing", "ConfigurationFingerprint", "Generation", "Model", "Provider" },
			values: new object[] { 1, true, "", new Guid("00000000-0000-0000-0000-000000000000"), "", "" });

		migrationBuilder.CreateIndex(
			name: "IX_Observations_AttemptId",
			schema: "category_assistance",
			table: "Observations",
			column: "AttemptId");

		migrationBuilder.CreateIndex(
			name: "IX_Attempts_RequestId",
			schema: "category_assistance",
			table: "Attempts",
			column: "RequestId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~",
			schema: "category_assistance",
			table: "Attempts",
			columns: new[] { "RestoreGeneration", "SourceHash", "TaxonomyRevision", "Provider", "Model", "QuestionPolicyVersion", "ConnectionGeneration" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_Attempts_State_NextEligibleAt_LeaseExpiresAt",
			schema: "category_assistance",
			table: "Attempts",
			columns: new[] { "State", "NextEligibleAt", "LeaseExpiresAt" });

		migrationBuilder.AddForeignKey(
			name: "FK_Observations_Attempts_AttemptId",
			schema: "category_assistance",
			table: "Observations",
			column: "AttemptId",
			principalSchema: "category_assistance",
			principalTable: "Attempts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."Attempts")
                        OR EXISTS (SELECT 1 FROM category_assistance."DailyUsage") THEN
                        RAISE EXCEPTION 'Category assistance dispatch evidence is retained; downgrade would erase attempts or usage';
                    END IF;
                END $$;
                """);

		migrationBuilder.DropForeignKey(
			name: "FK_Observations_Attempts_AttemptId",
			schema: "category_assistance",
			table: "Observations");

		migrationBuilder.DropTable(
			name: "Attempts",
			schema: "category_assistance");

		migrationBuilder.DropTable(
			name: "Connection",
			schema: "category_assistance");

		migrationBuilder.DropTable(
			name: "DailyUsage",
			schema: "category_assistance");

		migrationBuilder.DropIndex(
			name: "IX_Observations_AttemptId",
			schema: "category_assistance",
			table: "Observations");

		migrationBuilder.DropColumn(
			name: "AttemptId",
			schema: "category_assistance",
			table: "Observations");

		migrationBuilder.DropColumn(
			name: "DispatchSkipped",
			schema: "category_assistance",
			table: "Observations");
	}
}
