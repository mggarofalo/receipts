using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddEnvelopes : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.EnsureSchema(
			name: "budget");

		migrationBuilder.AddColumn<Guid>(
			name: "OccasionId",
			schema: "receipts",
			table: "Receipts",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "EnvelopeSource",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 16,
			nullable: false,
			// Existing purchases are historical; the envelope backfill then records their envelope.
			defaultValue: "Historical");

		migrationBuilder.AddColumn<Guid>(
			name: "OccasionId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "EnvelopeBackfill",
			schema: "budget",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				CompletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				ItemCount = table.Column<int>(type: "integer", nullable: false),
				EnvelopeCount = table.Column<int>(type: "integer", nullable: false),
				DefaultCount = table.Column<int>(type: "integer", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_EnvelopeBackfill", x => x.Id);
				table.CheckConstraint("CK_EnvelopeBackfill_Singleton", "\"Id\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "Envelopes",
			schema: "budget",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Name = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				IsActive = table.Column<bool>(type: "boolean", nullable: false),
				DeletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				DeletedByUserId = table.Column<string>(type: "text", nullable: true),
				DeletedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
				CascadeDeletedByParentId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Envelopes", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "Occasions",
			schema: "budget",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Name = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				IsActive = table.Column<bool>(type: "boolean", nullable: false),
				DeletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				DeletedByUserId = table.Column<string>(type: "text", nullable: true),
				DeletedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
				CascadeDeletedByParentId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Occasions", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "ItemTypeEnvelopeDefaults",
			schema: "budget",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
				EnvelopeId = table.Column<Guid>(type: "uuid", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ItemTypeEnvelopeDefaults", x => x.Id);
				table.ForeignKey(
					name: "FK_ItemTypeEnvelopeDefaults_Envelopes_EnvelopeId",
					column: x => x.EnvelopeId,
					principalSchema: "budget",
					principalTable: "Envelopes",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_ItemTypeEnvelopeDefaults_Identities_CategoryId",
					column: x => x.CategoryId,
					principalSchema: "taxonomy",
					principalTable: "Identities",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "YnabEnvelopeMappings",
			schema: "ynab",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				EnvelopeId = table.Column<Guid>(type: "uuid", nullable: false),
				YnabBudgetId = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				YnabCategoryId = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				YnabCategoryName = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				YnabCategoryGroupName = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabEnvelopeMappings", x => x.Id);
				table.ForeignKey(
					name: "FK_YnabEnvelopeMappings_Envelopes_EnvelopeId",
					column: x => x.EnvelopeId,
					principalSchema: "budget",
					principalTable: "Envelopes",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_Receipts_OccasionId",
			schema: "receipts",
			table: "Receipts",
			column: "OccasionId");

		migrationBuilder.CreateIndex(
			name: "IX_Receipts_PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts",
			column: "PaidFromEnvelopeId");

		migrationBuilder.CreateIndex(
			name: "IX_ReceiptItems_EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems",
			column: "EnvelopeId");

		migrationBuilder.CreateIndex(
			name: "IX_ReceiptItems_OccasionId",
			schema: "receipts",
			table: "ReceiptItems",
			column: "OccasionId");

		migrationBuilder.CreateIndex(
			name: "IX_Envelopes_Name",
			schema: "budget",
			table: "Envelopes",
			column: "Name",
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_ItemTypeEnvelopeDefaults_CategoryId",
			schema: "budget",
			table: "ItemTypeEnvelopeDefaults",
			column: "CategoryId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_ItemTypeEnvelopeDefaults_EnvelopeId",
			schema: "budget",
			table: "ItemTypeEnvelopeDefaults",
			column: "EnvelopeId");

		migrationBuilder.CreateIndex(
			name: "IX_Occasions_Name",
			schema: "budget",
			table: "Occasions",
			column: "Name",
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_YnabEnvelopeMappings_EnvelopeId_YnabBudgetId",
			schema: "ynab",
			table: "YnabEnvelopeMappings",
			columns: new[] { "EnvelopeId", "YnabBudgetId" },
			unique: true);

		migrationBuilder.AddForeignKey(
			name: "FK_ReceiptItems_Envelopes_EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems",
			column: "EnvelopeId",
			principalSchema: "budget",
			principalTable: "Envelopes",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);

		migrationBuilder.AddForeignKey(
			name: "FK_ReceiptItems_Occasions_OccasionId",
			schema: "receipts",
			table: "ReceiptItems",
			column: "OccasionId",
			principalSchema: "budget",
			principalTable: "Occasions",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);

		migrationBuilder.AddForeignKey(
			name: "FK_Receipts_Envelopes_PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts",
			column: "PaidFromEnvelopeId",
			principalSchema: "budget",
			principalTable: "Envelopes",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);

		migrationBuilder.AddForeignKey(
			name: "FK_Receipts_Occasions_OccasionId",
			schema: "receipts",
			table: "Receipts",
			column: "OccasionId",
			principalSchema: "budget",
			principalTable: "Occasions",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);

		// Budget attribution is not normalization or classification evidence: changing only an
		// envelope or occasion keeps the row's normalization revision, like canonical embeddings.
		migrationBuilder.Sql(RefreshNormalizationRevision(preserveBudgetAttribution: true));
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(RefreshNormalizationRevision(preserveBudgetAttribution: false));

		migrationBuilder.DropForeignKey(
			name: "FK_ReceiptItems_Envelopes_EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropForeignKey(
			name: "FK_ReceiptItems_Occasions_OccasionId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropForeignKey(
			name: "FK_Receipts_Envelopes_PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropForeignKey(
			name: "FK_Receipts_Occasions_OccasionId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropTable(
			name: "EnvelopeBackfill",
			schema: "budget");

		migrationBuilder.DropTable(
			name: "ItemTypeEnvelopeDefaults",
			schema: "budget");

		migrationBuilder.DropTable(
			name: "Occasions",
			schema: "budget");

		migrationBuilder.DropTable(
			name: "YnabEnvelopeMappings",
			schema: "ynab");

		migrationBuilder.DropTable(
			name: "Envelopes",
			schema: "budget");

		migrationBuilder.DropIndex(
			name: "IX_Receipts_OccasionId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropIndex(
			name: "IX_Receipts_PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropIndex(
			name: "IX_ReceiptItems_EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropIndex(
			name: "IX_ReceiptItems_OccasionId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "OccasionId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "PaidFromEnvelopeId",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "EnvelopeId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "EnvelopeSource",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "OccasionId",
			schema: "receipts",
			table: "ReceiptItems");
	}

	private static string RefreshNormalizationRevision(bool preserveBudgetAttribution)
	{
		string preserve = preserveBudgetAttribution ? """
                    IF TG_OP = 'UPDATE' AND NEW."NormalizationRevision" IS NOT DISTINCT FROM OLD."NormalizationRevision"
                        AND (to_jsonb(NEW) - ARRAY['NormalizationRevision', 'EnvelopeId', 'EnvelopeSource', 'OccasionId', 'PaidFromEnvelopeId'])
                            = (to_jsonb(OLD) - ARRAY['NormalizationRevision', 'EnvelopeId', 'EnvelopeSource', 'OccasionId', 'PaidFromEnvelopeId']) THEN
                        RETURN NEW;
                    END IF;
                """ : "";
		return """
            CREATE OR REPLACE FUNCTION matching.refresh_normalization_revision() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE restore_id uuid;
            BEGIN
            -- preserve
                IF NEW."NormalizationRevision" IS DISTINCT FROM OLD."NormalizationRevision"
                    AND (to_jsonb(NEW) - 'NormalizationRevision') = (to_jsonb(OLD) - 'NormalizationRevision') THEN
                    SELECT "Id" INTO restore_id FROM matching."SourceRevisionRestores"
                    WHERE "SourceTable" = TG_TABLE_NAME AND "SourceId" = NEW."Id"
                        AND "PreviousRevision" = OLD."NormalizationRevision" AND "RestoredRevision" = NEW."NormalizationRevision"
                        AND "Transaction" = pg_current_xact_id() AND NOT "Consumed"
                    LIMIT 1 FOR UPDATE;
                    IF restore_id IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = pg_backend_pid()
                            AND relation = TG_RELID AND mode = 'ExclusiveLock' AND granted) THEN
                            RAISE EXCEPTION 'Source restore requires exclusive table lock';
                        END IF;
                        UPDATE matching."SourceRevisionRestores" SET "Consumed" = true WHERE "Id" = restore_id;
                        RETURN NEW;
                    END IF;
                END IF;
                NEW."NormalizationRevision" := gen_random_uuid();
                RETURN NEW;
            END;
            $$;
            """.Replace("-- preserve", preserve, StringComparison.Ordinal);
	}
}
