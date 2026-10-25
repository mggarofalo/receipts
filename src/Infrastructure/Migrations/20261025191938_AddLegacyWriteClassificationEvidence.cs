using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddLegacyWriteClassificationEvidence : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "LegacyCategory",
			schema: "taxonomy",
			table: "Assignments",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "LegacyResolution",
			schema: "taxonomy",
			table: "Assignments",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "LegacySubcategory",
			schema: "taxonomy",
			table: "Assignments",
			type: "text",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		throw new InvalidOperationException(
			"Downgrade would destroy immutable classification evidence. Preserve the history and apply a forward fix or restore a verified compatible backup.");
	}
}
