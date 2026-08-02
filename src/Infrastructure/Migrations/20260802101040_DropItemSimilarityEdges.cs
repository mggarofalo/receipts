using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropItemSimilarityEdges : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "ItemSimilarityEdges",
			schema: "matching");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "ItemSimilarityEdges",
			schema: "matching",
			columns: table => new
			{
				DescA = table.Column<string>(type: "text", nullable: false),
				DescB = table.Column<string>(type: "text", nullable: false),
				ComputedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				Score = table.Column<double>(type: "double precision", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ItemSimilarityEdges", x => new { x.DescA, x.DescB });
				table.CheckConstraint("CK_ItemSimilarityEdges_CanonicalOrder", "\"DescA\" < \"DescB\"");
				table.ForeignKey(
					name: "FK_ItemSimilarityEdges_DistinctDescriptions_DescA",
					column: x => x.DescA,
					principalSchema: "matching",
					principalTable: "DistinctDescriptions",
					principalColumn: "Description",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "FK_ItemSimilarityEdges_DistinctDescriptions_DescB",
					column: x => x.DescB,
					principalSchema: "matching",
					principalTable: "DistinctDescriptions",
					principalColumn: "Description",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_ItemSimilarityEdges_DescB",
			schema: "matching",
			table: "ItemSimilarityEdges",
			column: "DescB");

		migrationBuilder.CreateIndex(
			name: "IX_ItemSimilarityEdges_Score",
			schema: "matching",
			table: "ItemSimilarityEdges",
			column: "Score");
	}
}
