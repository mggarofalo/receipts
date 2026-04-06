using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryAndSubcategory : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "Categories",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Name = table.Column<string>(type: "text", nullable: false),
				Description = table.Column<string>(type: "text", nullable: true),
				DeletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				DeletedByUserId = table.Column<string>(type: "text", nullable: true),
				DeletedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Categories", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "Subcategories",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Name = table.Column<string>(type: "text", nullable: false),
				CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
				Description = table.Column<string>(type: "text", nullable: true),
				DeletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				DeletedByUserId = table.Column<string>(type: "text", nullable: true),
				DeletedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Subcategories", x => x.Id);
				table.ForeignKey(
					name: "FK_Subcategories_Categories_CategoryId",
					column: x => x.CategoryId,
					principalTable: "Categories",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_Categories_Name",
			table: "Categories",
			column: "Name",
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_Subcategories_CategoryId_Name",
			table: "Subcategories",
			columns: ["CategoryId", "Name"],
			unique: true,
			filter: "\"DeletedAt\" IS NULL");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "Subcategories");

		migrationBuilder.DropTable(
			name: "Categories");
	}
}
