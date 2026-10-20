using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCatalogLabelSuggestions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "LabelOnly",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.AddColumn<string>(
			name: "SuggestedDisplayLabel",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "text",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "LabelOnly",
			schema: "matching",
			table: "NormalizationAiProposals");

		migrationBuilder.DropColumn(
			name: "SuggestedDisplayLabel",
			schema: "matching",
			table: "NormalizationAiProposals");
	}
}
