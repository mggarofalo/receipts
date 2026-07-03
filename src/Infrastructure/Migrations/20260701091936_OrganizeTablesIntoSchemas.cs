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
			newName: "YnabSyncRecords",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "YnabServerKnowledge",
			schema: "ynab",
			newName: "YnabServerKnowledge",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "YnabSelectedBudgets",
			schema: "ynab",
			newName: "YnabSelectedBudgets",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "YnabCategoryMappings",
			schema: "ynab",
			newName: "YnabCategoryMappings",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "YnabAccountMappings",
			schema: "ynab",
			newName: "YnabAccountMappings",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Transactions",
			schema: "receipts",
			newName: "Transactions",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Subcategories",
			schema: "library",
			newName: "Subcategories",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Receipts",
			schema: "receipts",
			newName: "Receipts",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "ReceiptItems",
			schema: "receipts",
			newName: "ReceiptItems",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptionSettings",
			schema: "matching",
			newName: "NormalizedDescriptionSettings",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "NormalizedDescriptions",
			schema: "matching",
			newName: "NormalizedDescriptions",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "ItemTemplates",
			schema: "library",
			newName: "ItemTemplates",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "ItemSimilarityEdges",
			schema: "matching",
			newName: "ItemSimilarityEdges",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "ItemEmbeddings",
			schema: "matching",
			newName: "ItemEmbeddings",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "DistinctDescriptions",
			schema: "matching",
			newName: "DistinctDescriptions",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Categories",
			schema: "library",
			newName: "Categories",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Cards",
			schema: "library",
			newName: "Cards",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AuthAuditLogs",
			schema: "audit",
			newName: "AuthAuditLogs",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AuditLogs",
			schema: "audit",
			newName: "AuditLogs",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetUserTokens",
			schema: "identity",
			newName: "AspNetUserTokens",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetUsers",
			schema: "identity",
			newName: "AspNetUsers",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetUserRoles",
			schema: "identity",
			newName: "AspNetUserRoles",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetUserLogins",
			schema: "identity",
			newName: "AspNetUserLogins",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetUserClaims",
			schema: "identity",
			newName: "AspNetUserClaims",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetRoles",
			schema: "identity",
			newName: "AspNetRoles",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "AspNetRoleClaims",
			schema: "identity",
			newName: "AspNetRoleClaims",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "ApiKeys",
			schema: "identity",
			newName: "ApiKeys",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Adjustments",
			schema: "receipts",
			newName: "Adjustments",
			newSchema: "public");

		migrationBuilder.RenameTable(
			name: "Accounts",
			schema: "library",
			newName: "Accounts",
			newSchema: "public");

		// Every table has been moved back to public above, so the six bounded-context
		// schemas Up() created are now empty and can be dropped — restoring the exact
		// pre-migration state (symmetric teardown, RECEIPTS-749).
		migrationBuilder.DropSchema(name: "ynab");
		migrationBuilder.DropSchema(name: "matching");
		migrationBuilder.DropSchema(name: "audit");
		migrationBuilder.DropSchema(name: "identity");
		migrationBuilder.DropSchema(name: "receipts");
		migrationBuilder.DropSchema(name: "library");
	}
}
