using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCatalogSuggestionProvenance : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateIndex(
			name: "IX_NormalizationCatalogMembers_ItemId",
			schema: "matching",
			table: "NormalizationCatalogMembers",
			column: "ItemId",
			filter: "\"State\" = 'pending'");
		migrationBuilder.AddColumn<Guid>(
			name: "SuggestionProposalId",
			schema: "matching",
			table: "NormalizationCatalogOperations",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "SuggestionRunId",
			schema: "matching",
			table: "NormalizationCatalogOperations",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "SuggestionRunRevision",
			schema: "matching",
			table: "NormalizationCatalogOperations",
			type: "uuid",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_NormalizationCatalogMembers_ItemId",
			schema: "matching",
			table: "NormalizationCatalogMembers");
		migrationBuilder.DropColumn(
			name: "SuggestionProposalId",
			schema: "matching",
			table: "NormalizationCatalogOperations");

		migrationBuilder.DropColumn(
			name: "SuggestionRunId",
			schema: "matching",
			table: "NormalizationCatalogOperations");

		migrationBuilder.DropColumn(
			name: "SuggestionRunRevision",
			schema: "matching",
			table: "NormalizationCatalogOperations");
	}
}
