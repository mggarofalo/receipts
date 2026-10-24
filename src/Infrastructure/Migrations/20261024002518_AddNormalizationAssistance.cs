using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddNormalizationAssistance : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "NormalizationAssistanceAttempts",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				OwnerUserId = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				SourceHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Location = table.Column<string>(type: "text", maxLength: 1000, nullable: false),
				Description = table.Column<string>(type: "text", maxLength: 2000, nullable: false),
				ItemCode = table.Column<string>(type: "text", maxLength: 200, nullable: true),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
				PreferenceRevision = table.Column<Guid>(type: "uuid", nullable: false),
				Model = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				State = table.Column<string>(type: "text", maxLength: 32, nullable: false),
				RunId = table.Column<Guid>(type: "uuid", nullable: true),
				AdmissionAttempts = table.Column<int>(type: "integer", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				ErrorCode = table.Column<string>(type: "text", maxLength: 100, nullable: true),
				ErrorDetail = table.Column<string>(type: "text", maxLength: 1000, nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationAssistanceAttempts", x => x.Id);
				table.ForeignKey(
					name: "FK_NormalizationAssistanceAttempts_NormalizationAiRuns_RunId",
					column: x => x.RunId,
					principalSchema: "matching",
					principalTable: "NormalizationAiRuns",
					principalColumn: "Id",
					onDelete: ReferentialAction.SetNull);
			});

		migrationBuilder.CreateTable(
			name: "NormalizationAssistancePreferences",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				OwnerUserId = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				Enabled = table.Column<bool>(type: "boolean", nullable: false),
				Model = table.Column<string>(type: "text", maxLength: 200, nullable: true),
				DailyLimit = table.Column<int>(type: "integer", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationAssistancePreferences", x => x.Id);
			});

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAssistanceAttempts_ClientRequestId",
			schema: "matching",
			table: "NormalizationAssistanceAttempts",
			column: "ClientRequestId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAssistanceAttempts_OwnerUserId_SourceHash",
			schema: "matching",
			table: "NormalizationAssistanceAttempts",
			columns: new[] { "OwnerUserId", "SourceHash" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAssistanceAttempts_RunId",
			schema: "matching",
			table: "NormalizationAssistanceAttempts",
			column: "RunId");

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAssistanceAttempts_State_NextAttemptAt",
			schema: "matching",
			table: "NormalizationAssistanceAttempts",
			columns: new[] { "State", "NextAttemptAt" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "NormalizationAssistanceAttempts",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "NormalizationAssistancePreferences",
			schema: "matching");
	}
}
