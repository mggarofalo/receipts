using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddRevisionedTaxonomy : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.EnsureSchema(
			name: "taxonomy");

		migrationBuilder.CreateTable(
			name: "Identities",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Identities", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "Revisions",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Sequence = table.Column<long>(type: "bigint", nullable: false),
				WriteTransaction = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()"),
				PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false),
				Reason = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				AffectedCount = table.Column<int>(type: "integer", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Revisions", x => x.Id);
				table.ForeignKey(
					name: "FK_Revisions_Revisions_PreviousRevisionId",
					column: x => x.PreviousRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "Head",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				RevisionId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Head", x => x.Id);
				table.CheckConstraint("CK_TaxonomyHead_Singleton", "\"Id\" = 1");
				table.ForeignKey(
					name: "FK_Head_Revisions_RevisionId",
					column: x => x.RevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "Nodes",
			schema: "taxonomy",
			columns: table => new
			{
				RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
				ParentId = table.Column<Guid>(type: "uuid", nullable: true),
				Name = table.Column<string>(type: "text", nullable: false, collation: "C"),
				Definition = table.Column<string>(type: "text", nullable: true),
				Archived = table.Column<bool>(type: "boolean", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Nodes", x => new { x.RevisionId, x.CategoryId });
				table.CheckConstraint("CK_TaxonomyNode_NameLength", "char_length(\"Name\") BETWEEN 1 AND 200");
				table.CheckConstraint("CK_TaxonomyNode_DefinitionLength", "char_length(\"Definition\") <= 4000");
				table.ForeignKey(
					name: "FK_Nodes_Identities_CategoryId",
					column: x => x.CategoryId,
					principalSchema: "taxonomy",
					principalTable: "Identities",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Nodes_Nodes_RevisionId_ParentId",
					columns: x => new { x.RevisionId, x.ParentId },
					principalSchema: "taxonomy",
					principalTable: "Nodes",
					principalColumns: new[] { "RevisionId", "CategoryId" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Nodes_Revisions_RevisionId",
					column: x => x.RevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "Operations",
			schema: "taxonomy",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				AffectedCount = table.Column<int>(type: "integer", nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				Reason = table.Column<string>(type: "text", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Operations", x => new { x.Actor, x.OperationId });
				table.ForeignKey(
					name: "FK_Operations_Revisions_RevisionId",
					column: x => x.RevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.InsertData(
			schema: "taxonomy",
			table: "Revisions",
			columns: new[] { "Id", "Actor", "AffectedCount", "OperationId", "PreviousRevisionId", "Reason", "RecordedAt", "Sequence", "WriteTransaction" },
			values: new object[] { new Guid("e0450000-0000-0000-0000-000000000000"), "system", 0, new Guid("e0450000-0000-0000-0000-000000000000"), null, "Empty taxonomy foundation; legacy import is separate.", new DateTimeOffset(new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 0L, 0L });

		migrationBuilder.InsertData(
			schema: "taxonomy",
			table: "Head",
			columns: new[] { "Id", "RevisionId" },
			values: new object[] { 1, new Guid("e0450000-0000-0000-0000-000000000000") });

		migrationBuilder.CreateIndex(
			name: "IX_Head_RevisionId",
			schema: "taxonomy",
			table: "Head",
			column: "RevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_Nodes_CategoryId",
			schema: "taxonomy",
			table: "Nodes",
			column: "CategoryId");

		migrationBuilder.CreateIndex(
			name: "IX_Nodes_RevisionId_Name",
			schema: "taxonomy",
			table: "Nodes",
			columns: new[] { "RevisionId", "Name" },
			unique: true,
			filter: "NOT \"Archived\" AND \"ParentId\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_Nodes_RevisionId_ParentId_Name",
			schema: "taxonomy",
			table: "Nodes",
			columns: new[] { "RevisionId", "ParentId", "Name" },
			unique: true,
			filter: "NOT \"Archived\" AND \"ParentId\" IS NOT NULL");

		migrationBuilder.CreateIndex(
			name: "IX_Operations_RevisionId",
			schema: "taxonomy",
			table: "Operations",
			column: "RevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_Revisions_PreviousRevisionId",
			schema: "taxonomy",
			table: "Revisions",
			column: "PreviousRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_Revisions_Sequence",
			schema: "taxonomy",
			table: "Revisions",
			column: "Sequence",
			unique: true);

		migrationBuilder.Sql("""
                CREATE FUNCTION taxonomy.stamp_revision_transaction() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN NEW."WriteTransaction" := txid_current(); RETURN NEW; END $$;
                CREATE TRIGGER stamp_revision_transaction BEFORE INSERT ON taxonomy."Revisions"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.stamp_revision_transaction();
                CREATE FUNCTION taxonomy.reject_sealed_snapshot_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM taxonomy."Revisions" WHERE "Id" = NEW."RevisionId" AND "WriteTransaction" = txid_current()) THEN
                        RAISE EXCEPTION 'Taxonomy snapshot is sealed';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER sealed_snapshot_insert BEFORE INSERT ON taxonomy."Nodes"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_sealed_snapshot_insert();
                CREATE FUNCTION taxonomy.reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Taxonomy history is immutable'; END $$;
                CREATE TRIGGER immutable_revisions BEFORE UPDATE OR DELETE ON taxonomy."Revisions" FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_nodes BEFORE UPDATE OR DELETE ON taxonomy."Nodes" FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_identities BEFORE UPDATE OR DELETE ON taxonomy."Identities" FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_operations BEFORE UPDATE OR DELETE ON taxonomy."Operations" FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		throw new NotSupportedException("Taxonomy history is retained. Roll forward or restore a verified backup instead of dropping immutable revisions.");
	}
}
