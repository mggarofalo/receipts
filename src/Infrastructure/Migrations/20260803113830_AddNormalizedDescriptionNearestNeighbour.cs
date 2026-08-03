using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddNormalizedDescriptionNearestNeighbour : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// RECEIPTS-873: record the near-miss that pushed a description into PendingReview so the
		// Review Queue can explain itself. Both columns are nullable and deliberately NOT
		// backfilled — the score alone survived on ReceiptItems, but the neighbour it was measured
		// against was never persisted, so there is nothing to reconstruct from. Existing rows are
		// requeued and re-resolved from scratch under RECEIPTS-882 instead.
		migrationBuilder.AddColumn<Guid>(
			name: "NearestNeighbourId",
			schema: "matching",
			table: "NormalizedDescriptions",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<double>(
			name: "NearestNeighbourSimilarity",
			schema: "matching",
			table: "NormalizedDescriptions",
			type: "double precision",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizedDescriptions_NearestNeighbourId",
			schema: "matching",
			table: "NormalizedDescriptions",
			column: "NearestNeighbourId");

		// SetNull, not Cascade: merging a canonical row away must not delete the unrelated pending
		// rows that happened to name it as their nearest neighbour. Losing the reference degrades
		// those rows to "no comparison recorded", which is the honest outcome.
		migrationBuilder.AddForeignKey(
			name: "FK_NormalizedDescriptions_NormalizedDescriptions_NearestNeighb~",
			schema: "matching",
			table: "NormalizedDescriptions",
			column: "NearestNeighbourId",
			principalSchema: "matching",
			principalTable: "NormalizedDescriptions",
			principalColumn: "Id",
			onDelete: ReferentialAction.SetNull);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "FK_NormalizedDescriptions_NormalizedDescriptions_NearestNeighb~",
			schema: "matching",
			table: "NormalizedDescriptions");

		migrationBuilder.DropIndex(
			name: "IX_NormalizedDescriptions_NearestNeighbourId",
			schema: "matching",
			table: "NormalizedDescriptions");

		migrationBuilder.DropColumn(
			name: "NearestNeighbourId",
			schema: "matching",
			table: "NormalizedDescriptions");

		migrationBuilder.DropColumn(
			name: "NearestNeighbourSimilarity",
			schema: "matching",
			table: "NormalizedDescriptions");
	}
}
