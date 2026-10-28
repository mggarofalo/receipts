using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddOrganizationEffectManifest : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "OrganizationEffectManifests",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				PolicyVersion = table.Column<string>(type: "text", nullable: false),
				TaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				CapturedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				WriteTransaction = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()")
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_OrganizationEffectManifests", x => x.Id);
				table.UniqueConstraint("AK_OrganizationEffectManifests_Id_TaxonomyRevisionId", x => new { x.Id, x.TaxonomyRevisionId });
				table.ForeignKey(
					name: "FK_OrganizationEffectManifests_Revisions_TaxonomyRevisionId",
					column: x => x.TaxonomyRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "OrganizationEffectItems",
			schema: "taxonomy",
			columns: table => new
			{
				ManifestId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RawDescription = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ExpectedAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				TaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				TargetCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
				ApplyOperationId = table.Column<Guid>(type: "uuid", nullable: false),
				UndoOperationId = table.Column<Guid>(type: "uuid", nullable: false),
				DecisionEvidenceJson = table.Column<string>(type: "jsonb", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_OrganizationEffectItems", x => new { x.ManifestId, x.ItemId });
				table.CheckConstraint("CK_EffectItem_Operations", "\"ApplyOperationId\" <> \"UndoOperationId\"");
				table.ForeignKey(
					name: "FK_OrganizationEffectItems_Assignments_ItemId_ExpectedAssignme~",
					columns: x => new { x.ItemId, x.ExpectedAssignmentId },
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumns: new[] { "ReceiptItemId", "Id" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_OrganizationEffectItems_Nodes_TaxonomyRevisionId_TargetCate~",
					columns: x => new { x.TaxonomyRevisionId, x.TargetCategoryId },
					principalSchema: "taxonomy",
					principalTable: "Nodes",
					principalColumns: new[] { "RevisionId", "CategoryId" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_OrganizationEffectItems_OrganizationEffectManifests_Manifes~",
					columns: x => new { x.ManifestId, x.TaxonomyRevisionId },
					principalSchema: "taxonomy",
					principalTable: "OrganizationEffectManifests",
					principalColumns: new[] { "Id", "TaxonomyRevisionId" },
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "OrganizationEffectOperations",
			schema: "taxonomy",
			columns: table => new
			{
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				ManifestId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				Phase = table.Column<string>(type: "text", nullable: false, collation: "C")
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_OrganizationEffectOperations", x => x.OperationId);
				table.CheckConstraint("CK_EffectOperation_Phase", "\"Phase\" IN ('apply', 'undo')");
				table.ForeignKey(
					name: "FK_OrganizationEffectOperations_OrganizationEffectItems_Manife~",
					columns: x => new { x.ManifestId, x.ItemId },
					principalSchema: "taxonomy",
					principalTable: "OrganizationEffectItems",
					principalColumns: new[] { "ManifestId", "ItemId" },
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "OrganizationEffectOutcomes",
			schema: "taxonomy",
			columns: table => new
			{
				ManifestId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				Phase = table.Column<string>(type: "text", nullable: false, collation: "C"),
				Disposition = table.Column<string>(type: "text", nullable: false, collation: "C"),
				Reason = table.Column<string>(type: "text", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: true),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: true),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_OrganizationEffectOutcomes", x => new { x.ManifestId, x.ItemId, x.Phase });
				table.CheckConstraint("CK_EffectOutcome_Disposition", "\"Disposition\" IN ('applied', 'skipped')");
				table.CheckConstraint("CK_EffectOutcome_Evidence", "(\"Disposition\" = 'applied' AND \"AssignmentId\" IS NOT NULL AND \"ItemRevision\" IS NOT NULL AND \"ReceiptRevision\" IS NOT NULL) OR (\"Disposition\" = 'skipped' AND \"AssignmentId\" IS NULL)");
				table.CheckConstraint("CK_EffectOutcome_Phase", "\"Phase\" IN ('apply', 'undo')");
				table.ForeignKey(
					name: "FK_OrganizationEffectOutcomes_Assignments_ItemId_AssignmentId",
					columns: x => new { x.ItemId, x.AssignmentId },
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumns: new[] { "ReceiptItemId", "Id" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_OrganizationEffectOutcomes_OrganizationEffectItems_Manifest~",
					columns: x => new { x.ManifestId, x.ItemId },
					principalSchema: "taxonomy",
					principalTable: "OrganizationEffectItems",
					principalColumns: new[] { "ManifestId", "ItemId" },
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectItems_ApplyOperationId",
			schema: "taxonomy",
			table: "OrganizationEffectItems",
			column: "ApplyOperationId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectItems_ItemId_ExpectedAssignmentId",
			schema: "taxonomy",
			table: "OrganizationEffectItems",
			columns: new[] { "ItemId", "ExpectedAssignmentId" });

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectItems_ManifestId_TaxonomyRevisionId",
			schema: "taxonomy",
			table: "OrganizationEffectItems",
			columns: new[] { "ManifestId", "TaxonomyRevisionId" });

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectItems_TaxonomyRevisionId_TargetCategoryId",
			schema: "taxonomy",
			table: "OrganizationEffectItems",
			columns: new[] { "TaxonomyRevisionId", "TargetCategoryId" });

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectItems_UndoOperationId",
			schema: "taxonomy",
			table: "OrganizationEffectItems",
			column: "UndoOperationId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectManifests_TaxonomyRevisionId",
			schema: "taxonomy",
			table: "OrganizationEffectManifests",
			column: "TaxonomyRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectOperations_ManifestId_ItemId_Phase",
			schema: "taxonomy",
			table: "OrganizationEffectOperations",
			columns: new[] { "ManifestId", "ItemId", "Phase" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_OrganizationEffectOutcomes_ItemId_AssignmentId",
			schema: "taxonomy",
			table: "OrganizationEffectOutcomes",
			columns: new[] { "ItemId", "AssignmentId" });
		migrationBuilder.Sql("""
                CREATE FUNCTION taxonomy.stamp_effect_manifest() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN NEW."WriteTransaction" := txid_current(); RETURN NEW; END $$;
                CREATE TRIGGER stamp_effect_manifest BEFORE INSERT ON taxonomy."OrganizationEffectManifests"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.stamp_effect_manifest();

                CREATE FUNCTION taxonomy.guard_effect_item_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectManifests"
                        WHERE "Id" = NEW."ManifestId" AND "WriteTransaction" = txid_current()) THEN
                        RAISE EXCEPTION 'Organization effect manifest is sealed' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_effect_item_insert BEFORE INSERT ON taxonomy."OrganizationEffectItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_effect_item_insert();

                CREATE FUNCTION taxonomy.reserve_effect_operations() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO taxonomy."OrganizationEffectOperations" ("OperationId", "ManifestId", "ItemId", "Phase")
                        VALUES (NEW."ApplyOperationId", NEW."ManifestId", NEW."ItemId", 'apply'),
                               (NEW."UndoOperationId", NEW."ManifestId", NEW."ItemId", 'undo');
                    RETURN NEW;
                END $$;
                CREATE TRIGGER reserve_effect_operations AFTER INSERT ON taxonomy."OrganizationEffectItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reserve_effect_operations();
                CREATE FUNCTION taxonomy.guard_effect_operation_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectItems" i
                        JOIN taxonomy."OrganizationEffectManifests" m ON m."Id" = i."ManifestId"
                        WHERE i."ManifestId" = NEW."ManifestId" AND i."ItemId" = NEW."ItemId"
                        AND m."WriteTransaction" = txid_current()
                        AND ((NEW."Phase" = 'apply' AND NEW."OperationId" = i."ApplyOperationId")
                          OR (NEW."Phase" = 'undo' AND NEW."OperationId" = i."UndoOperationId"))) THEN
                        RAISE EXCEPTION 'Operation reservation differs from sealed intent' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_effect_operation_insert BEFORE INSERT ON taxonomy."OrganizationEffectOperations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_effect_operation_insert();
                CREATE TRIGGER immutable_effect_operations BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectOperations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER retain_effect_operations BEFORE TRUNCATE ON taxonomy."OrganizationEffectOperations"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();

                CREATE FUNCTION taxonomy.guard_effect_outcome_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE intent taxonomy."OrganizationEffectItems"%ROWTYPE;
                DECLARE applied uuid;
                BEGIN
                    SELECT * INTO intent FROM taxonomy."OrganizationEffectItems"
                        WHERE "ManifestId" = NEW."ManifestId" AND "ItemId" = NEW."ItemId";
                    IF NEW."Phase" = 'undo' THEN
                        SELECT "AssignmentId" INTO applied FROM taxonomy."OrganizationEffectOutcomes"
                            WHERE "ManifestId" = NEW."ManifestId" AND "ItemId" = NEW."ItemId"
                            AND "Phase" = 'apply' AND "Disposition" = 'applied';
                        IF applied IS NULL THEN
                            RAISE EXCEPTION 'Undo requires an applied effect' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF NEW."Disposition" = 'applied' AND NOT EXISTS (
                        SELECT 1 FROM taxonomy."Assignments" a
                        WHERE a."Id" = NEW."AssignmentId" AND a."ReceiptItemId" = NEW."ItemId"
                        AND ((NEW."Phase" = 'apply' AND a."OperationId" = intent."ApplyOperationId"
                            AND a."PreviousAssignmentId" IS NOT DISTINCT FROM intent."ExpectedAssignmentId"
                            AND a."TaxonomyRevisionId" = intent."TaxonomyRevisionId"
                            AND a."CategoryId" = intent."TargetCategoryId")
                        OR (NEW."Phase" = 'undo' AND a."OperationId" = intent."UndoOperationId"
                            AND a."PreviousAssignmentId" = applied
                            AND a."CategoryId" IS NOT DISTINCT FROM (SELECT "CategoryId" FROM taxonomy."Assignments"
                                WHERE "Id" = intent."ExpectedAssignmentId")))) THEN
                        RAISE EXCEPTION 'Outcome assignment differs from effect intent' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_effect_outcome_insert BEFORE INSERT ON taxonomy."OrganizationEffectOutcomes"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_effect_outcome_insert();

                CREATE TRIGGER immutable_effect_manifests BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectManifests"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_effect_items BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_effect_outcomes BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectOutcomes"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER retain_effect_manifests BEFORE TRUNCATE ON taxonomy."OrganizationEffectManifests"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER retain_effect_items BEFORE TRUNCATE ON taxonomy."OrganizationEffectItems"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER retain_effect_outcomes BEFORE TRUNCATE ON taxonomy."OrganizationEffectOutcomes"
                    FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		throw new NotSupportedException("Organization effect evidence is retained; roll forward instead.");
	}
}
