using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAuditTables : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "DeletedAt",
			table: "Transactions",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DeletedByApiKeyId",
			table: "Transactions",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DeletedByUserId",
			table: "Transactions",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "DeletedAt",
			table: "Receipts",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DeletedByApiKeyId",
			table: "Receipts",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DeletedByUserId",
			table: "Receipts",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "DeletedAt",
			table: "ReceiptItems",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DeletedByApiKeyId",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DeletedByUserId",
			table: "ReceiptItems",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "DeletedAt",
			table: "Accounts",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DeletedByApiKeyId",
			table: "Accounts",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DeletedByUserId",
			table: "Accounts",
			type: "text",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "AuditLogs",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				EntityType = table.Column<string>(type: "text", nullable: false),
				EntityId = table.Column<string>(type: "text", nullable: false),
				Action = table.Column<string>(type: "text", nullable: false),
				ChangesJson = table.Column<string>(type: "text", nullable: false),
				ChangedByUserId = table.Column<string>(type: "text", nullable: true),
				ChangedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
				ChangedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				IpAddress = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_AuditLogs", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "AuthAuditLogs",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				EventType = table.Column<string>(type: "text", nullable: false),
				UserId = table.Column<string>(type: "text", nullable: true),
				ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
				Username = table.Column<string>(type: "text", nullable: true),
				Success = table.Column<bool>(type: "boolean", nullable: false),
				FailureReason = table.Column<string>(type: "text", nullable: true),
				IpAddress = table.Column<string>(type: "text", nullable: true),
				UserAgent = table.Column<string>(type: "text", nullable: true),
				Timestamp = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				MetadataJson = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_AuthAuditLogs", x => x.Id);
			});

		migrationBuilder.CreateIndex(
			name: "IX_AuditLogs_ChangedAt",
			table: "AuditLogs",
			column: "ChangedAt");

		migrationBuilder.CreateIndex(
			name: "IX_AuditLogs_ChangedByApiKeyId",
			table: "AuditLogs",
			column: "ChangedByApiKeyId");

		migrationBuilder.CreateIndex(
			name: "IX_AuditLogs_ChangedByUserId",
			table: "AuditLogs",
			column: "ChangedByUserId");

		migrationBuilder.CreateIndex(
			name: "IX_AuditLogs_EntityType_EntityId",
			table: "AuditLogs",
			columns: ["EntityType", "EntityId"]);

		migrationBuilder.CreateIndex(
			name: "IX_AuthAuditLogs_EventType_Timestamp",
			table: "AuthAuditLogs",
			columns: ["EventType", "Timestamp"]);

		migrationBuilder.CreateIndex(
			name: "IX_AuthAuditLogs_IpAddress",
			table: "AuthAuditLogs",
			column: "IpAddress");

		migrationBuilder.CreateIndex(
			name: "IX_AuthAuditLogs_Timestamp",
			table: "AuthAuditLogs",
			column: "Timestamp");

		migrationBuilder.CreateIndex(
			name: "IX_AuthAuditLogs_UserId_Timestamp",
			table: "AuthAuditLogs",
			columns: ["UserId", "Timestamp"]);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "AuditLogs");

		migrationBuilder.DropTable(
			name: "AuthAuditLogs");

		migrationBuilder.DropColumn(
			name: "DeletedAt",
			table: "Transactions");

		migrationBuilder.DropColumn(
			name: "DeletedByApiKeyId",
			table: "Transactions");

		migrationBuilder.DropColumn(
			name: "DeletedByUserId",
			table: "Transactions");

		migrationBuilder.DropColumn(
			name: "DeletedAt",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "DeletedByApiKeyId",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "DeletedByUserId",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "DeletedAt",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "DeletedByApiKeyId",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "DeletedByUserId",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "DeletedAt",
			table: "Accounts");

		migrationBuilder.DropColumn(
			name: "DeletedByApiKeyId",
			table: "Accounts");

		migrationBuilder.DropColumn(
			name: "DeletedByUserId",
			table: "Accounts");
	}
}
