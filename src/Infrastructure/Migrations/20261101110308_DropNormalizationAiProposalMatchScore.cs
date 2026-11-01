using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropNormalizationAiProposalMatchScore : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "MatchScore",
			schema: "matching",
			table: "NormalizationAiProposals");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<double>(
			name: "MatchScore",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "double precision",
			nullable: true);
	}
}
