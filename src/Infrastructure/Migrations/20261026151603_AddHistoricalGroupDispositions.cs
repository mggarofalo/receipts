using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalGroupDispositions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "HistoricalDispositionOperations",
			schema: "taxonomy",
			columns: table => new
			{
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false, collation: "C"),
				SourceHash = table.Column<string>(type: "text", nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RawDescription = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				Disposition = table.Column<string>(type: "text", nullable: true),
				ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
				ResultVersion = table.Column<long>(type: "bigint", nullable: false),
				ResultUpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalDispositionOperations", x => x.OperationId);
			});

		migrationBuilder.CreateTable(
			name: "HistoricalGroupDispositions",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false, collation: "C"),
				SourceHash = table.Column<string>(type: "text", nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				RawDescription = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				Disposition = table.Column<string>(type: "text", nullable: true),
				Version = table.Column<long>(type: "bigint", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalGroupDispositions", x => x.Id);
			});

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalGroupDispositions_Actor_SourceHash",
			schema: "taxonomy",
			table: "HistoricalGroupDispositions",
			columns: new[] { "Actor", "SourceHash" },
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "HistoricalGroupDispositions",
			schema: "taxonomy");

		migrationBuilder.DropTable(
			name: "HistoricalDispositionOperations",
			schema: "taxonomy");
	}
}
