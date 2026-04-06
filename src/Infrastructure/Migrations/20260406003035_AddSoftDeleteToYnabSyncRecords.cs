using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddSoftDeleteToYnabSyncRecords : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "CascadeDeletedByParentId",
			table: "YnabSyncRecords",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "DeletedAt",
			table: "YnabSyncRecords",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "DeletedByApiKeyId",
			table: "YnabSyncRecords",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "DeletedByUserId",
			table: "YnabSyncRecords",
			type: "text",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "CascadeDeletedByParentId",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "DeletedAt",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "DeletedByApiKeyId",
			table: "YnabSyncRecords");

		migrationBuilder.DropColumn(
			name: "DeletedByUserId",
			table: "YnabSyncRecords");
	}
}
