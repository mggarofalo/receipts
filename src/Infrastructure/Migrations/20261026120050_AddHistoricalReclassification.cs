using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalReclassification : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "HistoricalOperations",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				SelectionJson = table.Column<string>(type: "jsonb", nullable: false),
				TaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				CapturedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				Reason = table.Column<string>(type: "text", nullable: false),
				State = table.Column<string>(type: "text", nullable: false),
				UndoTaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
				UndoReason = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalOperations", x => x.Id);
				table.ForeignKey(
					name: "FK_HistoricalOperations_Revisions_TaxonomyRevisionId",
					column: x => x.TaxonomyRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_HistoricalOperations_Revisions_UndoTaxonomyRevisionId",
					column: x => x.UndoTaxonomyRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "HistoricalOperationItems",
			schema: "taxonomy",
			columns: table => new
			{
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RawDescription = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ExpectedAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				PreviousCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
				TargetCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
				ApplyOperationId = table.Column<Guid>(type: "uuid", nullable: false),
				UndoOperationId = table.Column<Guid>(type: "uuid", nullable: false),
				State = table.Column<string>(type: "text", nullable: false),
				AppliedAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				Reason = table.Column<string>(type: "text", nullable: true),
				UndoState = table.Column<string>(type: "text", nullable: false),
				UndoAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				UndoReason = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalOperationItems", x => new { x.OperationId, x.ItemId });
				table.ForeignKey(
					name: "FK_HistoricalOperationItems_Assignments_AppliedAssignmentId",
					column: x => x.AppliedAssignmentId,
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_HistoricalOperationItems_Assignments_ExpectedAssignmentId",
					column: x => x.ExpectedAssignmentId,
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_HistoricalOperationItems_Assignments_UndoAssignmentId",
					column: x => x.UndoAssignmentId,
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_HistoricalOperationItems_HistoricalOperations_OperationId",
					column: x => x.OperationId,
					principalSchema: "taxonomy",
					principalTable: "HistoricalOperations",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_AppliedAssignmentId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "AppliedAssignmentId");

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_ApplyOperationId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "ApplyOperationId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_ExpectedAssignmentId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "ExpectedAssignmentId");

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_OperationId_State_ItemId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			columns: new[] { "OperationId", "State", "ItemId" });

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_OperationId_State_UndoState_ItemId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			columns: new[] { "OperationId", "State", "UndoState", "ItemId" });

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_UndoAssignmentId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "UndoAssignmentId");

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_UndoOperationId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "UndoOperationId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperations_TaxonomyRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations",
			column: "TaxonomyRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperations_UndoTaxonomyRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations",
			column: "UndoTaxonomyRevisionId");

		// Retain captured intent while allowing the runner to persist progress in stages.
		migrationBuilder.Sql("""
                CREATE FUNCTION taxonomy.guard_historical_operation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                        RAISE EXCEPTION 'Historical operation evidence is retained' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'UPDATE' THEN
                        IF ROW(NEW."Id", NEW."Actor" COLLATE "C", NEW."RequestHash" COLLATE "C",
                               NEW."SelectionJson", NEW."TaxonomyRevisionId", NEW."CapturedAt", NEW."Reason" COLLATE "C")
                           IS DISTINCT FROM
                           ROW(OLD."Id", OLD."Actor" COLLATE "C", OLD."RequestHash" COLLATE "C",
                               OLD."SelectionJson", OLD."TaxonomyRevisionId", OLD."CapturedAt", OLD."Reason" COLLATE "C") THEN
                            RAISE EXCEPTION 'Historical operation intent is immutable' USING ERRCODE = '23514';
                        END IF;
                        IF OLD."UndoTaxonomyRevisionId" IS NOT NULL AND
                           ROW(NEW."UndoTaxonomyRevisionId", NEW."UndoReason" COLLATE "C") IS DISTINCT FROM
                           ROW(OLD."UndoTaxonomyRevisionId", OLD."UndoReason" COLLATE "C") THEN
                            RAISE EXCEPTION 'Historical undo intent is immutable' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."State" IS DISTINCT FROM OLD."State" AND NOT (
                            (OLD."State" = 'Preview' AND NEW."State" IN ('Applying', 'Cancelled')) OR
                            (OLD."State" = 'Applying' AND NEW."State" IN ('Completed', 'Cancelled')) OR
                            (OLD."State" = 'Cancelled' AND NEW."State" = 'Undoing') OR
                            (OLD."State" = 'Completed' AND NEW."State" = 'Undoing') OR
                            (OLD."State" = 'Undoing' AND NEW."State" = 'Undone')) THEN
                            RAISE EXCEPTION 'Invalid historical operation state transition' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF (NEW."UndoTaxonomyRevisionId" IS NULL) <> (NEW."UndoReason" IS NULL) OR
                       (NEW."UndoReason" IS NOT NULL AND btrim(NEW."UndoReason",
                           U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') = '') OR
                       (NEW."State" IN ('Undoing', 'Undone') AND NEW."UndoTaxonomyRevisionId" IS NULL) OR
                       NEW."State" NOT IN ('Preview', 'Applying', 'Completed', 'Cancelled', 'Undoing', 'Undone') THEN
                        RAISE EXCEPTION 'Historical operation requires valid paired undo intent and state' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER historical_operation_integrity
                    BEFORE INSERT OR UPDATE OR DELETE ON taxonomy."HistoricalOperations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_historical_operation();
                CREATE TRIGGER historical_operation_retention
                    BEFORE TRUNCATE ON taxonomy."HistoricalOperations"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.guard_historical_operation();

                CREATE FUNCTION taxonomy.guard_historical_operation_item() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                        RAISE EXCEPTION 'Historical manifest evidence is retained' USING ERRCODE = '23514';
                    END IF;
                    IF ROW(NEW."OperationId", NEW."ItemId", NEW."ReceiptId", NEW."Store" COLLATE "C",
                           NEW."RawDescription" COLLATE "C", NEW."ItemCode" COLLATE "C", NEW."ItemRevision",
                           NEW."ReceiptRevision", NEW."ExpectedAssignmentId", NEW."PreviousCategoryId",
                           NEW."TargetCategoryId", NEW."ApplyOperationId", NEW."UndoOperationId")
                       IS DISTINCT FROM
                       ROW(OLD."OperationId", OLD."ItemId", OLD."ReceiptId", OLD."Store" COLLATE "C",
                           OLD."RawDescription" COLLATE "C", OLD."ItemCode" COLLATE "C", OLD."ItemRevision",
                           OLD."ReceiptRevision", OLD."ExpectedAssignmentId", OLD."PreviousCategoryId",
                           OLD."TargetCategoryId", OLD."ApplyOperationId", OLD."UndoOperationId") THEN
                        RAISE EXCEPTION 'Historical manifest evidence is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER historical_manifest_integrity
                    BEFORE UPDATE OR DELETE ON taxonomy."HistoricalOperationItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_historical_operation_item();
                CREATE TRIGGER historical_manifest_retention
                    BEFORE TRUNCATE ON taxonomy."HistoricalOperationItems"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.guard_historical_operation_item();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Acquire the same locks as DROP before checking, so a concurrent insert cannot
		// arrive between the retained-evidence check and the destructive DDL.
		migrationBuilder.Sql("""
                LOCK TABLE taxonomy."HistoricalOperations", taxonomy."HistoricalOperationItems" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations") OR
                       EXISTS (SELECT 1 FROM taxonomy."HistoricalOperationItems") THEN
                        RAISE EXCEPTION 'Cannot downgrade: historical reclassification evidence is retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
		migrationBuilder.DropTable(
			name: "HistoricalOperationItems",
			schema: "taxonomy");

		migrationBuilder.DropTable(
			name: "HistoricalOperations",
			schema: "taxonomy");

		migrationBuilder.Sql("""
                DROP FUNCTION taxonomy.guard_historical_operation_item();
                DROP FUNCTION taxonomy.guard_historical_operation();
                """);
	}
}
