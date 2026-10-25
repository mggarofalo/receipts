using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddPurchaseClassificationHistory : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "Assignments",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptItemId = table.Column<Guid>(type: "uuid", nullable: false),
				Sequence = table.Column<long>(type: "bigint", nullable: false),
				PreviousAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
				TaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false),
				Source = table.Column<string>(type: "text", nullable: false),
				Reason = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Assignments", x => x.Id);
				table.UniqueConstraint("AK_Assignments_ReceiptItemId_Id", x => new { x.ReceiptItemId, x.Id });
				table.CheckConstraint("CK_Assignment_Sequence", "\"Sequence\" > 0");
				table.ForeignKey(
					name: "FK_Assignments_Assignments_ReceiptItemId_PreviousAssignmentId",
					columns: x => new { x.ReceiptItemId, x.PreviousAssignmentId },
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumns: new[] { "ReceiptItemId", "Id" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Assignments_Nodes_TaxonomyRevisionId_CategoryId",
					columns: x => new { x.TaxonomyRevisionId, x.CategoryId },
					principalSchema: "taxonomy",
					principalTable: "Nodes",
					principalColumns: new[] { "RevisionId", "CategoryId" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Assignments_ReceiptItems_ReceiptItemId",
					column: x => x.ReceiptItemId,
					principalSchema: "receipts",
					principalTable: "ReceiptItems",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Assignments_Revisions_TaxonomyRevisionId",
					column: x => x.TaxonomyRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "AssignmentOperations",
			schema: "taxonomy",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_AssignmentOperations", x => new { x.Actor, x.OperationId });
				table.ForeignKey(
					name: "FK_AssignmentOperations_Assignments_AssignmentId",
					column: x => x.AssignmentId,
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "CurrentAssignments",
			schema: "taxonomy",
			columns: table => new
			{
				ReceiptItemId = table.Column<Guid>(type: "uuid", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_CurrentAssignments", x => x.ReceiptItemId);
				table.ForeignKey(
					name: "FK_CurrentAssignments_Assignments_ReceiptItemId_AssignmentId",
					columns: x => new { x.ReceiptItemId, x.AssignmentId },
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumns: new[] { "ReceiptItemId", "Id" },
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_AssignmentOperations_AssignmentId",
			schema: "taxonomy",
			table: "AssignmentOperations",
			column: "AssignmentId");

		migrationBuilder.CreateIndex(
			name: "IX_Assignments_ReceiptItemId_PreviousAssignmentId",
			schema: "taxonomy",
			table: "Assignments",
			columns: new[] { "ReceiptItemId", "PreviousAssignmentId" });

		migrationBuilder.CreateIndex(
			name: "IX_Assignments_ReceiptItemId_Sequence",
			schema: "taxonomy",
			table: "Assignments",
			columns: new[] { "ReceiptItemId", "Sequence" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_Assignments_TaxonomyRevisionId_CategoryId",
			schema: "taxonomy",
			table: "Assignments",
			columns: new[] { "TaxonomyRevisionId", "CategoryId" });

		migrationBuilder.CreateIndex(
			name: "IX_CurrentAssignments_ReceiptItemId_AssignmentId",
			schema: "taxonomy",
			table: "CurrentAssignments",
			columns: new[] { "ReceiptItemId", "AssignmentId" });
		migrationBuilder.Sql("""
                CREATE TRIGGER immutable_assignments BEFORE UPDATE OR DELETE ON taxonomy."Assignments"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_assignment_operations BEFORE UPDATE OR DELETE ON taxonomy."AssignmentOperations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Classification history is retained; restore a verified backup explicitly instead.'; END $$;");
	}
}
