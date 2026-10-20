using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddNormalizationAiWorkflow : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationRevision",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: false,
			defaultValueSql: "gen_random_uuid()");

		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationRevision",
			schema: "matching",
			table: "NormalizedDescriptions",
			type: "uuid",
			nullable: false,
			defaultValueSql: "gen_random_uuid()");

		migrationBuilder.CreateTable(
			name: "NormalizationAiConnection",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				Changing = table.Column<bool>(type: "boolean", nullable: false),
				Generation = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationAiConnection", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "NormalizationAiRuns",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
				OwnerUserId = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				ConnectionGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				Model = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				State = table.Column<string>(type: "text", maxLength: 24, nullable: false),
				PromptVersion = table.Column<string>(type: "text", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				CompletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
				LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				ErrorCode = table.Column<string>(type: "text", nullable: true),
				ErrorDetail = table.Column<string>(type: "text", nullable: true),
				UsageJson = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationAiRuns", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "NormalizationAiProposals",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				RunId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				SourceRevision = table.Column<Guid>(type: "uuid", nullable: false),
				SourceCanonicalId = table.Column<Guid>(type: "uuid", nullable: true),
				SourceCanonicalRevision = table.Column<Guid>(type: "uuid", nullable: true),
				SourceMatchScore = table.Column<double>(type: "double precision", nullable: true),
				Description = table.Column<string>(type: "text", nullable: false),
				CandidatesJson = table.Column<string>(type: "text", nullable: false),
				Action = table.Column<string>(type: "text", maxLength: 24, nullable: false),
				TargetId = table.Column<Guid>(type: "uuid", nullable: true),
				TargetRevision = table.Column<Guid>(type: "uuid", nullable: true),
				MatchScore = table.Column<double>(type: "double precision", nullable: true),
				CanonicalName = table.Column<string>(type: "text", nullable: true),
				DisplayLabel = table.Column<string>(type: "text", nullable: true),
				Reason = table.Column<string>(type: "text", nullable: true),
				State = table.Column<string>(type: "text", maxLength: 24, nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationAiProposals", x => x.Id);
				table.ForeignKey(
					name: "FK_NormalizationAiProposals_NormalizationAiRuns_RunId",
					column: x => x.RunId,
					principalSchema: "matching",
					principalTable: "NormalizationAiRuns",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAiProposals_ReceiptItemId",
			schema: "matching",
			table: "NormalizationAiProposals",
			column: "ReceiptItemId");

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAiProposals_RunId_ReceiptItemId",
			schema: "matching",
			table: "NormalizationAiProposals",
			columns: new[] { "RunId", "ReceiptItemId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAiRuns_OwnerUserId_ClientRequestId",
			schema: "matching",
			table: "NormalizationAiRuns",
			columns: new[] { "OwnerUserId", "ClientRequestId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationAiRuns_State_CreatedAt",
			schema: "matching",
			table: "NormalizationAiRuns",
			columns: new[] { "State", "CreatedAt" });

		// Durable versions change for every database update, including raw SQL and
		// edit/revert ABA. Portable imports omit this internal metadata and get fresh IDs.
		migrationBuilder.Sql("""
                CREATE FUNCTION matching.refresh_normalization_revision() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."NormalizationRevision" := gen_random_uuid();
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER receipt_item_normalization_revision
                BEFORE UPDATE ON receipts."ReceiptItems"
                FOR EACH ROW EXECUTE FUNCTION matching.refresh_normalization_revision();
                CREATE TRIGGER canonical_normalization_revision
                BEFORE UPDATE ON matching."NormalizedDescriptions"
                FOR EACH ROW EXECUTE FUNCTION matching.refresh_normalization_revision();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DROP TRIGGER receipt_item_normalization_revision ON receipts."ReceiptItems";
                DROP TRIGGER canonical_normalization_revision ON matching."NormalizedDescriptions";
                DROP FUNCTION matching.refresh_normalization_revision();
                """);
		migrationBuilder.DropTable(
			name: "NormalizationAiConnection",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "NormalizationAiProposals",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "NormalizationAiRuns",
			schema: "matching");

		migrationBuilder.DropColumn(
			name: "NormalizationRevision",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationRevision",
			schema: "matching",
			table: "NormalizedDescriptions");
	}
}
