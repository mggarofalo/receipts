using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class OrganizeTablesIntoSchemas : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.EnsureSchema(
			name: "library");

		migrationBuilder.EnsureSchema(
			name: "receipts");

		migrationBuilder.EnsureSchema(
			name: "identity");

		migrationBuilder.EnsureSchema(
			name: "audit");

		migrationBuilder.EnsureSchema(
			name: "matching");

		migrationBuilder.EnsureSchema(
			name: "ynab");

		migrationBuilder.RenameTable(
			name: "YnabSyncRecords",
			newName: "YnabSyncRecords",
			newSchema: "ynab");

		migrationBuilder.RenameTable(
			name: "YnabServerKnowledge",
			newName: "YnabServerKnowledge",
			newSchema: "ynab");

		migrationBuilder.RenameTable(
			name: "YnabSelectedBudgets",
			newName: "YnabSelectedBudgets",
			newSchema: "ynab");

		migrationBuilder.RenameTable(
			name: "YnabCategoryMappings",
			newName: "YnabCategoryMappings",
			newSchema: "ynab");

		migrationBuilder.RenameTable(
			name: "YnabAccountMappings",
			newName: "YnabAccountMappings",
			newSchema: "ynab");

		migrationBuilder.RenameTable(
			name: "Transactions",
			newName: "Transactions",
			newSchema: "receipts");

		migrationBuilder.RenameTable(
			name: "Subcategories",
			newName: "Subcategories",
			newSchema: "library");

		migrationBuilder.RenameTable(
			name: "Receipts",
			newName: "Receipts",
			newSchema: "receipts");

		migrationBuilder.RenameTable(
			name: "ReceiptItems",
			newName: "ReceiptItems",
			newSchema: "receipts");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptionSettings",
			newName: "NormalizedDescriptionSettings",
			newSchema: "matching");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptions",
			newName: "NormalizedDescriptions",
			newSchema: "matching");

		migrationBuilder.RenameTable(
			name: "ItemTemplates",
			newName: "ItemTemplates",
			newSchema: "library");

		migrationBuilder.RenameTable(
			name: "ItemSimilarityEdges",
			newName: "ItemSimilarityEdges",
			newSchema: "matching");

		migrationBuilder.RenameTable(
			name: "ItemEmbeddings",
			newName: "ItemEmbeddings",
			newSchema: "matching");

		migrationBuilder.RenameTable(
			name: "DistinctDescriptions",
			newName: "DistinctDescriptions",
			newSchema: "matching");

		migrationBuilder.RenameTable(
			name: "Categories",
			newName: "Categories",
			newSchema: "library");

		migrationBuilder.RenameTable(
			name: "Cards",
			newName: "Cards",
			newSchema: "library");

		migrationBuilder.RenameTable(
			name: "AuthAuditLogs",
			newName: "AuthAuditLogs",
			newSchema: "audit");

		migrationBuilder.RenameTable(
			name: "AuditLogs",
			newName: "AuditLogs",
			newSchema: "audit");

		migrationBuilder.RenameTable(
			name: "AspNetUserTokens",
			newName: "AspNetUserTokens",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetUsers",
			newName: "AspNetUsers",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetUserRoles",
			newName: "AspNetUserRoles",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetUserLogins",
			newName: "AspNetUserLogins",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetUserClaims",
			newName: "AspNetUserClaims",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetRoles",
			newName: "AspNetRoles",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "AspNetRoleClaims",
			newName: "AspNetRoleClaims",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "ApiKeys",
			newName: "ApiKeys",
			newSchema: "identity");

		migrationBuilder.RenameTable(
			name: "Adjustments",
			newName: "Adjustments",
			newSchema: "receipts");

		migrationBuilder.RenameTable(
			name: "Accounts",
			newName: "Accounts",
			newSchema: "library");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.RenameTable(
			name: "YnabSyncRecords",
			schema: "ynab",
			newName: "YnabSyncRecords");

		migrationBuilder.RenameTable(
			name: "YnabServerKnowledge",
			schema: "ynab",
			newName: "YnabServerKnowledge");

		migrationBuilder.RenameTable(
			name: "YnabSelectedBudgets",
			schema: "ynab",
			newName: "YnabSelectedBudgets");

		migrationBuilder.RenameTable(
			name: "YnabCategoryMappings",
			schema: "ynab",
			newName: "YnabCategoryMappings");

		migrationBuilder.RenameTable(
			name: "YnabAccountMappings",
			schema: "ynab",
			newName: "YnabAccountMappings");

		migrationBuilder.RenameTable(
			name: "Transactions",
			schema: "receipts",
			newName: "Transactions");

		migrationBuilder.RenameTable(
			name: "Subcategories",
			schema: "library",
			newName: "Subcategories");

		migrationBuilder.RenameTable(
			name: "Receipts",
			schema: "receipts",
			newName: "Receipts");

		migrationBuilder.RenameTable(
			name: "ReceiptItems",
			schema: "receipts",
			newName: "ReceiptItems");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptionSettings",
			schema: "matching",
			newName: "NormalizedDescriptionSettings");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptions",
			schema: "matching",
			newName: "NormalizedDescriptions");

		migrationBuilder.RenameTable(
			name: "ItemTemplates",
			schema: "library",
			newName: "ItemTemplates");

		migrationBuilder.RenameTable(
			name: "ItemSimilarityEdges",
			schema: "matching",
			newName: "ItemSimilarityEdges");

		migrationBuilder.RenameTable(
			name: "ItemEmbeddings",
			schema: "matching",
			newName: "ItemEmbeddings");

		migrationBuilder.RenameTable(
			name: "DistinctDescriptions",
			schema: "matching",
			newName: "DistinctDescriptions");

		migrationBuilder.RenameTable(
			name: "Categories",
			schema: "library",
			newName: "Categories");

		migrationBuilder.RenameTable(
			name: "Cards",
			schema: "library",
			newName: "Cards");

		migrationBuilder.RenameTable(
			name: "AuthAuditLogs",
			schema: "audit",
			newName: "AuthAuditLogs");

		migrationBuilder.RenameTable(
			name: "AuditLogs",
			schema: "audit",
			newName: "AuditLogs");

		migrationBuilder.RenameTable(
			name: "AspNetUserTokens",
			schema: "identity",
			newName: "AspNetUserTokens");

		migrationBuilder.RenameTable(
			name: "AspNetUsers",
			schema: "identity",
			newName: "AspNetUsers");

		migrationBuilder.RenameTable(
			name: "AspNetUserRoles",
			schema: "identity",
			newName: "AspNetUserRoles");

		migrationBuilder.RenameTable(
			name: "AspNetUserLogins",
			schema: "identity",
			newName: "AspNetUserLogins");

		migrationBuilder.RenameTable(
			name: "AspNetUserClaims",
			schema: "identity",
			newName: "AspNetUserClaims");

		migrationBuilder.RenameTable(
			name: "AspNetRoles",
			schema: "identity",
			newName: "AspNetRoles");

		migrationBuilder.RenameTable(
			name: "AspNetRoleClaims",
			schema: "identity",
			newName: "AspNetRoleClaims");

		migrationBuilder.RenameTable(
			name: "ApiKeys",
			schema: "identity",
			newName: "ApiKeys");

		migrationBuilder.RenameTable(
			name: "Adjustments",
			schema: "receipts",
			newName: "Adjustments");

		migrationBuilder.RenameTable(
			name: "Accounts",
			schema: "library",
			newName: "Accounts");
	}
}
